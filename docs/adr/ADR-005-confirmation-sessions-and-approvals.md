# ADR-005: Prompt confirmation, destination binding, sessions, Queue, Steer, questions, and approvals

- Status: Accepted
- Date: 2026-09-04
- Owner: Claude Opus 5
- Related tasks: T001, T016, T017, T018, T019, T022, T023

## Context

Three product invariants meet in this ADR:

- Never send a prompt without a visible explicit destination and user confirmation.
- Never infer or silently change the destination.
- Approval requests are authenticated, expiring, replay-protected, and bound to the displayed operation.

A confirmation therefore has to mean, provably, "this exact text, to this exact destination, in this exact session, approved by this exact device, at this exact moment, once". Anything weaker allows a race, a stale draft, or a substituted destination to send something the user did not read.

Core is the single confirmation authority (ADR-001 section 3). Devices render and echo; they never decide.

## Decision

### 1. Prompt lifecycle

```
Capture -> Transcript -> CleanedDraft -> [user selects destination] -> ConfirmationRequest
       -> [user confirms on the device] -> SendAction -> validation -> ConfirmedPrompt -> adapter
```

- A `PromptDraft` alone is never sendable. It carries no confirmation material.
- Core issues a `ConfirmationRequest` only when a destination is explicitly set for the draft. If no destination is set, `RequestConfirmation` fails with `DESTINATION_REQUIRED`. Core never picks a default, never reuses "the last one" implicitly, and never infers a destination from prompt content.
- The destination shown in the `ConfirmationRequest` is the destination that is validated at send time. A destination change after issuance invalidates the confirmation.
- Editing the draft on the device invalidates the confirmation, because the confirmation binds the text hash. The device must call `RequestConfirmation` again, and the new confirmation renders the edited text back to the user.

### 2. Confirmation token

Core generates, holds in memory, and issues:

```
ConfirmationRequest.payload {
  confirmationId,          // ULID
  promptText,              // exactly what the device must display
  promptHash,              // base64url SHA-256 of NFC UTF-8 bytes of promptText
  destinationId, destinationLabel, providerId,
  sessionId,               // null for a new session
  intendedMode,            // Send | Queue | Steer
  deviceId,                // the only device permitted to confirm
  issuedAtUtc, expiresAtUtc,   // expiry = issuedAt + 120 s
  nonce,                   // base64url 16 random bytes
  mac                      // base64url HMAC-SHA256(K_conf, canonical bytes below)
}
```

MAC input is the canonical concatenation (`docs/specs/WIRE_PROTOCOL.md` section 3):

```
"optimus-confirm-v1" || confirmationId || promptHash || destinationId || providerId ||
(sessionId ?? "") || intendedMode || deviceId || issuedAtUtc || expiresAtUtc || nonce
```

`K_conf` is 32 CSPRNG bytes generated at each Core start, held in memory only, and zeroed on shutdown (ADR-003 section 7). It never leaves Core, and no adapter, runner, or device ever sees it.

The MAC exists so that server-side pending state is verifiable rather than merely looked up. Core also keeps an authoritative pending map keyed by `confirmationId`, so a valid MAC alone is insufficient: the entry must exist and be unconsumed.

### 3. Validation of `SendAction`

The device sends back:

```
SendAction.payload {
  confirmationId,
  displayedText,        // the exact string the device rendered
  destinationId,
  sessionId,            // or null
  mode,                 // Send | Queue | Steer
  mac,                  // echoed unmodified
  confirmedAtUtc
}
```

Core validates in this order, rejecting at the first failure and consuming nothing on failure except the attempt counter:

| # | Check | Failure code |
| --- | --- | --- |
| 1 | Connection is authenticated and not revoked | `UNAUTHENTICATED` |
| 2 | `confirmationId` exists in the pending map | `CONFIRMATION_UNKNOWN` |
| 3 | The entry is unconsumed | `CONFIRMATION_ALREADY_USED` |
| 4 | `now <= expiresAtUtc` | `CONFIRMATION_EXPIRED` |
| 5 | `mac` matches a recomputation over the stored entry, constant-time compare | `CONFIRMATION_INVALID` |
| 6 | `SHA-256(NFC(displayedText)) == promptHash`, constant-time compare | `CONFIRMATION_CONTENT_MISMATCH` |
| 7 | `destinationId`, `providerId`, `sessionId`, and `mode` equal the issued values | `CONFIRMATION_BINDING_MISMATCH` |
| 8 | The authenticated connection `deviceId` equals the issued `deviceId` | `CONFIRMATION_DEVICE_MISMATCH` |
| 9 | The destination still exists and is reachable | `DESTINATION_UNAVAILABLE` |
| 10 | Session state permits `mode` (section 5) | `SESSION_BUSY` or `SESSION_NOT_FOUND` |

Only after all ten checks pass does Core mark the entry consumed (atomically, under the pending map lock, so two concurrent `SendAction` messages cannot both pass check 3) and construct a `ConfirmedPrompt` for the adapter.

Check 6 is the reason `displayedText` is echoed rather than trusted from server state: it proves the device rendered the same bytes Core is about to send. A device that shows text A and echoes text B fails, and a compromised device cannot make Core send text the confirmation did not cover.

Additional rules:

- Attempts are limited to 3 per `confirmationId`; the fourth invalidates the entry.
- Expired and consumed entries are retained for 10 minutes so a replay gets a precise code instead of `CONFIRMATION_UNKNOWN`, then dropped.
- Rejections are recorded in the local event history with the code and no text.
- Revoking a device invalidates all of its pending confirmations immediately.

### 4. `ConfirmedPrompt` handed to an adapter

```
ConfirmedPrompt {
  operationId,        // ULID, the adapter idempotency key
  sessionId,          // resolved, never null at this point
  destinationId, providerId,
  text,               // the exact confirmed text
  mode,               // Send | Queue | Steer
  originDeviceId,     // for voice routing
  receipt             // ConfirmationReceipt { confirmationId, promptHash, validatedAtUtc }
}
```

Adapters must reject a `ConfirmedPrompt` whose `receipt` is absent. There is no adapter entry point that accepts unconfirmed text. This is the type-level expression of the invariant.

### 5. Sessions

```
AgentSession {
  sessionId,               // ULID, Optimus-owned
  providerId, destinationId,
  providerSessionRef,      // opaque provider identifier
  state,                   // Idle | Busy | AwaitingAnswer | AwaitingApproval | Failed | Closed
  createdAtUtc, lastActivityAtUtc,
  title,                   // derived from the first prompt, user-editable
  queueDepth
}
```

- Sessions persist as **text metadata only** in `%LOCALAPPDATA%\Optimus\state\sessions.json`. No prompt bodies, no provider output, no audio.
- Sessions survive Core restarts and are re-attached with `ResumeSession`. A provider that cannot resume returns `SessionNotFound`; Core then marks the session `Closed` and tells the user, rather than silently creating a new one.
- A session is discarded only by an explicit `NewSession` action. Nothing else, including a provider crash, a reconnect, or a destination refresh, ends a session.
- Destination changes never migrate a session. Selecting a different destination creates or resumes a session on that destination; the previous session remains listed and resumable.

State transitions:

| From | Event | To |
| --- | --- | --- |
| `Idle` | `Send` accepted | `Busy` |
| `Busy` | turn completes | `Idle` |
| `Busy` | agent asks a question | `AwaitingAnswer` |
| `Busy` | agent requests approval | `AwaitingApproval` |
| `AwaitingAnswer` | answer confirmed and sent | `Busy` |
| `AwaitingApproval` | approval decided | `Busy` |
| any active | provider error or CLI exit | `Failed` |
| any | `NewSession` or user close | `Closed` |

`AwaitingAnswer` and `AwaitingApproval` count as busy for admission purposes.

### 6. Queue and Steer

When the target session is not `Idle`, a `Send` is refused with `SESSION_BUSY` and `details.allowedActions` listing `Queue` and `Steer`. The UI then presents exactly those two choices plus Cancel. Core never auto-selects one.

**Queue.**

- FIFO per session, maximum depth 5 (`maxQueuedPromptsPerSession`). A sixth attempt fails with `QUEUE_FULL`.
- The confirmation is validated at enqueue time. Once enqueued, the item carries its `ConfirmationReceipt` and is **not** re-checked for expiry, because the user already confirmed that exact text for that exact session; expiring it later would silently drop a confirmed intent.
- Queued items are visible and individually removable from the Sessions screen. Removing one is not a send.
- On session `Failed` or `Closed`, the queue is discarded and the user is told how many items were dropped.
- Queue survives a Core restart only if the session resumes; otherwise it is dropped with a notice. Queued item text is held in memory and in the session state file, subject to the same retention setting as history.

**Steer.**

- Delivered into the running turn through `ISenderAdapter.SteerActiveSessionAsync`.
- Exactly one steer may be in flight per session. A second attempt fails with `STEER_IN_FLIGHT`.
- Steer requires its own confirmation with `intendedMode = Steer`; a confirmation issued for `Send` cannot be redeemed as a steer (check 7).
- If the provider reports that the turn ended before the steer landed, Core reports `STEER_MISSED` and offers to send the same text as a normal prompt, which requires a **new** confirmation.

**Cancel.** `CancelRequest` targets a session turn. It cancels the provider turn through `ISenderAdapter.CancelAsync`, drops the in-flight steer, and leaves the queue intact unless the user also chooses to clear it.

### 7. Agent questions

- A provider question arrives as `AgentEvent { kind: Question, questionId, text, options? }`, moves the session to `AwaitingAnswer`, is displayed, and is spoken.
- An answer is a prompt. It goes through the same draft, confirmation, and validation path with `intendedMode = Send` and `questionId` bound into the confirmation entry.
- The destination is not inferred: the answer inherits the session destination, which is displayed in the confirmation card exactly like any other send. The invariant requires a visible explicit destination, not a re-selection.
- Selecting a provider-offered option is still a confirmation: the option label becomes the prompt text and is displayed for confirmation. There is no one-tap path that bypasses the gate.
- Questions expire after 30 minutes into `Failed` for that turn, and the user is told.

### 8. Approvals

```
ApprovalRequest.payload {
  approvalId, sessionId, providerId, destinationId,
  title, detail,            // provider-supplied, sanitized (section 9)
  riskLevel,                // Low | Elevated | Consequential
  operationHash,            // base64url SHA-256 over the canonical operation description from the provider
  issuedAtUtc, expiresAtUtc,   // expiry = issuedAt + 180 s
  nonce, mac                // HMAC-SHA256(K_conf, "optimus-approval-v1" || approvalId || sessionId || operationHash || riskLevel || issuedAtUtc || expiresAtUtc || nonce)
}
```

Response:

```
ApprovalResponse.payload {
  approvalId, decision,      // Approve | Deny
  operationHash,             // echoed
  mac,                       // echoed
  decidedAtUtc,
  attestation                // required when riskLevel == Consequential
}
```

`attestation` is an Ed25519 signature by the Android `optimus_approval_v1` biometric-bound key (ADR-003 section 7), or a Windows Hello `UserConsentVerifier` success token equivalent on the desktop, over:

```
"optimus-approval-response-v1" || approvalId || decision || operationHash || decidedAtUtc || exporter
```

Validation order, first failure wins:

| # | Check | Failure code |
| --- | --- | --- |
| 1 | Connection authenticated, device not revoked | `UNAUTHENTICATED` |
| 2 | `approvalId` pending and unconsumed | `APPROVAL_UNKNOWN` / `APPROVAL_ALREADY_USED` |
| 3 | `now <= expiresAtUtc` | `APPROVAL_EXPIRED` |
| 4 | `mac` recomputes and matches, constant-time | `APPROVAL_INVALID` |
| 5 | Echoed `operationHash` equals the issued value | `APPROVAL_CONTENT_MISMATCH` |
| 6 | The provider still reports the same pending operation with the same hash | `APPROVAL_OPERATION_CHANGED` |
| 7 | `riskLevel == Consequential` implies a valid `attestation` bound to this connection exporter and within 60 s | `APPROVAL_ATTESTATION_REQUIRED` / `APPROVAL_ATTESTATION_INVALID` |

Then the entry is consumed atomically and the decision is forwarded.

Additional rules:

- Approvals are broadcast to every connected device of the user so an approval can be handled from the phone while the PC is locked, but each approval is consumable exactly once; the losing device is told `APPROVAL_ALREADY_USED` and its card resolves.
- An expired approval is denied to the provider, never approved by default.
- `riskLevel` is assigned by the adapter from the provider operation class, not by the provider text. The mapping is in `docs/specs/SENDER_ADAPTER.md` section 7. Anything that writes outside the workspace, executes a shell command, installs a dependency, touches credentials, performs a network write, or performs a Git push is `Consequential`.
- Background notifications on Android carry no operation detail beyond the title until the device is unlocked and the biometric prompt has succeeded.

### 9. Provider output is untrusted input

Provider text (`AgentEvent`, question text, approval `title` and `detail`) is treated as hostile data:

- Rendered as plain text only. No HTML, no Markdown link resolution, no image loading, no clickable actions derived from content.
- Control characters other than newline and tab are stripped; bidirectional-override characters are stripped; the result is capped at 4 KiB for display and 600 characters for speech.
- Provider output can never: set or change a destination, create or consume a confirmation, trigger a send, mark an approval approved, change `riskLevel`, or dismiss a UI gate. These transitions are only reachable from an authenticated device message.
- A provider event that claims a prompt was sent does not create local session state; Core's own record is authoritative.

### 10. Voice routing and event speech

- `VoiceSummary` carries `targetDeviceId`, set to the `originDeviceId` of the `ConfirmedPrompt` that started the turn. Playback happens on that device only.
- If the origin device is offline, the summary is not rerouted; it is stored as a text event and spoken only if that device reconnects within 5 minutes.
- Spoken transitions: planning begins (first only), tests pass, tests fail, question asked, approval requested, failure, and final completion. Repeated planning events and ordinary file edits are visual only.
- Coalescing: events of the same kind within 5 s collapse into one summary. Speech is rate-limited to one utterance per 4 s per session, with a queue depth of 2; overflow drops the older of two adjacent same-kind summaries.

## Alternatives considered

- **Server-side text only, no `displayedText` echo.** Simpler, but it cannot detect a device that displayed different text from what Core holds, which is the exact failure the invariant is written against. Rejected.
- **Confirmation as a bare server-side identifier with no MAC.** Rejected: the MAC makes the binding self-describing, lets rejection reasons be precise, and protects against pending-map bugs that would otherwise silently accept mismatched bindings.
- **Long-lived confirmations (10 minutes or more).** Rejected: a draft the user read ten minutes ago is not a current intent. 120 s matches the observed read-and-confirm interaction and is renewable with one tap.
- **Auto-queueing when a session is busy.** Rejected: it converts a decision the user must make into an inference, which the invariants forbid.
- **Re-validating queued confirmations for expiry at dequeue.** Rejected: it would silently drop confirmed intent after the user was told the prompt was queued.
- **Biometric on every approval.** Rejected: it trains users to authenticate reflexively. Reserving it for `Consequential` keeps the prompt meaningful.
- **Trusting provider-declared risk level.** Rejected outright: it lets untrusted output downgrade a gate.

## Consequences

- Every send is traceable to one consumed confirmation with a content hash, which makes the invariant auditable in the event history.
- Editing a draft costs one extra round trip for a new confirmation. Acceptable and explicit.
- Broadcasting approvals to all devices means two devices can race; the loser sees a clear resolved state rather than an error dialog.
- Because `K_conf` is per-Core-start, a Core restart during confirmation forces a re-confirmation. Intended.
- Adapters cannot be given a convenience "send text" entry point without breaking the type contract, which keeps future adapter work honest.

## Verification

- T019 unit tests cover each of the ten `SendAction` checks with a dedicated negative case, plus: concurrent double-send admits exactly one; tampered `displayedText` by one character fails check 6; destination swap fails check 7; confirmation from a second device fails check 8; three failed attempts invalidate the entry.
- T019 test asserts a `Send` confirmation cannot be redeemed as `Steer` or `Queue`.
- T018 tests cover queue FIFO, `QUEUE_FULL`, queue drop on `Failed`, single in-flight steer, `STEER_MISSED` requiring a new confirmation, and session persistence across a Core restart.
- T023 instrumented test asserts a `Consequential` approval without biometric attestation is rejected with `APPROVAL_ATTESTATION_REQUIRED`, and that a replayed `ApprovalResponse` gets `APPROVAL_ALREADY_USED`.
- T016 and T017 adapter tests feed hostile provider output containing control characters, bidirectional overrides, Markdown, and instruction-like text, and assert no state transition occurs and the rendered and spoken strings are sanitized and capped.
- T029 release audit greps the codebase for any adapter entry point accepting prompt text without a `ConfirmationReceipt`.

## Related documents

- `docs/specs/SENDER_ADAPTER.md`
- `docs/specs/WIRE_PROTOCOL.md`
- `docs/security/THREAT_MODEL.md`
