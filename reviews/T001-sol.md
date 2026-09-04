# T001 — GPT-5.6 Sol review

- Reviewer: GPT-5.6 Sol
- Task contract: `tasks/T001-architecture.md`
- Base commit: `fdca807`
- Reviewed commit: `122cbbf`
- Round: 1

## Evidence reviewed

- All 12 required deliverables, `docs/ARCHITECTURE.md`, and the complete `fdca807...122cbbf` diff.
- Cross-document confirmation, session, queue, approval, inference-scheduling, latency, benchmark, credential, and protocol invariants.
- `git diff --check fdca807...HEAD`: clean, exit 0.
- `git status --short`: clean before this review was written.
- Required-deliverable existence check: all 12 paths present.
- Diff size: 14 files, 3,017 insertions, 5 deletions; documentation and task contracts only.
- Platform feasibility references:
  - Android `KeyPairGenerator` supported algorithms: <https://developer.android.com/reference/java/security/KeyPairGenerator>
  - Android Keystore cryptographic primitives: <https://source.android.com/docs/security/features/keystore/features>
  - Android authentication-bound keys: <https://developer.android.com/reference/android/security/keystore/KeyGenParameterSpec.Builder>
  - Windows `UserConsentVerifier.RequestVerificationAsync`: <https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.ui.userconsentverifier.requestverificationasync>
  - Calling WinRT UI from desktop apps: <https://learn.microsoft.com/en-us/windows/apps/develop/ui/display-ui-objects>
  - Conscrypt TLS exporter API: <https://github.com/google/conscrypt/blob/master/common/src/main/java/org/conscrypt/Conscrypt.java>

## Findings

### P0

None.

### P1

#### 1. The approval-key design is not implementable on the named platforms

`docs/adr/ADR-003-pairing-authentication-and-secrets.md:53-54,70,163-172` requires two hardware-backed Android Keystore Ed25519 keys, while `docs/adr/ADR-005-confirmation-sessions-and-approvals.md:210-226` requires either an Ed25519 approval signature or a Windows Hello `UserConsentVerifier` “success token equivalent.” Android Keystore's documented `KeyPairGenerator` algorithms do not include Ed25519, and `UserConsentVerifier` returns a verification result rather than a cryptographic signature over Optimus's approval fields. The current contract therefore cannot be implemented by T020/T021 as written.

Required resolution: use an Android Keystore-supported hardware-backed signing primitive, preferably ECDSA P-256 with SHA-256, and update all key encodings, vectors, signature fields, validation rules, and threat-model entries. Define desktop approval honestly as a local interactive verification result bound inside Core to a single pending approval, or specify a real Windows cryptographic signing mechanism. Do not describe `UserConsentVerifier` as producing an attestation token.

#### 2. The TLS-exporter binding is incomplete for the specified Android WebSocket stack

`docs/adr/ADR-003-pairing-authentication-and-secrets.md:53-78,102-124` and `docs/specs/WIRE_PROTOCOL.md:93-107,323` make an RFC 5705 exporter mandatory in pairing, hello, welcome, and approval signatures. The architecture also fixes the Android transport as OkHttp WebSocket, but neither the ADR nor T002 specifies a TLS provider/socket integration that exposes exporter bytes to application code. Conscrypt has an exporter API only when the application owns a compatible Conscrypt socket; that is a materially different dependency and integration decision. T020 cannot infer it.

Required resolution: either replace exporter dependence with an explicit protocol challenge/response using server and client nonces under the pinned TLS channel, including exact signed byte layouts and replay storage, or mandate the concrete Android TLS provider/socket path and prove it works through the selected WebSocket client. Update every message schema, vector owner, error code, and threat-model claim consistently.

#### 3. Queue persistence contradicts the privacy and session-storage contract

`docs/adr/ADR-005-confirmation-sessions-and-approvals.md:136` says `sessions.json` contains text metadata only and no prompt bodies. Line 166 says queued prompt text is also written to that same state file. `docs/security/THREAT_MODEL.md:19` says prompt content is never persisted beyond opt-in history. These statements cannot all be true, and the queue cannot survive restart without persisting either plaintext or recoverable prompt content.

Required resolution: choose and specify one behavior. The privacy-preserving default is an in-memory-only queue that is dropped on Core restart with a count-only notice, while session metadata still resumes. If durable queues are product-critical, define explicit opt-in, encryption, retention, deletion, crash-dump, backup, and threat-model behavior rather than hiding prompt bodies in session state.

#### 4. The explicit-session invariant is not enforced by the wire contract

The product requires a managed provider session to resume until the user explicitly chooses New Session. However, `docs/specs/WIRE_PROTOCOL.md:239` and `docs/adr/ADR-005-confirmation-sessions-and-approvals.md:44` let `RequestConfirmation.sessionId = null` request a new session. A stale or buggy client can therefore create a new provider session without an explicit `NewSession` action. Making `Destination.defaultSessionId` informational does not close this path.

Required resolution: make confirmation target a Core-resolved, non-null session. Permit absence only when that destination has never had a session, or require the client to execute explicit `NewSession` first and use the returned Optimus session ID. Specify concurrency/idempotency semantics for simultaneous `NewSession` requests and update schemas, state transitions, validation order, and test vectors.

#### 5. GPU preemption violates both the single-slot invariant and the latency budget

`docs/adr/ADR-004-inference-lifecycle-and-gpu-scheduling.md:122` admits P0 after waiting 100 ms for cancellation, but lines 135-137 do not kill an unresponsive runner until 500 ms. This permits two GPU jobs to overlap for up to 400 ms despite the stated one-job invariant. Separately, `docs/performance/LATENCY_BUDGET.md:47,65` allocates only 10 ms for ASR admission, while active-TTS preemption may consume 100 ms; the benchmark contention run does not exercise this transition.

Required resolution: never grant the next lease until cancellation acknowledgement or verified runner exit/GPU-context teardown. Define what happens at the 100 ms boundary without overlap—immediate termination, degraded CPU path, or failed capture—and reconcile it with the 10 ms admission allocation. Add an active-TTS-to-hotkey benchmark and release gate so G2/G3 include the actual worst interactive contention path.

#### 6. Optimus must not take ownership of provider subscription credentials

`docs/adr/ADR-003-pairing-authentication-and-secrets.md:157`, `docs/specs/SENDER_ADAPTER.md:173`, and `docs/security/THREAT_MODEL.md:25,78` require Optimus to copy provider credentials/tokens into its DPAPI store. That conflicts with the stated product boundary that provider cloud behavior remains unchanged and with the subscription-native Claude Code/Codex workflow. It also expands the secret-handling attack surface for no demonstrated need.

Required resolution: adapters invoke an already-authenticated provider CLI/app and leave authentication material in the provider's own credential store. Optimus may detect and report `AUTH_REQUIRED`, but must not read, copy, transform, persist, or inject provider tokens. Update adapter startup/error behavior and remove provider credentials from Optimus-owned assets and residual risks.

### P2

#### 1. Echoed `displayedText` does not prove what a device rendered

`docs/adr/ADR-005-confirmation-sessions-and-approvals.md:97,255` claims that echoing `displayedText` proves the device rendered the same bytes and prevents a compromised device from displaying A while echoing B. A response field can detect honest client binding bugs, but it cannot attest to pixels or defeat a compromised client.

Required resolution: narrow the security claim. Require one immutable confirmation view model to feed both the rendered text and the echoed hash, retain server-side exact-content validation, and list a compromised paired client presenting misleading UI as a residual risk. Do not call the echo cryptographic proof of rendering.

### P3

None.

## Contract compliance

- Scope: Pass. No production implementation, models, secrets, audio, or build output were introduced.
- Deliverables: Pass. All 12 required artifacts exist and are substantial.
- Interfaces and invariants: Fail pending the six P1 corrections above.
- Acceptance criteria: Fail because several public interfaces are not implementable or internally consistent on the selected platforms.
- Required tests/checks: Structural checks pass; target-hardware measurements appropriately remain future work.
- Unrelated changes: None found.

## Verdict

`CHANGES_REQUIRED`

The architecture is well structured and close to an implementation-ready foundation, but T002 must remain blocked until the P1 findings are resolved consistently across the ADRs, wire protocol, threat model, latency/benchmark documents, backlog ownership, and T002 contract. Claude Opus 5 should own this correction round; GPT-5.6 Sol should then review the new branch head.
