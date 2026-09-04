# Optimus device wire protocol, version 1.0

- Status: Accepted
- Owner: Claude Opus 5
- Governing decisions: `docs/adr/ADR-002-device-protocol-and-compatibility.md`, `docs/adr/ADR-003-pairing-authentication-and-secrets.md`, `docs/adr/ADR-005-confirmation-sessions-and-approvals.md`
- Implemented by: T003 (`Optimus.Contracts`, Android `:core:protocol`), T004 (endpoint)

This document is normative for every message that crosses the device boundary. Both the Android application and the desktop Shell are devices; they speak the identical protocol (`docs/adr/ADR-001-process-and-subsystem-boundaries.md` section 2).

Keywords: **must**, **must not**, **should**, **may**.

## 1. Transport summary

| Property | Value |
| --- | --- |
| Endpoint | `wss://<host>:<port>/v1/device` |
| Pairing endpoint | `wss://<host>:<port>/v1/pair`, carrying `PairChallenge`, `PairRequest`, and `PairResult`, whose fields and proofs are normative in ADR-003 section 4 |
| Subprotocol | `optimus.v1` |
| TLS | 1.3 only, SPKI-pinned self-signed certificate |
| Control frames | WebSocket text, UTF-8 JSON envelope (section 2) |
| Audio frames | WebSocket binary, 16-byte header + PCM (section 4) |
| Version | `1.0`; `MAJOR` in the path, `MINOR` negotiated in `Hello`/`Welcome` |

## 2. Control envelope

```json
{
  "v": "1.0",
  "type": "<MessageType>",
  "id": "<ULID>",
  "corr": "<ULID or null>",
  "seq": 1,
  "ts": "2026-09-04T11:02:31.482Z",
  "payload": { }
}
```

Rules are defined in ADR-002 section 2. Summary of the invariants an implementation must enforce:

- `id` is unique per sender per connection; `seq` is monotonic per direction and never resets while a `deviceSessionId` lives.
- Unknown properties are ignored, **except** inside `ConfirmationRequest`, `SendAction`, `ApprovalRequest`, and `ApprovalResponse`, where an unknown property is `VALIDATION_FAILED`.
- Unknown `type` from device to server yields `UNKNOWN_MESSAGE_TYPE` without closing; from server to device it is ignored after logging.
- Maximum text frame is 256 KiB.

## 3. Canonical encoding for signatures and MACs

Every MAC or signature in this protocol is computed over a canonical byte string, never over transmitted JSON. The canonical form is:

1. Take the ordered field list given by the message definition.
2. Encode each field as UTF-8 bytes of its string form: strings as-is in Unicode NFC; integers in base-10 with no leading zeros; instants as RFC 3339 UTC with exactly millisecond precision (`yyyy-MM-ddTHH:mm:ss.fffZ`); byte arrays as base64url without padding; `null` as an empty byte sequence.
3. Join fields with the single byte `0x1F` (unit separator).
4. Prefix with the domain-separation label given by the message definition, followed by `0x1F`.

Implementations **must** ship the canonicalizer as a single shared function per platform and **must** validate it against the vectors in `tests/protocol-vectors/canonical/`.

Hashes: `promptHash` and `operationHash` are `SHA-256` over Unicode NFC UTF-8 bytes of the covered string, rendered base64url without padding. All equality comparisons on MACs, hashes, signatures, and tokens **must** be constant-time.

## 4. Audio framing

Binary frame header, little-endian:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| 0 | 2 | magic | `0x4F 0x41` |
| 2 | 1 | frameVersion | `0x01` |
| 3 | 1 | flags | bit0 `final`, bit1 `discontinuity`, bits 2-7 reserved zero |
| 4 | 4 | streamId | uint32; device streams use `0x0000_0001`-`0x7FFF_FFFF`, server (TTS) streams use `0x8000_0000`-`0xFFFF_FFFF` |
| 8 | 4 | sequence | uint32, starts at 0 |
| 12 | 4 | sampleCount | uint32 |
| 16 | n | pcm | signed 16-bit LE, mono, 16 000 Hz |

- Nominal frame is 20 ms (320 samples, 640 bytes PCM).
- Maximum binary frame is 64 KiB.
- Reserved flag bits **must** be zero on send and ignored on receive.
- An invalid magic, version, or unknown `streamId` produces `AUDIO_FRAME_INVALID`; three in one stream abort it with `AudioAborted`.

## 5. Message catalogue

Direction key: `D2S` device to server, `S2D` server to device.

### 5.1 Session and connection

#### `Challenge` (S2D, first message on every connection)

Core sends this immediately after the WebSocket opens, before the client sends anything. It carries the per-connection anti-replay binding that every signature covers (ADR-003 section 6).

| Field | Type | Notes |
| --- | --- | --- |
| `serverChallenge` | string | base64url 32 CSPRNG bytes; single-use; bound to this connection |
| `serverTimeUtc` | instant | Lets a client detect its own clock skew before signing |

The challenge is consumed by the first `Hello`. A second `Hello` on the same connection is `4400`.

#### `Hello` (D2S, first client message)

| Field | Type | Notes |
| --- | --- | --- |
| `protocolMajor` | int | Must equal 1 |
| `protocolMinorMax` | int | Highest minor the client supports |
| `clientBuild` | string | `platform/version+build` |
| `platform` | enum | `Android` \| `WindowsShell` |
| `resumeDeviceSessionId` | string? | For resume |
| `lastServerSeq` | int? | For resume replay |
| `attestationSupport` | enum | `DeviceSignature` \| `LocalVerification` \| `None`; what this device can produce for a `Consequential` approval. Core validates the claim at approval time and never trusts it alone |
| `keyBacking` | enum? | Android only: `StrongBox` \| `Tee`; displayed in the PC device list |
| `auth` | object | ADR-003 section 6: `deviceId`, `timestampUtc`, `clientNonce`, `serverChallenge` (echoed), `signature`; or `{ deviceId: "local-shell", localToken, serverChallenge }` on loopback |

`signature` is ECDSA P-256 with SHA-256, DER, base64url, over `canonical("optimus-hello-v1", deviceId, protocolMajor, timestampUtc, clientNonce, serverChallenge, spkiFingerprint)`.

Errors: `4401` on invalid auth, a challenge that Core did not issue on this connection, or an already-consumed challenge; `4403` revoked; `4406` major mismatch; `4400` malformed.

#### `Welcome` (S2D, response to `Hello`)

| Field | Type | Notes |
| --- | --- | --- |
| `protocolVersion` | string | Negotiated `MAJOR.MINOR` |
| `serverBuild` | string | |
| `deviceSession` | `DeviceSession` | Section 5.2 |
| `resumed` | bool | Whether replay follows |
| `serverTimeUtc` | instant | |
| `limits` | object | ADR-002 section 8 |
| `serverAuth` | object | `serverNonce`, and `signature` (ECDSA P-256 with SHA-256, DER, base64url) over `canonical("optimus-welcome-v1", deviceId, clientNonce, serverNonce, serverChallenge)` |

#### `DeviceSession` (S2D, in `Welcome` and on change)

The device's connection identity and current capabilities.

| Field | Type | Notes |
| --- | --- | --- |
| `deviceSessionId` | string | ULID; stable across resume |
| `deviceId` | string | Authenticated device identity |
| `deviceName` | string | |
| `platform` | enum | `Android` \| `WindowsShell` |
| `pairedAtUtc` | instant | |
| `capabilities` | string[] | Subset of `capture`, `playback`, `approve`, `approveConsequential`, `settings` |
| `establishedAtUtc` | instant | |
| `resumeWindowMs` | int | 300000 |

`capabilities` are computed by Core, not claimed by the device. `approveConsequential` is present only when the device's `attestationSupport` is a kind the active `approvals.consequentialAttestationPolicy` accepts (ADR-005 section 8), and it is revalidated at approval time regardless of what this list said.

#### `Ping` / `Pong` (both directions)

`Ping.payload { nonce }`, `Pong.payload { nonce }`. Server pings every 10 s; 30 s without traffic is dead.

#### `Bye` (S2D)

`{ reason, closeCode }` sent immediately before a deliberate close so the device can render a cause.

#### `Error` (both directions)

`{ code, message, retryable, retryAfterMs, details }` per ADR-002 section 7. `message` **must not** contain transcript, draft, provider output, paths, tokens, or host names.

### 5.2 Status and destinations

#### `ServiceStatus` (S2D, on connect, on change, at most 1 Hz)

| Field | Type | Notes |
| --- | --- | --- |
| `state` | enum | `Starting` \| `Ready` \| `SetupExclusive` \| `Degraded` \| `Stopping` |
| `captureEnabled` | bool | False in `SetupExclusive` or when ASR failed |
| `runners` | array | `{ kind, state, modelId, degradedReason? }` for `asr`, `cleanup`, `tts` |
| `voiceEnabled` | bool | |
| `cleanupEnabled` | bool | False means drafts are raw transcripts |
| `activeDestinationId` | string? | The explicit current selection, or null |
| `notices` | array | `{ code, severity, text }`, text is app-authored, never provider output |

#### `Destination` and `DestinationList` (S2D)

`Destination`:

| Field | Type | Notes |
| --- | --- | --- |
| `destinationId` | string | Stable per provider + workspace |
| `providerId` | string | `claude-code` \| `codex` |
| `label` | string | User-visible, for example `claude-code · D:\repo\optimus` |
| `workspacePath` | string? | Displayed truncated; never sent in `Error.message` |
| `state` | enum | `Available` \| `Starting` \| `Unavailable` |
| `unavailableReason` | enum? | `NotInstalled` \| `Starting` \| `AuthRequired` \| `Unreachable` \| `ProviderError`. A coded value, never provider text. `AuthRequired` means the user must authenticate in the provider's own tool; Optimus never collects the credential |
| `resumableSessionId` | string? | Most recent resumable session on this destination, or null when the destination has never had one. Informational: the device must still send `ResumeSessionRequest` or `NewSession` to obtain a usable `sessionId` |

`DestinationList.payload { destinations: Destination[], discoveredAtUtc }`. Sent on connect, after `DiscoverDestinations`, and on change.

#### `SetDestination` (D2S)

`{ destinationId }`. Sets the explicit active destination for this device. Response is `ServiceStatus` with the new `activeDestinationId`. Core **must not** set `activeDestinationId` on its own at any time; a destination that becomes `Unavailable` while active is reported and the active selection is cleared to null, never replaced.

#### `RefreshDestinations` (D2S)

`{ }`. Triggers `ISenderAdapter.DiscoverDestinationsAsync` on all registered providers. Rate limited to 1 per 5 s per device.

### 5.3 Capture and drafting

#### `StartCapture` (D2S)

| Field | Type | Notes |
| --- | --- | --- |
| `mode` | enum | `HoldToTalk` \| `TapToggle` |
| `sampleRateHz` | int | Must be 16000 |
| `inputDeviceLabel` | string? | Diagnostics only |

Response `CaptureStarted { streamId, maxUtteranceSeconds }`, or `Error` with `SERVICE_BUSY_SETUP`, `ASR_UNAVAILABLE`, or `CAPTURE_ALREADY_ACTIVE`.

Accepting `StartCapture` does two things in Core (ADR-004 section 4): it opens the GPU interactive window, preempting any speech or background GPU job and barring their admission until the draft is delivered; and it requests the ASR lease immediately, so decoding can begin while the key is still held. Frames that arrive before the lease is granted are buffered in Core, bounded by `capture.maxPreLeaseBufferMs` (2000 ms).

There is no minimum utterance duration. A short utterance is transcribed like any other; an utterance that contains no speech is rejected by the runner's voice-activity verdict, not by a clock (see `EndCapture`).

#### `AudioFrame` (D2S, binary)

Section 4. Not a JSON message. Frames belong to the `streamId` returned by `CaptureStarted`.

#### `EndCapture` (D2S)

`{ streamId, reason }` where `reason` is `UserReleased` \| `UserCancelled` \| `MaxDurationReached`. `UserCancelled` discards everything and produces no draft.

If the ASR runner reports `speechDetected: false` for the utterance, Core answers `Error` with `NO_SPEECH_DETECTED`, discards the audio, produces no transcript and no draft, and closes the GPU interactive window. Devices render this as a return to `idle`, not as an error banner.

This is a decision about what the audio contained, made by the runner that decoded it. Duration is never used to infer intent: a 150 ms "run" or "stop" is transcribed, drafted, confirmed, and counted in the G2 and G3 measurement population exactly like a ten-second instruction (ADR-004 section 4).

#### `AudioAborted` (S2D)

`{ streamId, code }` where `code` is `Reconnected` \| `FrameInvalid` \| `RateLimited` \| `RunnerFailed` \| `LeaseUnavailable`. The device must discard local capture state; no draft will follow. `LeaseUnavailable` means Core buffered more than `capture.maxPreLeaseBufferMs` of audio without obtaining the ASR lease, which indicates a stuck GPU slot rather than a device problem.

#### `Transcript` (S2D)

| Field | Type | Notes |
| --- | --- | --- |
| `streamId` | int | |
| `text` | string | Raw ASR output after glossary and normalization |
| `isFinal` | bool | Partials may be sent for display; only the final is used downstream |
| `confidence` | number? | 0-1, informational |
| `durationMs` | int | Utterance duration |
| `latencyMs` | int | Release to this message, for diagnostics |

#### `PromptDraft` (S2D)

| Field | Type | Notes |
| --- | --- | --- |
| `draftId` | string | ULID |
| `streamId` | int | |
| `rawText` | string | The final transcript |
| `text` | string | The cleaned draft, or `rawText` when cleanup is unavailable |
| `cleanupApplied` | bool | |
| `cleanupUnavailableReason` | string? | Coded, present when `cleanupApplied` is false |
| `edits` | array | `{ kind, from, to }` where `kind` is `Filler` \| `Repetition` \| `Punctuation` \| `Casing` \| `Identifier` \| `Glossary`; used for the diff view |
| `truncated` | bool | True when the utterance hit `maxUtteranceSeconds` |
| `latencyMs` | int | Release to this message |

A `PromptDraft` is **not** sendable. It carries no confirmation material.

#### `EditDraft` (D2S)

`{ draftId, text }`. Replaces the draft text with user-edited text. Invalidates any outstanding confirmation for that draft. Response is an updated `PromptDraft` with `cleanupApplied: false` and `edits: []`.

### 5.4 Confirmation and sending

#### `RequestConfirmation` (D2S)

| Field | Type | Notes |
| --- | --- | --- |
| `draftId` | string | |
| `destinationId` | string | **Required.** Absent or null yields `DESTINATION_REQUIRED` |
| `sessionId` | string | **Required and non-null.** Must name an existing, non-`Closed` session on `destinationId`. Null or absent yields `SESSION_REQUIRED`; unknown, closed, or belonging to another destination yields `SESSION_NOT_FOUND`. Core never creates a provider session from this message (ADR-005 section 1) |
| `intendedMode` | enum | `Send` \| `Queue` \| `Steer` |

#### `ConfirmationRequest` (S2D)

Fields and MAC input are normative in ADR-005 section 2. Canonical label `optimus-confirm-v1`, field order:

```
confirmationId, promptHash, destinationId, providerId, sessionId, intendedMode, deviceId, issuedAtUtc, expiresAtUtc, nonce
```

The device **must** build one immutable confirmation view model from this message, render `promptText`, `destinationLabel`, and the session title from it, and echo `displayedText` from that same frozen value. No re-formatting, re-wrapping, or re-derivation may occur between rendering and echoing. What this check does and does not prove is stated in ADR-005 section 3.

#### `SendAction` (D2S)

Fields per ADR-005 section 3. Validation is the ten ordered checks in that section. Strict validation applies: an unknown property is `VALIDATION_FAILED`.

#### `SendResult` (S2D, `corr` = the `SendAction` id)

| Field | Type | Notes |
| --- | --- | --- |
| `accepted` | bool | |
| `mode` | enum | The mode actually applied; equals the requested mode or the request failed |
| `sessionId` | string | Resolved session |
| `operationId` | string | Adapter idempotency key |
| `queuePosition` | int? | Present for `Queue` |
| `duplicate` | bool | True when this was a deduped repeat of an earlier `id` |

A failed send returns `Error` with a code from section 8 and `retryable: false`.

#### `CancelRequest` (D2S)

`{ sessionId, target }` where `target` is `Turn` \| `Steer` \| `Queue` \| `Capture`. Response is `SessionUpdate`.

### 5.5 Sessions

#### `SessionList` (S2D)

`{ sessions: AgentSession[] }` per ADR-005 section 5, plus `queued: [{ sessionId, position, preview, queuedAtUtc }]` where `preview` is the first 80 characters of the confirmed text.

Queued prompt content is in-memory only; neither `preview` nor queued prompt text is ever written to disk. After a Core restart `queued` is always empty, and each affected session carries `queueDroppedCount` from the durable per-session `pendingQueueDepth` integer (ADR-005 section 6). That field appears in the first `SessionList` or `SessionUpdate` a given device receives for that session in this Core run, and is omitted from every later message to that device.

#### `SessionUpdate` (S2D)

| Field | Type | Notes |
| --- | --- | --- |
| `session` | `AgentSession` | |
| `queued` | array? | Same shape as in `SessionList` |
| `changeReason` | enum | `Created` \| `Resumed` \| `TurnStarted` \| `TurnCompleted` \| `QuestionRaised` \| `ApprovalRaised` \| `Cancelled` \| `Failed` \| `Closed` \| `QueueChanged` \| `QueueDropped` |
| `duplicate` | bool | True when this reply was produced by envelope-id deduplication |
| `coalesced` | bool | True when a `NewSession` was coalesced into an existing creation within 2 s (ADR-005 section 1) |
| `queueDroppedCount` | int? | The durable `pendingQueueDepth` recovered at startup. Present in the first message about this session that each connecting device receives in this Core run, and omitted thereafter. Because the durable record is written before an enqueue is acknowledged and after a dequeue is accepted, this value is never lower than the number of prompts actually lost, and after a crash may exceed it by one (ADR-005 section 6) |

Sent on every session state transition.

#### `NewSession` (D2S)

`{ destinationId }`. The only action that creates a session, and the only action that discards an existing provider session context. Response `SessionUpdate` carrying the new `sessionId`.

A device must call this (or `ResumeSessionRequest`) before it can request a confirmation, because `RequestConfirmation.sessionId` is required. Concurrency: repeats of the same envelope `id` return the original result with `duplicate: true`; different ids for the same `destinationId` within 2 s of a successful creation return that same session with `coalesced: true` (ADR-005 section 1).

#### `ResumeSessionRequest` (D2S)

`{ sessionId }`. Response `SessionUpdate`, or `Error` `SESSION_NOT_FOUND` when the provider cannot resume.

### 5.6 Agent events and voice

#### `AgentEvent` (S2D)

| Field | Type | Notes |
| --- | --- | --- |
| `eventId` | string | ULID |
| `sessionId` | string | |
| `kind` | enum | `PlanningStarted` \| `ToolStarted` \| `FileEdited` \| `TestsPassed` \| `TestsFailed` \| `Question` \| `ApprovalRequested` \| `Output` \| `Failed` \| `Completed` |
| `kindFallbackText` | string | Renderable text for a kind the client does not know (ADR-002 section 6) |
| `text` | string | Sanitized provider text, at most 4096 characters |
| `questionId` | string? | Present when `kind` is `Question` |
| `options` | string[]? | Provider-offered answers, sanitized; selecting one still requires confirmation |
| `occurredAtUtc` | instant | |
| `coalescedCount` | int | 1 unless events were merged |
| `speak` | bool | Whether Core will emit a `VoiceSummary` for this event |

Provider text is untrusted (ADR-005 section 9): plain text only, control characters and bidirectional overrides stripped, no link or markup resolution.

#### `ApprovalRequest` (S2D)

Fields and MAC input are normative in ADR-005 section 8. Canonical label `optimus-approval-v1`, field order:

```
approvalId, sessionId, operationHash, riskLevel, issuedAtUtc, expiresAtUtc, nonce
```

#### `ApprovalResponse` (D2S)

Fields per ADR-005 section 8. Strict validation applies.

| Field | Type | Notes |
| --- | --- | --- |
| `approvalId` | string | |
| `decision` | enum | `Approve` \| `Deny` |
| `operationHash` | string | Echoed unmodified |
| `mac` | string | Echoed unmodified |
| `decidedAtUtc` | instant | |
| `attestationKind` | enum | `DeviceSignature` \| `LocalVerification` \| `None` |
| `attestation` | object? | Required when `riskLevel` is `Consequential`; shape depends on `attestationKind` |

`attestationKind: "DeviceSignature"` carries `{ signature }`: ECDSA P-256 with SHA-256, DER, base64url, by the device's `approvalPublicKey`, over canonical label `optimus-approval-response-v1`, field order:

```
approvalId, decision, operationHash, decidedAtUtc, approvalNonce, serverChallenge
```

`approvalNonce` is the `nonce` from the `ApprovalRequest`; `serverChallenge` is this connection's challenge. Together they prevent reuse on another approval or another connection.

`attestationKind: "LocalVerification"` carries `{ approvalId, verifiedAtUtc, method: "WindowsHello" }` and **is not a signature**. It is an assertion by the desktop Shell that a Windows Hello verification succeeded, accepted only on a loopback connection, only for the named approval, only within 60 s, and only when the active policy permits it. Its trust boundary is stated in ADR-005 section 8 and `docs/security/THREAT_MODEL.md`.

#### `ApprovalResolved` (S2D)

`{ approvalId, resolution, resolvedByDeviceId?, resolvedAtUtc }` where `resolution` is `Approved` \| `Denied` \| `Expired` \| `Superseded`. Broadcast to all devices so a card resolves everywhere.

#### `VoiceSummary` (S2D)

| Field | Type | Notes |
| --- | --- | --- |
| `summaryId` | string | ULID |
| `sessionId` | string | |
| `targetDeviceId` | string | Only this device plays it |
| `text` | string | At most 600 characters, sanitized |
| `priority` | enum | `Normal` \| `Interrupting` (approval and failure) |
| `streamId` | int? | Present when audio follows as binary frames |
| `expiresAtUtc` | instant | Not played after this time |

Audio arrives as binary frames on `streamId` in the server range. A device with `playback` absent from its capabilities receives the text and does not receive audio.

#### `VoiceControl` (D2S)

`{ action }` where `action` is `Mute` \| `Unmute` \| `StopCurrent`. Affects only the sending device.

### 5.7 Privacy and history

#### `HistoryQuery` (D2S) / `HistoryPage` (S2D)

`{ sinceUtc?, beforeUtc?, limit<=200, cursor? }` returns `{ entries: [{ entryId, occurredAtUtc, kind, sessionId?, text }], nextCursor? }`. Entries are text only. Audio is never referenced.

#### `ClearHistory` (D2S)

`{ scope }` where `scope` is `All` \| `Session` \| `OlderThan`, with the required parameter. Response `HistoryCleared { removedCount }`. This is destructive and irreversible; the device **must** confirm with the user before sending it.

## 6. Canonical sequences

### 6.0 Connect

```
                                 S2D Challenge{serverChallenge}
D2S Hello{auth{serverChallenge, signature}}
                                 S2D Welcome{protocolVersion, deviceSession, serverAuth}
                                 S2D ServiceStatus / DestinationList / SessionList
```

### 6.1 Warm-path send

```
D2S StartCapture                 S2D CaptureStarted{streamId}
                                 (Core preempts TTS/background work and takes the ASR lease)
D2S AudioFrame x N (binary)      (decoded as they arrive, once the lease is held)
D2S EndCapture{UserReleased}
                                 S2D Transcript{isFinal:true}
                                 S2D PromptDraft{draftId}
D2S SetDestination               S2D ServiceStatus{activeDestinationId}
D2S NewSession{destinationId}    S2D SessionUpdate{session{sessionId}, changeReason:"Created"}
   (or ResumeSessionRequest{sessionId} when Destination.resumableSessionId is set)
D2S RequestConfirmation{draftId, destinationId, sessionId, intendedMode:"Send"}
                                 S2D ConfirmationRequest{confirmationId, mac}
D2S SendAction{confirmationId, displayedText, sessionId, mac}
                                 S2D SendResult{accepted:true, sessionId}
                                 S2D SessionUpdate{state:Busy}
                                 S2D AgentEvent... / ApprovalRequest... / VoiceSummary...
                                 S2D SessionUpdate{state:Idle}
```

`NewSession` appears once per destination, on first use or when the user deliberately starts fresh. Every later prompt to that destination reuses the returned `sessionId`.

### 6.2 Busy session

```
D2S SendAction                   S2D Error{SESSION_BUSY, details.allowedActions:["Queue","Steer"]}
D2S RequestConfirmation{intendedMode:"Queue"}
                                 S2D ConfirmationRequest
D2S SendAction{mode:"Queue"}     S2D SendResult{mode:"Queue", queuePosition:1}
```

### 6.3 Consequential approval from the phone

```
                                 S2D ApprovalRequest{riskLevel:"Consequential", operationHash, nonce}
                                 S2D VoiceSummary{priority:"Interrupting"}
(BiometricPrompt succeeds; the bound CryptoObject signs with optimus_approval_v1)
D2S ApprovalResponse{decision:"Approve", attestationKind:"DeviceSignature", attestation{signature}}
                                 S2D ApprovalResolved{resolution:"Approved"} (broadcast)
```

The desktop equivalent sends `attestationKind:"LocalVerification"`, which Core accepts only on loopback and only under the `AllowLocalVerification` policy; otherwise it answers `APPROVAL_ATTESTATION_NOT_PERMITTED` and the decision must be made on the phone.

### 6.4 Reconnect and resume

```
(socket drops mid-capture)
                                 S2D Challenge{serverChallenge}     (new connection, new challenge)
D2S Hello{resumeDeviceSessionId, lastServerSeq, auth{serverChallenge, signature}}
                                 S2D Welcome{resumed:true}
                                 S2D AudioAborted{code:"Reconnected"}
                                 S2D <replayed messages with seq > lastServerSeq>
```

A `Hello` captured from the previous connection cannot be replayed here: it echoes a challenge that was consumed and will never be issued again.

## 7. Device-visible state machine

The desktop widget and the phone Talk screen render exactly these states, derived from protocol messages:

| State | Entered by | Left by |
| --- | --- | --- |
| `idle` | `Welcome`, `SessionUpdate{Idle}` | `CaptureStarted` |
| `listening` | `CaptureStarted` | `EndCapture` or `AudioAborted` |
| `transcribing` | `EndCapture` sent | `Transcript{isFinal}` |
| `cleaning` | `Transcript{isFinal}` | `ConfirmationRequest`, `AudioAborted`, or `Error` |
| `awaitingConfirmation` | `ConfirmationRequest` | `SendResult`, expiry, or `EditDraft` |
| `sending` | `SendAction` sent | `SendResult` or `Error` |
| `busy` | `SessionUpdate{Busy}` | `SessionUpdate{Idle}`, `Failed` |
| `approval` | `ApprovalRequest` | `ApprovalResolved` |
| `completed` | `AgentEvent{Completed}` | any new action |
| `error` | `Error`, `Bye`, socket loss | user dismissal or recovery |

These are the ten states named in `PROJECT_PLAN.md`; this specification introduces no additional user-visible state.

Two clarifications inside `cleaning`, which spans from the final transcript to the issued confirmation:

- `PromptDraft` does not end `cleaning`; it changes what `cleaning` renders. The card shows the draft, the raw transcript, the destination picker, and the session picker, and the user acts there. Destination selection (`SetDestination`) and session resolution (`NewSession` or `ResumeSessionRequest`) both happen in this state, because `RequestConfirmation` requires a non-null `sessionId`.
- An utterance the runner judged to contain no speech produces `Error{NO_SPEECH_DETECTED}` instead of a `Transcript`, so the device returns from `listening` straight to `idle` with no visible error. Utterance duration never causes this.

Devices **must not** invent transitions. In particular, a device **must not** move from `cleaning` to `sending` without an intervening `ConfirmationRequest` and an explicit user action, and **must not** reach `awaitingConfirmation` without a destination and a session the user chose.

## 8. Error code registry

Codes are stable within major version 1.

### Connection and protocol

| Code | Meaning | Retryable |
| --- | --- | --- |
| `UNSUPPORTED_VERSION` | Major mismatch | no |
| `UNAUTHENTICATED` | Missing or invalid auth material | no |
| `DEVICE_REVOKED` | Device record revoked | no |
| `VALIDATION_FAILED` | Envelope or strict-payload validation failed | no |
| `UNKNOWN_MESSAGE_TYPE` | Unrecognized `type` | no |
| `RATE_LIMITED` | Limit exceeded; see `retryAfterMs` | yes |
| `INTERNAL_ERROR` | Unexpected server fault | yes |

### Capture and inference

| Code | Meaning | Retryable |
| --- | --- | --- |
| `CAPTURE_ALREADY_ACTIVE` | A stream is already open for this device | no |
| `AUDIO_FRAME_INVALID` | Bad header or unknown stream | no |
| `ASR_UNAVAILABLE` | ASR runner failed | yes |
| `CLEANUP_UNAVAILABLE` | Cleanup runner failed; draft is the raw transcript | yes |
| `TTS_UNAVAILABLE` | TTS runner failed | yes |
| `SERVICE_BUSY_SETUP` | Core is in `SetupExclusive` | yes |
| `UTTERANCE_TOO_LONG` | Exceeded `maxUtteranceSeconds` | no |
| `NO_SPEECH_DETECTED` | The ASR runner's voice-activity verdict over the captured audio was negative; no transcript and no draft. Never inferred from duration | no |
| `GPU_SLOT_STUCK` | A GPU job could not be evicted within `W_preempt` (60 ms) and the slot could not be granted without overlap (ADR-004 section 4) | yes |

### Destination and session

| Code | Meaning | Retryable |
| --- | --- | --- |
| `DESTINATION_REQUIRED` | No explicit destination selected | no |
| `DESTINATION_UNAVAILABLE` | Destination not reachable | yes |
| `SESSION_REQUIRED` | `RequestConfirmation` arrived without a `sessionId`; send `NewSession` or `ResumeSessionRequest` first | no |
| `SESSION_NOT_FOUND` | Unknown, closed, unresumable, or belonging to a different destination | no |
| `PROVIDER_AUTH_REQUIRED` | The provider CLI is not authenticated. The user must authenticate in the provider's own tool; Optimus never collects provider credentials | no |
| `SESSION_BUSY` | Turn in progress; see `details.allowedActions` | no |
| `QUEUE_FULL` | Queue depth limit reached | no |
| `STEER_IN_FLIGHT` | A steer is already pending | no |
| `STEER_MISSED` | Turn ended before the steer landed | no |

### Confirmation

| Code | Meaning |
| --- | --- |
| `CONFIRMATION_UNKNOWN` | No pending entry |
| `CONFIRMATION_ALREADY_USED` | Single-use entry consumed |
| `CONFIRMATION_EXPIRED` | Past `expiresAtUtc` |
| `CONFIRMATION_INVALID` | MAC mismatch |
| `CONFIRMATION_CONTENT_MISMATCH` | `displayedText` hash differs from `promptHash` |
| `CONFIRMATION_BINDING_MISMATCH` | Destination, provider, session, or mode differs |
| `CONFIRMATION_DEVICE_MISMATCH` | Confirmed from a different device |

All confirmation codes are `retryable: false`.

### Approval

| Code | Meaning |
| --- | --- |
| `APPROVAL_UNKNOWN` | No pending approval |
| `APPROVAL_ALREADY_USED` | Consumed, possibly by another device |
| `APPROVAL_EXPIRED` | Past `expiresAtUtc`; treated as denied |
| `APPROVAL_INVALID` | MAC mismatch |
| `APPROVAL_CONTENT_MISMATCH` | Echoed `operationHash` differs |
| `APPROVAL_OPERATION_CHANGED` | Provider operation no longer matches |
| `APPROVAL_ATTESTATION_REQUIRED` | `Consequential` with `attestationKind: None` |
| `APPROVAL_ATTESTATION_NOT_PERMITTED` | The offered `attestationKind` is not accepted by the active policy, for example `LocalVerification` under `RequireDeviceSignature` |
| `APPROVAL_ATTESTATION_INVALID` | Signature, binding, freshness, transport, or single-use check failed |

All approval codes are `retryable: false`.

## 9. Conformance vectors

T003 delivers `tests/protocol-vectors/` with these directories, consumed by both platforms:

| Directory | Contents |
| --- | --- |
| `canonical/` | Canonical-encoding inputs and expected bytes, including NFC normalization and null handling |
| `envelope/` | Valid and invalid envelopes, including strict-payload rejections |
| `audio/` | Binary header encode and decode cases, gaps, reserved-bit violations |
| `signatures-lows/` | Provider-shaped high-S ECDSA signatures with their normalized low-S canonical DER form, produced by both an Android-backed and a .NET-backed signer, plus negative cases for non-canonical DER (ADR-003 section 3) |
| `negotiation/` | `Challenge`, version negotiation, `Hello` signature, challenge reuse, and resume cases |
| `signatures/` | ECDSA P-256 with SHA-256 vectors for `optimus-hello-v1`, `optimus-welcome-v1`, `optimus-pair-v1`, and `optimus-pair-result-v1`, with fixed test key pairs, plus negative cases for non-canonical DER and high-S signatures |
| `confirmation/` | MAC vectors for `optimus-confirm-v1` with a fixed test key, including the non-null `sessionId` requirement |
| `approval/` | MAC vectors for `optimus-approval-v1`, signature vectors for `optimus-approval-response-v1`, and `LocalVerification` payload validation cases |
| `errors/` | One example per registry code |

A change to any vector file is a protocol change and requires an ADR amendment.
