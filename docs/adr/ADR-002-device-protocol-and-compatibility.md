# ADR-002: Versioned device protocol, transport, compatibility, and error behavior

- Status: Accepted
- Date: 2026-09-04
- Owner: Claude Opus 5
- Related tasks: T001, T003, T004, T021

## Context

One WebSocket connection carries every interaction between a device (Pixel 9a or the desktop Shell) and Core: control messages, streaming PCM, drafts, confirmations, agent events, approvals, and voice summaries. Because the desktop Shell is also a protocol client (ADR-001 section 2), this protocol is the only way any user action reaches the orchestrator.

The protocol must therefore be explicit about versioning, framing, ordering, correlation, idempotency, timeouts, reconnection, and error semantics. Ambiguity here becomes a confirmation-integrity bug or a session-corruption bug later.

The message *catalogue* is specified in `docs/specs/WIRE_PROTOCOL.md`. This ADR fixes the transport rules that every message obeys.

## Decision

### 1. Endpoint and negotiation

- URL: `wss://<host>:<port>/v1/device`. The `/v1` path segment carries the **major** version. A future major version gets a new path and may be served concurrently.
- WebSocket subprotocol: `optimus.v1`. Core rejects a handshake that does not offer it, with HTTP 400.
- TLS 1.3 only. Cipher suites limited to `TLS_AES_128_GCM_SHA256` and `TLS_AES_256_GCM_SHA384`. Certificate and pinning rules are in ADR-003.
- Version string format is `MAJOR.MINOR`, currently `1.0`. MAJOR changes are breaking. MINOR changes are additive only.

Negotiation is the first exchange on every connection, and the **server speaks first**:

1. Core sends `Challenge` with a single-use `serverChallenge` and `serverTimeUtc`, immediately after the WebSocket opens (ADR-003 section 6). The client must not send anything before receiving it.
2. Client sends `Hello` with `protocolMajor`, `protocolMinorMax`, `deviceId`, `clientBuild`, and the authentication material from ADR-003 section 6, which echoes and signs over `serverChallenge`.
3. Core replies `Welcome` with the negotiated `protocolVersion` (`MAJOR.min(clientMinorMax, serverMinor)`), `serverBuild`, `deviceSessionId`, `serverTimeUtc`, `serverAuth`, and the effective `limits` object.
4. If `protocolMajor` differs from the server major, Core closes with code `4406` and reason `UNSUPPORTED_VERSION` before any other message.

The server speaks first so the connection carries a fresh anti-replay binding that does not depend on TLS keying-material export, which the Android WebSocket stack cannot reach (ADR-003 section 3).

No message other than `Challenge`, `Hello`, `Welcome`, `Error`, or a close frame may precede a successful `Welcome`. Core drops a connection that sends anything else first, with code `4400`. A `Hello` that arrives before the client could have received `Challenge`, or that echoes a challenge Core did not issue on this connection, is `4401`.

### 2. Text-frame envelope

All control messages are WebSocket text frames containing one UTF-8 JSON object:

```json
{
  "v": "1.0",
  "type": "PromptDraft",
  "id": "01J9YB6C5R7Q3W2K8N4M0X5T1A",
  "corr": "01J9YB6BZ0F2N9V6T3H7QK4D8E",
  "seq": 128,
  "ts": "2026-09-04T11:02:31.482Z",
  "payload": { }
}
```

| Field | Type | Rules |
| --- | --- | --- |
| `v` | string | Negotiated `protocolVersion`. Mismatch after `Welcome` is a `4400` close. |
| `type` | string | PascalCase message type from `docs/specs/WIRE_PROTOCOL.md`. |
| `id` | string | ULID, unique per sender per connection lifetime. Used for idempotency. |
| `corr` | string or null | The `id` of the message this responds to or continues. |
| `seq` | integer | Monotonically increasing per direction, starting at 1, never reset while a `deviceSessionId` lives. Used for resume replay. |
| `ts` | string | RFC 3339 UTC with millisecond precision. Informational only; never used for security decisions except the freshness checks defined in ADR-003 section 6 and ADR-005 section 3, which use the dedicated signed timestamps in those payloads. |
| `payload` | object | Message-specific body. Absent means empty object. |

Serialization rules (identical on both platforms, defined once in `Optimus.Contracts` and mirrored in `:core:protocol`):

- JSON only, no BOM, no trailing whitespace, UTF-8.
- Property names are camelCase inside `payload`.
- Enumerations are transmitted as strings, never ordinals.
- Instants are RFC 3339 UTC strings. Durations are integer milliseconds with an `Ms` suffix in the name.
- Binary blobs inside JSON are base64url without padding.
- `null` and an absent property are equivalent; senders should omit.
- Maximum text frame size is 256 KiB. Larger is a `4400` close.

**Canonical form.** Wherever a signature or MAC covers a payload, it covers the *canonical byte encoding* defined in `docs/specs/WIRE_PROTOCOL.md` section 3, not the transmitted JSON. This removes any dependence on key order or whitespace.

### 3. Binary-frame framing for audio

Audio uses WebSocket binary frames with a fixed 16-byte little-endian header:

| Offset | Size | Field | Value |
| --- | --- | --- | --- |
| 0 | 2 | magic | `0x4F 0x41` (`OA`) |
| 2 | 1 | frameVersion | `0x01` |
| 3 | 1 | flags | bit0 `final`, bit1 `discontinuity`, bits 2-7 reserved zero |
| 4 | 4 | streamId | uint32, allocated by `StartCapture` |
| 8 | 4 | sequence | uint32, starts at 0, increments by 1 per frame in the stream |
| 12 | 4 | sampleCount | uint32, PCM samples in this frame |
| 16 | n | pcm | signed 16-bit little-endian, mono |

- Sample rate is fixed at 16 000 Hz mono for the transport. Devices resample locally.
- One frame carries 20 ms (320 samples, 640 bytes of PCM, 656 bytes total). Devices may coalesce up to 5 frames of audio into one WebSocket message only when the link reports congestion; sequence numbers still advance by one per 20 ms frame.
- Maximum binary frame size is 64 KiB.
- A frame with an unknown magic or `frameVersion`, or an unknown `streamId`, is dropped and answered with an `Error` of code `AUDIO_FRAME_INVALID`. Three such frames in one stream abort the stream.
- Gaps are detected by sequence. A gap sets the server-side `discontinuity` marker for the affected region; the ASR runner receives the marker so it does not silently splice unrelated audio.
- Audio flows device to Core only. Core to device audio (TTS) is delivered as `VoiceChunk` binary frames with the same header, distinguished by `streamId` values allocated by Core in the high half of the uint32 range (`0x8000_0000` and above).

### 4. Ordering, correlation, and idempotency

- WebSocket guarantees ordering within a direction. The protocol adds no reordering tolerance for control messages and relies on `seq` only for resume replay.
- Request/response pairs are correlated by `corr`. A response always carries `corr` equal to the request `id`.
- Every client-to-server message that changes state (`SendAction`, `ApprovalResponse`, `NewSession`, `ResumeSessionRequest`, `CancelRequest`, `SetDestination`, `RequestConfirmation`, `EditDraft`, `ClearHistory`) is idempotent by `id`. Core keeps a per-device dedupe set of the last 512 ids for 10 minutes. A repeat returns the original response with `duplicate: true` and performs no new work.
- Idempotency is not a substitute for confirmation single-use. A `SendAction` replayed with a *different* `id` but the same `confirmationId` is rejected as `CONFIRMATION_ALREADY_USED` (ADR-005 section 3).

### 5. Keepalive, timeouts, and reconnection

| Parameter | Value |
| --- | --- |
| Server ping interval | 10 s |
| Connection considered dead | 30 s without a pong or any frame |
| Handshake budget (TCP connect to `Welcome`) | 5 s |
| Idle connection close (no traffic, no active session) | 30 min, close code `4408` |
| Client reconnect backoff | 0.5 s, 1 s, 2 s, 4 s, 8 s, 15 s, 30 s, then 30 s, each with +/- 20 percent jitter |
| Resume window | 5 min after disconnect |
| Control replay buffer | Last 200 server-to-device messages per `deviceSessionId` |

Reconnect procedure:

1. Client reconnects, receives a fresh `Challenge`, and sends `Hello` including `resumeDeviceSessionId`, `lastServerSeq`, and a signature over the new challenge. A `Hello` captured from the previous connection cannot be reused.
2. If the `deviceSessionId` is still live and within the resume window, Core replies `Welcome` with `resumed: true` and replays buffered messages with `seq > lastServerSeq`.
3. If the buffer no longer covers `lastServerSeq`, Core replies `resumed: false` and sends a full `ServiceStatus`, `SessionList`, and `DestinationList` snapshot. The device discards local optimistic state, including any local copy of a queued prompt.
4. **Audio streams never resume.** Any stream open at disconnect is aborted and the device is told `AudioAborted`. A partially captured utterance is discarded and never transcribed or sent.
5. In-flight confirmations survive a resume only if unexpired; the device re-renders them from the replayed `ConfirmationRequest`.

A second connection presenting the same `deviceId` while one is live causes the older connection to be closed with code `4409`. Devices are single-connection.

### 6. Compatibility rules

**Minor version, additive only.** Within major version 1:

- A new optional property may be added to any payload.
- A new message type may be added.
- A new enum member may be added to a *server-to-device* enum only if every message that carries it also carries a client-renderable fallback string.

Forbidden inside a major version: removing or renaming a property, narrowing a type, changing a default, changing units, changing a required-ness, or changing the meaning of an existing enum member.

**Receiver rules.**

- Unknown JSON properties are ignored, never rejected.
- An unknown `type` from server to device is ignored by the device after logging.
- An unknown `type` from device to server produces an `Error` with code `UNKNOWN_MESSAGE_TYPE` and does not close the connection.
- An unknown enum member in a server-to-device message is rendered from its fallback string and never mapped to a default action. An unknown enum member in a device-to-server message is a `VALIDATION_FAILED` error.
- A device that negotiated `1.0` must not send `1.1` fields; Core ignores them if it does.

**Security-relevant messages do not degrade.** `ConfirmationRequest`, `SendAction`, `ApprovalRequest`, and `ApprovalResponse` are validated strictly: an unknown property inside those four payloads is a `VALIDATION_FAILED` error rather than an ignored field, because silently ignoring a field that a future version uses to constrain an action would weaken the gate.

### 7. Error model

Errors are ordinary text frames of type `Error`:

```json
{
  "v": "1.0", "type": "Error", "id": "...", "corr": "<failing message id>",
  "seq": 42, "ts": "...",
  "payload": {
    "code": "SESSION_BUSY",
    "message": "The session is running a turn.",
    "retryable": false,
    "retryAfterMs": null,
    "details": { "allowedActions": ["Queue", "Steer"] }
  }
}
```

- `code` is a stable SCREAMING_SNAKE_CASE identifier. The full registry lives in `docs/specs/WIRE_PROTOCOL.md` section 8. Codes are never removed within a major version.
- `message` is a short, human-readable, **non-sensitive** string. It never contains transcript text, draft text, provider output, tokens, file paths, or host names.
- `retryable` states whether the identical request may be retried. `SendAction` failures are never `retryable: true`, because a duplicate send is worse than a failed one.
- `details` carries structured, machine-actionable context only.

Transport-level failures use close codes:

| Code | Meaning |
| --- | --- |
| 1000 | Normal shutdown |
| 1001 | Peer going away (logoff, app backgrounded past its grace period) |
| 4400 | Malformed frame, envelope violation, or premature message |
| 4401 | Authentication material missing or invalid |
| 4403 | Device unpaired, revoked, or not permitted on this interface |
| 4406 | Unsupported protocol major version |
| 4408 | Idle timeout |
| 4409 | Duplicate device connection |
| 4429 | Rate limit exceeded |
| 4500 | Internal server error |

Close reasons are drawn from this fixed set of strings and never include free-form diagnostic text.

### 8. Flow control and limits

`Welcome.limits` communicates the effective values so a client never has to hard-code them:

| Limit | Value |
| --- | --- |
| `maxTextFrameBytes` | 262144 |
| `maxBinaryFrameBytes` | 65536 |
| `maxUtteranceSeconds` | 120 |
| `maxConcurrentAudioStreams` | 1 per device |
| `maxQueuedPromptsPerSession` | 5 |
| `maxControlMessagesPerSecond` | 50 sustained, burst 100 |
| `maxAudioBytesPerSecond` | 64000 (2x realtime) |

Exceeding a rate limit yields an `Error` with `RATE_LIMITED` and `retryAfterMs`. Three rate-limit violations within 60 s close the connection with `4429`. Exceeding `maxUtteranceSeconds` finalizes the stream at the limit and marks the resulting draft `truncated: true`; it never silently drops audio.

## Alternatives considered

- **Protobuf or MessagePack for control frames.** Faster to parse, but the control path is roughly ten small messages per utterance and the parse cost is under 1 ms. JSON keeps the protocol inspectable in diagnostics and keeps the Kotlin and .NET models trivially reviewable, which matters more for a confirmation-critical protocol. Binary framing is used where it actually pays: audio.
- **Separate WebSocket connections for control and audio.** Rejected: two connections double the TLS handshake, the pinning surface, and the reconnect state machine, and introduce cross-connection ordering questions during capture finalization.
- **Opus-compressed audio uplink.** Rejected for v1: encode and decode add roughly 20 to 40 ms to a 500 ms budget, and 256 kbit/s raw PCM is trivial on LAN and acceptable on Tailscale. Revisit only if a measured link cannot sustain it, which would require a new ADR.
- **HTTP/2 or gRPC streaming.** Rejected: WebSocket is fixed by `PROJECT_PLAN.md` and gives simpler Android lifecycle behavior and simpler pinning.
- **Version negotiation by URL only.** Rejected: minor-version negotiation is needed for additive evolution without a new endpoint.
- **Client-speaks-first negotiation with TLS channel binding instead of a server challenge.** Rejected on feasibility: the exporter value that binding needs is not reachable from the Android WebSocket stack (ADR-003 section 3). Having the server speak first costs nothing on an already-open socket and gives an anti-replay binding that protocol vectors can exercise offline.

## Consequences

- Devices and Core share one framing definition, so a protocol test suite can run identical vectors on both platforms.
- The strict validation carve-out for the four security-relevant payloads means adding a field to them is a deliberate, breaking-by-policy act that requires a coordinated release. This is intended.
- The 5-minute resume window plus a 200-message replay buffer bounds server memory per device at roughly 200 x 4 KiB, about 800 KiB worst case.
- Never resuming audio streams means a mid-utterance network blip costs the user a re-take. This is preferred over the alternative of splicing audio across a gap and producing an unfaithful transcript.

## Verification

- T003 delivers a shared vector suite: canonical encoding vectors, envelope validation vectors, binary-header vectors, signature vectors, and negotiation vectors including `Challenge`, executed by both `Optimus.Contracts.Tests` and the Android `:core:protocol` unit tests from the same JSON fixture files under `tests/protocol-vectors/`.
- T004 adds integration tests for: `Challenge` is the first frame on every connection; a `Hello` sent before `Challenge`, echoing an unissued challenge, or echoing an already-consumed challenge is `4401`; a second `Hello` on one connection is `4400`; major mismatch closes 4406; unknown device-to-server type yields `UNKNOWN_MESSAGE_TYPE` without closing; unknown property inside `SendAction` yields `VALIDATION_FAILED`; duplicate `id` returns `duplicate: true` without side effects; second connection for one `deviceId` closes the first with 4409.
- T027 adds resume tests covering replay coverage, buffer overflow to snapshot, and audio-stream abort on reconnect.

## Related documents

- `docs/specs/WIRE_PROTOCOL.md`
- `docs/adr/ADR-001-process-and-subsystem-boundaries.md`
- `docs/adr/ADR-003-pairing-authentication-and-secrets.md`
- `docs/adr/ADR-005-confirmation-sessions-and-approvals.md`
