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
- **`RequestConfirmation.sessionId` is required and must be non-null.** A null or unknown `sessionId` fails with `SESSION_REQUIRED`. Core never creates a provider session as a side effect of asking for a confirmation.
- The destination shown in the `ConfirmationRequest` is the destination that is validated at send time. A destination change after issuance invalidates the confirmation.
- Editing the draft on the device invalidates the confirmation, because the confirmation binds the text hash. The device must call `RequestConfirmation` again, and the new confirmation renders the edited text back to the user.

**Session resolution before confirmation.** The product requires that a provider session persists until the user chooses New Session, so session creation must be an explicit user action and must never be reachable through a stale or buggy client sending `sessionId: null`:

1. The device selects a destination and reads `Destination.resumableSessionId`.
2. If a resumable session exists, the device sends `ResumeSessionRequest` and uses the returned `sessionId`.
3. If none exists, or the user wants a fresh context, the user presses New Session and the device sends `NewSession { destinationId }`; Core creates the session and returns its `sessionId` in `SessionUpdate`.
4. Only then may the device send `RequestConfirmation` with that non-null `sessionId`.

Core validates that the `sessionId` exists, belongs to `destinationId`, and is not `Closed`; otherwise `SESSION_NOT_FOUND`. The first prompt to a never-used destination therefore costs one explicit New Session action, which is exactly the visible choice the invariant demands.

**Concurrent `NewSession`.** Two safeguards, in this order:

- Envelope idempotency: a repeated `NewSession` with the same `id` returns the original `SessionUpdate` with `duplicate: true` (ADR-002 section 4).
- Coalescing: Core holds a per-`destinationId` lock and coalesces `NewSession` requests with *different* ids arriving within 2 s of a successful creation, from any device, returning the same `sessionId` with `coalesced: true`. This stops a double tap, a retry after a slow response, or two devices acting at once from silently creating two provider sessions.

A deliberate second session on the same destination is available through New Session again after the 2 s window, and the Sessions screen lists both.

### 2. Confirmation token

Core generates, holds in memory, and issues:

```
ConfirmationRequest.payload {
  confirmationId,          // ULID
  promptText,              // exactly what the device must display
  promptHash,              // base64url SHA-256 of NFC UTF-8 bytes of promptText
  destinationId, destinationLabel, providerId,
  sessionId,               // required, never null; resolved by NewSession or ResumeSessionRequest
  intendedMode,            // Send | Queue | Steer
  deviceId,                // the only device permitted to confirm
  issuedAtUtc, expiresAtUtc,   // expiry = issuedAt + 120 s
  nonce,                   // base64url 16 random bytes
  mac                      // base64url HMAC-SHA256(K_conf, canonical bytes below)
}
```

MAC input is the canonical concatenation (`docs/specs/WIRE_PROTOCOL.md` section 3):

```
canonical("optimus-confirm-v1",
  confirmationId, promptHash, destinationId, providerId, sessionId,
  intendedMode, deviceId, issuedAtUtc, expiresAtUtc, nonce)
```

Every field is present; `sessionId` has no null case, so there is no empty-string substitution and no encoding ambiguity.

`K_conf` is 32 CSPRNG bytes generated at each Core start, held in memory only, and zeroed on shutdown (ADR-003 section 8). It never leaves Core, and no adapter, runner, or device ever sees it.

The MAC exists so that server-side pending state is verifiable rather than merely looked up. Core also keeps an authoritative pending map keyed by `confirmationId`, so a valid MAC alone is insufficient: the entry must exist and be unconsumed.

### 3. Validation of `SendAction`

The device sends back:

```
SendAction.payload {
  confirmationId,
  displayedText,        // the exact string the device rendered
  destinationId,
  sessionId,            // required, echoed from the ConfirmationRequest
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

**What check 6 does and does not prove.** Echoing `displayedText` is a binding check, not an attestation of pixels. It proves that the bytes the client submitted for sending are the bytes the confirmation covers, which catches the realistic and dangerous class of honest client defects: a stale draft buffer, a race between an edit and a confirmation, a truncation or normalization difference between the rendered string and the submitted one, and a client that mixes up two concurrent drafts. It cannot prove what a compromised client painted on screen; a client that has been subverted can render anything while echoing the correct bytes.

To make the check meaningful rather than ceremonial, clients are required to build one **immutable confirmation view model** per `ConfirmationRequest`, containing the exact `promptText`, `destinationLabel`, and session title. The rendered UI and the echoed `displayedText` must both read from that single frozen value, and no code path may re-derive, re-format, or re-wrap the text between render and echo. T005, T022, and the client review checklist enforce this.

Server-side exact-content validation stays regardless: Core recomputes the hash and never sends text the confirmation did not cover. A compromised paired client presenting misleading UI is recorded as a residual risk in `docs/security/THREAT_MODEL.md`, not as a threat this check defeats.

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

- Sessions persist as **metadata only** in `%LOCALAPPDATA%\Optimus\state\sessions.json`. The persisted fields are exactly those listed above plus `title`. No prompt bodies, no queued prompt text, no queue previews, no provider output, no audio. `queueDepth` is recomputed at runtime and is written as `0`.
- The `title` is the one exception that derives from prompt text: it is the first 60 characters of the first prompt, user-editable, and is covered by the history retention setting and by Clear History, which resets titles to `Session <n>`. This is called out rather than hidden, because a title is user-visible text derived from a prompt.
- Sessions survive Core restarts and are re-attached with `ResumeSessionRequest`. A provider that cannot resume returns `SessionNotFound`; Core then marks the session `Closed` and tells the user, rather than silently creating a new one.
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
- **The queue is in-memory only and never persisted.** Queued prompt text and the 80-character preview exist in Core memory and in device UI state, and nowhere else. Nothing about a queued item is written to `sessions.json`, to history, or to any other file.
- **A Core restart drops every queue.** Session metadata still resumes, and the affected sessions carry `queueDroppedCount` in the first `SessionUpdate` after restart so each device can show "3 queued prompts were discarded when the service restarted". The count is a number, not the text. Devices must not re-send from a local copy; a dropped item requires a fresh draft and a fresh confirmation.

This is the deliberate resolution of a conflict between two product requirements. Durable queues would mean writing confirmed prompt bodies to disk, which contradicts the storage rule above and the privacy position in `docs/security/THREAT_MODEL.md` that prompt content is not persisted beyond the opt-in text history. Losing a queue on a restart costs the user a re-dictation of at most five prompts, in a situation that is already visible to them. Persisting prompt bodies would cost every user a permanent on-disk record of their prompts. If durable queues later prove product-critical, they require an ADR amendment defining opt-in, encryption, retention, deletion, crash-dump, backup, and threat-model behavior; they must not arrive as an implementation convenience.

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
  nonce, mac                // HMAC-SHA256(K_conf, canonical("optimus-approval-v1",
                            //   approvalId, sessionId, operationHash, riskLevel,
                            //   issuedAtUtc, expiresAtUtc, nonce))
}
```

Response:

```
ApprovalResponse.payload {
  approvalId, decision,      // Approve | Deny
  operationHash,             // echoed
  mac,                       // echoed
  decidedAtUtc,
  attestationKind,           // DeviceSignature | LocalVerification | None
  attestation                // required when riskLevel == Consequential
}
```

**Two attestation kinds, with different and honestly stated strength.**

`DeviceSignature` (Android, and any future device with a hardware-backed, user-authentication-bound key) is an ECDSA P-256 with SHA-256 signature by `optimus_approval_v1` (ADR-003 section 8), produced through a `BiometricPrompt`-bound `CryptoObject`, over:

```
canonical("optimus-approval-response-v1",
  approvalId, decision, operationHash, decidedAtUtc, approvalNonce, serverChallenge)
```

`approvalNonce` is the `nonce` from the `ApprovalRequest`, binding the signature to that specific issuance. `serverChallenge` is the connection challenge from ADR-003 section 6, binding it to this connection. Together they make the signature useless on any other approval or any other connection.

`LocalVerification` (desktop) is **not a signature**. `UserConsentVerifier` returns a result enumeration and performs no cryptographic operation over Optimus data, so the desktop cannot produce anything comparable. The payload is `{ approvalId, verifiedAtUtc, method: "WindowsHello" }`, an assertion by the Shell that a Windows Hello verification succeeded for that approval. Core accepts it only on a loopback connection authenticated by the local capability token, only for the named `approvalId`, only within 60 s of `verifiedAtUtc`, and only once. Its trust is bounded by the same-user boundary, exactly like the capability token itself, and `docs/security/THREAT_MODEL.md` records that as a residual risk rather than a defence.

Which kinds Core accepts is policy `approvals.consequentialAttestationPolicy`:

| Value | Accepts | Default when |
| --- | --- | --- |
| `AllowLocalVerification` | `DeviceSignature` and `LocalVerification` | No phone is paired |
| `RequireDeviceSignature` | `DeviceSignature` only | From the moment a phone is paired |

Under `RequireDeviceSignature`, a desktop `LocalVerification` for a `Consequential` approval is refused with `APPROVAL_ATTESTATION_NOT_PERMITTED` and the UI directs the decision to the phone. The policy is shown during pairing and is changeable in tray settings; changing it is recorded in the event history.

Validation order, first failure wins:

| # | Check | Failure code |
| --- | --- | --- |
| 1 | Connection authenticated, device not revoked | `UNAUTHENTICATED` |
| 2 | `approvalId` pending and unconsumed | `APPROVAL_UNKNOWN` / `APPROVAL_ALREADY_USED` |
| 3 | `now <= expiresAtUtc` | `APPROVAL_EXPIRED` |
| 4 | `mac` recomputes and matches, constant-time | `APPROVAL_INVALID` |
| 5 | Echoed `operationHash` equals the issued value | `APPROVAL_CONTENT_MISMATCH` |
| 6 | The provider still reports the same pending operation with the same hash | `APPROVAL_OPERATION_CHANGED` |
| 7 | `riskLevel == Consequential` implies `attestationKind != None` | `APPROVAL_ATTESTATION_REQUIRED` |
| 8 | `attestationKind` is permitted by the active policy | `APPROVAL_ATTESTATION_NOT_PERMITTED` |
| 9 | `DeviceSignature`: verifies against the stored `approvalPublicKey`, covers this `approvalId`, `decision`, `operationHash`, `approvalNonce`, and this connection challenge, and `abs(now - decidedAtUtc) <= 60 s`. `LocalVerification`: loopback connection, matching `approvalId`, `abs(now - verifiedAtUtc) <= 60 s`, not previously accepted | `APPROVAL_ATTESTATION_INVALID` |

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

- **Server-side text only, no `displayedText` echo.** Simpler, but it cannot detect a client that submits a different string from the one the confirmation covers, which is the realistic defect class (stale buffers, edit races, normalization drift). Rejected, with the claim narrowed in section 3 to what the echo actually proves.
- **Confirmation as a bare server-side identifier with no MAC.** Rejected: the MAC makes the binding self-describing, lets rejection reasons be precise, and protects against pending-map bugs that would otherwise silently accept mismatched bindings.
- **Long-lived confirmations (10 minutes or more).** Rejected: a draft the user read ten minutes ago is not a current intent. 120 s matches the observed read-and-confirm interaction and is renewable with one tap.
- **Auto-queueing when a session is busy.** Rejected: it converts a decision the user must make into an inference, which the invariants forbid.
- **Allowing `RequestConfirmation` with a null `sessionId` to imply a new session.** Rejected: it makes provider-session creation reachable from a stale or buggy client without the user ever choosing New Session, which defeats the requirement that sessions persist until the user explicitly ends them. The cost is one explicit action on first use of a destination, which is the visible choice the product wants anyway.
- **Durable, disk-backed prompt queues.** Rejected: the only way a queue survives a restart is by persisting confirmed prompt bodies, which contradicts both the session-storage rule and the privacy position that prompt content is not persisted beyond the opt-in history. Queues are in-memory and are reported as dropped.
- **Re-validating queued confirmations for expiry at dequeue.** Rejected: it would silently drop confirmed intent after the user was told the prompt was queued.
- **Biometric on every approval.** Rejected: it trains users to authenticate reflexively. Reserving it for `Consequential` keeps the prompt meaningful.
- **Describing the Windows Hello result as an attestation token.** Rejected as factually wrong: `UserConsentVerifier` signs nothing. The desktop kind is named `LocalVerification`, its trust boundary is stated, and once a phone is paired the default policy routes consequential decisions to the phone where a real signature exists.
- **Trusting provider-declared risk level.** Rejected outright: it lets untrusted output downgrade a gate.

## Consequences

- Every send is traceable to one consumed confirmation with a content hash, which makes the invariant auditable in the event history.
- Editing a draft costs one extra round trip for a new confirmation. Acceptable and explicit.
- Broadcasting approvals to all devices means two devices can race; the loser sees a clear resolved state rather than an error dialog.
- Because `K_conf` is per-Core-start, a Core restart during confirmation forces a re-confirmation. Intended.
- Adapters cannot be given a convenience "send text" entry point without breaking the type contract, which keeps future adapter work honest.
- The first prompt to a new destination requires an explicit New Session tap. This is one extra action, and it is the action the invariant is about.
- A Core restart discards queued prompts. Users see a count, not a silent loss, and session context still resumes.
- Desktop consequential approvals are weaker than phone approvals, and the product says so instead of implying parity.

## Verification

- T019 unit tests cover each of the ten `SendAction` checks with a dedicated negative case, plus: concurrent double-send admits exactly one; tampered `displayedText` by one character fails check 6; destination swap fails check 7; confirmation from a second device fails check 8; three failed attempts invalidate the entry.
- T019 tests assert `RequestConfirmation` with a null, unknown, closed, or wrong-destination `sessionId` fails with `SESSION_REQUIRED` or `SESSION_NOT_FOUND`, and that no provider session is created as a side effect of any confirmation request.
- T018 tests assert that two `NewSession` requests with different envelope ids within 2 s produce one session with `coalesced: true`, and that the same envelope id produces `duplicate: true`.
- T005 and T022 tests assert that the rendered confirmation string and the echoed `displayedText` come from the same immutable view model instance, and that no re-formatting occurs between them.
- T019 test asserts a `Send` confirmation cannot be redeemed as `Steer` or `Queue`.
- T018 tests cover queue FIFO, `QUEUE_FULL`, queue drop on `Failed`, single in-flight steer, `STEER_MISSED` requiring a new confirmation, and session metadata persistence across a Core restart.
- T018 and T024 tests assert that `sessions.json` after a restart contains no queued prompt text and no preview, that every queue is empty, and that each affected session reports a non-zero `queueDroppedCount` exactly once.
- T023 instrumented test asserts a `Consequential` approval with `attestationKind: None` is rejected with `APPROVAL_ATTESTATION_REQUIRED`; that a `DeviceSignature` produced for a different `approvalId`, a different `approvalNonce`, or a different connection challenge fails check 9; and that a replayed `ApprovalResponse` gets `APPROVAL_ALREADY_USED`.
- T020 and T023 tests assert `LocalVerification` is refused on a non-loopback connection and refused entirely under `RequireDeviceSignature` with `APPROVAL_ATTESTATION_NOT_PERMITTED`.
- T016 and T017 adapter tests feed hostile provider output containing control characters, bidirectional overrides, Markdown, and instruction-like text, and assert no state transition occurs and the rendered and spoken strings are sanitized and capped.
- T029 release audit greps the codebase for any adapter entry point accepting prompt text without a `ConfirmationReceipt`.

## Related documents

- `docs/specs/SENDER_ADAPTER.md`
- `docs/specs/WIRE_PROTOCOL.md`
- `docs/security/THREAT_MODEL.md`
