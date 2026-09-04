# Optimus Voice OS threat model

- Status: Accepted
- Owner: Claude Opus 5
- Governing decisions: ADR-001 through ADR-005
- Reviewed against: `PROJECT_PLAN.md` product invariants
- Implemented and verified by: T004, T016, T017, T018, T019, T020, T023, T024, T029

## 1. Scope and method

The system under analysis is the full local product: the Windows Shell, Core, inference runners, provider adapters and the CLIs they spawn, the Android application, and the single WebSocket link between phone and PC. There is no hosted service, relay, account system, or telemetry endpoint, so no cloud component is in scope beyond the coding agents' own existing cloud behavior, which Optimus does not change.

Analysis method: enumerate assets, draw trust boundaries, enumerate attacker capabilities, then walk each attacker against each boundary using STRIDE, recording the mitigation and the residual risk. Every mitigation names the ADR that decides it and the task that verifies it.

## 2. Assets

| # | Asset | Why it matters | Primary protection |
| --- | --- | --- | --- |
| A1 | Prompt content (transcript and cleaned draft) | Contains proprietary source, credentials spoken aloud, business context | Never persisted beyond opt-in history; never in logs or error messages |
| A2 | Routine audio | Most sensitive capture; may contain background speech | Never written to disk anywhere (ADR-004 section 7) |
| A3 | Confirmation integrity | The gate that stops an unintended prompt reaching an agent | MAC + pending map + ten ordered checks (ADR-005 section 3) |
| A4 | Approval integrity | The gate that stops a destructive agent operation | MAC + operation hash + biometric attestation (ADR-005 section 8) |
| A5 | Device trust material | ECDSA P-256 device and approval keys, server signing key | Hardware-backed Android Keystore (StrongBox where available); non-exportable Windows CNG |
| A6 | PC identity key and pin | Impersonating the PC would capture every prompt | Non-exportable CNG key; SPKI pinning on the phone |
| A7 | Provider credentials | Access to the user's coding agent accounts | **Not an Optimus asset.** Credentials stay in the provider's own store; Optimus never reads, copies, persists, or injects them (ADR-003 section 8) |
| A8 | Agent session control | Sending or steering as the user | Confirmation gate plus authenticated device connection |
| A9 | Local text event history | Long-lived record of what was asked | User-configurable retention, explicit clear, DACL-restricted |
| A10 | Calibration recordings (opt-in) | Deliberately retained audio | Explicit per-recording consent, listed, individually deletable |
| A11 | Service availability | A silenced or stuck voice layer is a real failure | Supervisor, backoff, degradation policy (ADR-004 section 6) |

## 3. Trust boundaries

| # | Boundary | Crossing | Trust direction |
| --- | --- | --- | --- |
| B1 | Network to Core | Pinned-TLS WebSocket from phone | Fully untrusted until `Hello` authenticates |
| B2 | Loopback to Core | Shell connection | Trusted only as far as the user account boundary |
| B3 | Core to runner | Named pipe, user-SID DACL | Runner is trusted for compute, never for policy |
| B4 | Adapter to provider CLI | Child-process stdio | CLI input is trusted, CLI **output is untrusted** |
| B5 | Provider CLI to its cloud | Provider's own channel | Outside Optimus; unchanged by design |
| B6 | User account to OS | File DACLs, DPAPI, Keystore | Same-user code is not defended against by cryptography |
| B7 | Phone lock screen | Notifications and biometric | Locked device shows minimal detail only |
| B8 | Installer to model store | SHA-256 manifest | Weights are verified before load |

## 4. Attacker capabilities considered

| ID | Attacker | Assumed capability |
| --- | --- | --- |
| T1 | LAN attacker | Same subnet or same Tailnet as a peer; can send packets, run a rogue listener, ARP or DNS spoof, and capture traffic |
| T2 | Local malware, same user | Arbitrary code as the logged-in user; can read user files, DPAPI blobs, and process memory |
| T3 | Local malware, different user or lower privilege | Code on the machine under another account |
| T4 | Stolen or lost phone | Physical possession, possibly unlocked briefly, possibly with the user's PIN unknown |
| T5 | Passive or active network replay | Captured ciphertext and any recovered plaintext frames; can replay them later |
| T6 | Stale or racing approval | Not necessarily malicious: an approval displayed for operation X applied to operation Y |
| T7 | Malicious provider output | The coding agent, or content it read, emits text designed to manipulate Optimus or the user |
| T8 | Accidental prompt disclosure | No attacker; the system leaks prompt content into logs, screenshots, dumps, notifications, or history |
| T9 | Malicious QR or pairing race | An attacker who sees the QR, or who reaches the pairing endpoint during the window |
| T10 | Supply-chain tampering of models | Modified weights on disk |

## 5. Analysis

### T1. LAN attacker

| Threat | Mitigation | Residual |
| --- | --- | --- |
| Connect to the service and send prompts | Listener binds loopback plus explicitly configured trusted addresses only, never `0.0.0.0` (ADR-003 section 7). Unpaired devices are rejected at `Hello` with `4403`. | An attacker on an explicitly trusted interface still cannot authenticate without a device key. |
| Impersonate the PC to capture prompts | Phone pins the PC SPKI. A rogue listener presents a different key and the TLS session is refused before any data is sent. | A phone that pairs with the attacker's QR by mistake; addressed by T9. |
| Downgrade or strip TLS | TLS 1.3 only, fixed AEAD suites, `wss` scheme hard-coded, no plaintext fallback path exists in the client. | None material. |
| Read traffic | TLS 1.3 with forward secrecy. | Endpoint compromise. |
| Denial of service by connection flooding | Handshake rate limit of 30 per minute per source; auth failure lockout of 10 minutes; `4429` close on repeated rate-limit violations. | A determined attacker on a trusted interface can still consume bandwidth. Accepted: the PC remains usable and dictation is local to the desktop path. |
| Discover the service by scanning | The service exposes no discovery beacon and returns a bare TLS listener with no application banner before authentication. | Port presence is observable. Accepted. |

### T2. Local malware running as the same user

This is the strongest attacker and cryptography does not defeat it. The design goal is honest containment, not a false claim.

| Threat | Mitigation | Residual |
| --- | --- | --- |
| Read the DPAPI secret store | None possible: DPAPI CurrentUser is decryptable by same-user code by definition. | **Accepted and documented.** Device records (public keys and metadata) are exposed to same-user malware. Provider credentials are not, because Optimus never holds them; the provider's own store carries its own exposure independently of Optimus. |
| Extract the PC or device signing keys | `K_server_sign` and the certificate key live in non-exportable CNG containers, so the raw private keys are not readable even by same-user code, though the running process can be asked to sign. | Accepted: signing oracle, not key theft. |
| Read the Shell capability token and connect as a device | Token is DACL-restricted to the user, but same-user code can read it. Core additionally checks that the peer is loopback and runs as the same user SID. | **Accepted.** Same-user malware can drive Optimus as the local Shell. It still cannot bypass a confirmation: it would have to construct a `SendAction` matching a `ConfirmationRequest` it can request, which means it can send prompts. Documented as a residual risk equal in severity to the malware directly running the coding-agent CLI. |
| Scrape audio buffers from Core memory | Buffers are pooled and zeroed on release, reducing but not removing the window. | Accepted. |
| Read prompt text from disk | Nothing writes drafts or audio to disk except opt-in history and calibration. | History is readable if enabled; retention control and clear-history limit exposure. |
| Attach a debugger to Core | Not defended. | Accepted. |
| Replace runner executables or model files | Model files are SHA-256 verified against a manifest before load; runner binaries are verified only by the OS. | Accepted; code-signing enforcement is a T026 release item, not a v1 security boundary. |

The threat-model position is: **once code runs as the user, Optimus offers no additional barrier beyond what the coding-agent CLIs themselves offer.** Optimus does not widen the attack surface, and the confirmation gate still forces a visible destination and a MAC-bound send, which leaves evidence in the event history.

### T3. Local malware under a different account

| Threat | Mitigation | Residual |
| --- | --- | --- |
| Connect to the loopback service | Core verifies the connecting process owner SID equals the service user for `localToken` authentication; a different user is rejected `4401`. | None material. |
| Read the token, secrets, models, or history | Directory and file DACLs grant only the service user and `SYSTEM`. | An administrator account can still take ownership. Accepted: administrators are trusted. |
| Connect to a runner pipe | Pipe DACL is the user SID only, and the pipe name contains 128 random bits. | None material. |

### T4. Stolen or lost phone

| Threat | Mitigation | Residual |
| --- | --- | --- |
| Send prompts from the stolen phone | `optimus_device_v1` requires `setUnlockedDeviceRequired(true)`, so signing fails on a locked device. | A thief who knows the screen lock has full device authority; identical to any other app. Mitigated by remote revoke from the PC. |
| Approve a consequential operation | `optimus_approval_v1` is a hardware-backed P-256 key requiring `AUTHENTICATORS_BIOMETRIC_STRONG` per use through a bound `CryptoObject`, and is invalidated by new biometric enrolment. | A thief with a working biometric match. Not defensible. |
| Read past prompts on the phone | The phone caches only the current session view; history lives on the PC and is fetched on demand over an authenticated connection. | Screen content visible while unlocked. |
| Extract the device key | Keys are non-exportable, StrongBox when available. | Hardware attack on the secure element. Accepted. |
| Read approval details from the lock screen | Background approval notifications show only the session title and risk level until unlock and biometric success (ADR-005 section 8). | Title text is provider-derived and sanitized; still visible. |
| Continue access after theft | Tray "Revoke" closes the connection with `4403`, drops that device's queued prompts, and invalidates its confirmations and approvals. | Requires the user to notice and act. |

### T5. Replay

| Threat | Mitigation | Residual |
| --- | --- | --- |
| Replay a captured `Hello` on a new connection | The signature covers a single-use `serverChallenge` that Core issued for that one connection and consumed (ADR-003 section 6). A new connection issues a different challenge, and the captured `Hello` echoes one that will never be valid again. | None material. |
| Replay `Hello` on the same connection | 16-byte nonce cache per device for 5 minutes and a 60 s timestamp window. | None material. |
| Replay a `SendAction` | Confirmation entries are single-use and consumed atomically; a repeat yields `CONFIRMATION_ALREADY_USED`. A different envelope `id` does not help, because consumption is keyed on `confirmationId`. | None material. |
| Replay an `ApprovalResponse` | Approval entries are single-use; a `DeviceSignature` attestation covers the approval's own `nonce` and this connection's challenge, plus a 60 s freshness window. | A `LocalVerification` attestation is not cryptographic; see R8. |
| Replay a pairing `PairRequest` | `psk` is single-use and expires in 120 s, and the proof covers the single-use `PairChallenge` for that connection. | None material. |
| Replay after a Core restart | `K_conf` is regenerated per start, so every pre-restart confirmation and approval MAC fails. | Users must re-confirm after a restart. Intended. |

### T6. Stale, racing, or mismatched approval

| Threat | Mitigation | Residual |
| --- | --- | --- |
| Approve an operation that changed after display | `operationHash` is echoed and re-verified against the provider's current pending operation (`APPROVAL_OPERATION_CHANGED`). | A provider that changes an operation without changing its canonical description. Adapter conformance tests cover the known operation classes. |
| Approve after the operation already ran | Approvals expire in 180 s and are consumed once; an expired approval is reported to the provider as **denied**, never approved. | None material. |
| Two devices approving simultaneously | Atomic single consumption; the second device receives `APPROVAL_ALREADY_USED` and its card resolves via `ApprovalResolved`. | None material. |
| A confirmation redeemed for a different mode or destination | Checks 7 and 8 in ADR-005 section 3 bind mode, destination, provider, session, and device. | None material. |
| A stale draft confirmed long after it was read | 120 s confirmation expiry. | A user who confirms within 120 s of a draft they misread. Human factor, mitigated by showing the exact text on the confirmation card. |

### T7. Malicious provider output

The coding agent reads repositories, issues, and web content, so its output must be treated as attacker-controlled.

| Threat | Mitigation | Residual |
| --- | --- | --- |
| Output instructs Optimus to send a prompt, change destination, or approve | Structurally impossible: provider output is data on a one-way path to display and speech. Every state transition requires an authenticated device message (ADR-005 section 9). | None material. |
| Output instructs the *user* to approve something dangerous | Risk level is computed by the adapter from the operation class, never from provider text; `Other` maps to `Consequential`; consequential approvals require a per-use biometric signature over a specific hashed operation on a paired phone, and that is the default routing once a phone exists. | Social engineering of the user remains possible. Mitigated by showing operation class, not just provider prose. |
| Output contains terminal escapes, bidirectional overrides, or homoglyph tricks to disguise an operation | Control characters other than newline and tab are stripped, bidirectional override characters are stripped, output is rendered as plain text with no markup, link, or image resolution. | Homoglyph confusion in a path name remains possible; the operation class and workspace-relative rendering reduce it. |
| Output floods the UI or the speech queue | 4 KiB display cap, 600 character speech cap, 5 s coalescing, one utterance per 4 s per session. | None material. |
| Output leaks into logs and then to a third party | Adapters must not log `RawText`; diagnostics record kind, ref, and byte length. | Accepted, verified by T029 audit. |

### T8. Accidental prompt disclosure

| Vector | Mitigation |
| --- | --- |
| Log files | `Error.message` and all log statements are forbidden from carrying transcript, draft, provider output, tokens, paths, or host names. A logging analyzer rule and a T029 audit enforce it. |
| Crash dumps | Windows Error Reporting local dumps disabled for runner images; Core writes stack traces and state only, never buffers (ADR-004 section 7). |
| Temporary files | Runners get an empty per-run `%TEMP%` directory, deleted on exit; no component writes audio or draft text to temp. |
| Android notifications | Draft and prompt text never appear in a notification. Approval notifications carry title and risk level only until unlock. |
| Screen recording and screenshots | The Android Talk screen sets `FLAG_SECURE` while a draft or confirmation is displayed. |
| Clipboard | Optimus never copies drafts to the clipboard implicitly. |
| History | Text history retention is user-configurable with a clear-history control; default retention is 30 days. Session titles are the only prompt-derived text in `sessions.json`, and Clear History resets them. |
| Queued prompts | Queued prompt text, hashes, receipts, and previews are in-memory only and are never written to disk. The only durable trace is a per-session integer, `pendingQueueDepth`, which records how many prompts were pending and nothing about what they said (ADR-005 section 6). |
| Backups | `android:allowBackup="false"` and data-extraction rules exclude all app storage. |
| Diagnostics bundle | The diagnostics export contains counters, timings, versions, and coded events only, and is shown to the user before it is written. |

### T9. Malicious QR or pairing race

| Threat | Mitigation | Residual |
| --- | --- | --- |
| Attacker photographs the QR and pairs first | `psk` is single-use with a 120 s expiry, only one attempt is outstanding, and the PC displays a 6-character verification code the user must match on the phone. An attacker pairing first produces a code the user does not see on their phone. | A user who ignores the code mismatch. |
| Attacker phishes a QR to the user | The QR is generated only by the PC settings window; the phone shows the PC name and fingerprint before completing. A user pairing to an attacker PC would send prompts there. | Accepted; the verification step and PC name are the mitigation. |
| Attacker brute-forces `psk` | 256-bit secret, 3 attempts per `pid`, 5 per 10 minutes per source. | None material. |
| QR persists on screen | Erased on focus loss, success, or expiry. | Shoulder surfing during the window. |

### T10. Model or weight tampering

| Threat | Mitigation | Residual |
| --- | --- | --- |
| Modified weights alter transcription or cleanup to change intent | Every model file is SHA-256 verified against `manifest.json` at startup and after each runner restart; a mismatch is terminal (`MODEL_INTEGRITY_FAILED`). | Same-user malware could rewrite both weights and manifest. Falls under T2. |
| A cleanup model that silently rewrites meaning | The intent-guard suite (`docs/performance/BENCHMARK_PLAN.md` section 6) gates model selection at zero intent changes, and the draft always shows the raw transcript alongside the cleaned text so the user can see any change before confirming. | A change subtle enough to pass both the suite and the user's reading. |

## 6. Invariant-to-control map

| Product invariant | Enforcing control | Verified by |
| --- | --- | --- |
| Never send without a visible destination and confirmation | ADR-005 sections 1 to 3; `ConfirmedPrompt` requires a `ConfirmationReceipt`; ten ordered checks | T019, T016 conformance |
| Never infer or silently change the destination | `DESTINATION_REQUIRED`; active destination is cleared, never replaced, when a destination goes unavailable; binding check 7 | T018, T019 |
| Cleanup may not change intent | Cleanup is advisory; raw transcript is always shown and is the fallback; zero-intent-change gate | T009, T012 |
| Ordinary voice processing and audio remain local | No network path exists from capture to anything but Core and the ASR runner | T024, T029 |
| Routine audio is not retained | ADR-004 section 7 residency rule and filesystem audit | T024 |
| Agent cloud behavior unchanged, isolated behind adapters | `Optimus.Providers` boundary and dependency test | T002, T016, T017 |
| Approvals authenticated, expiring, replay-protected, operation-bound | ADR-005 section 8 | T023 |
| Phone is capture, status, approval only; PC is the only server | No inference or adapter code in the Android modules; module dependency check | T002, T021 |

## 7. Residual risks accepted

| ID | Residual risk | Why accepted | Compensating control |
| --- | --- | --- | --- |
| R1 | Same-user malware can read secrets and drive the local Shell client | Windows offers no cryptographic barrier at this boundary for an unelevated user-mode app | Event history records every send with its confirmation id and destination |
| R2 | A thief who knows the phone unlock and matches biometrics has full authority | Equivalent to any authenticated mobile app | Remote revoke from the PC; keys invalidated by new biometric enrolment |
| R3 | The user can be socially engineered by provider prose into approving a consequential operation | Cannot be solved technically | Risk level from operation class; biometric friction; operation hash shown as a class and target, not just prose |
| R4 | Text history, when enabled, is readable by same-user code | It is the feature the user asked for | Retention default 30 days, explicit clear, DACL restriction |
| R5 | A LAN attacker on an explicitly trusted interface can consume bandwidth | Availability, not integrity | Rate limits; desktop path unaffected |
| R6 | Runner binaries are not signature-verified in v1 | Scope; falls under T2 anyway | Model hash verification; code signing tracked in T026 |
| R7 | Homoglyph or path confusion in a displayed operation | Complete defence needs a Unicode confusable policy | Bidirectional overrides stripped; workspace-relative rendering; operation class shown |
| R8 | A compromised paired client can render misleading text while echoing the correct `displayedText`, and can assert `LocalVerification` on the desktop without a real Windows Hello prompt | No client-side check can attest to what was painted on screen, and `UserConsentVerifier` produces no signature to bind. This is the same boundary as R1 | One immutable confirmation view model feeding both render and echo; server-side exact-content validation; `RequireDeviceSignature` is the default policy once a phone is paired, moving consequential decisions to a device that signs in hardware |
| R9 | A Core restart discards queued prompts; a second crash after the count is reset but before a device reconnects can also lose the drop notice | The alternative is persisting confirmed prompt bodies or adding a durable per-device notification ledger in v1 | Count-only `pendingQueueDepth` normally makes loss visible; repeated startup failure remains a documented best-effort edge for T018 diagnostics |

## 8. Security tests required before release

Every item below is a release gate in T029:

1. Confirmation negative-path suite: one test per failure code in `docs/specs/WIRE_PROTOCOL.md` section 8, confirmation and approval groups.
2. Concurrency: two simultaneous `SendAction` messages for one confirmation admit exactly one; two devices approving one approval resolve exactly once.
3. Replay: recorded `Hello`, `PairRequest`, `SendAction`, and `ApprovalResponse` replayed on a fresh connection all fail with the expected codes, and a `Hello` replayed on its own connection fails because the challenge was consumed.
4. Pinning: connecting to a listener with a different SPKI fails before any application data is sent.
5. Binding: assert the listener never opens `0.0.0.0`; assert a `localToken` from a non-loopback peer is rejected.
6. Privacy sweep: after 50 utterances and 5 crashes, no audio file, draft text, or transcript exists outside the opt-in calibration directory; grep logs and the diagnostics bundle for known canary strings spoken during the run.
7. Hostile provider output corpus: escapes, bidirectional overrides, 1 MB output, instruction-like text, and homoglyph paths cause no state transition, no log leak, and correct truncation.
8. Revocation: a revoked device is disconnected, and its pending confirmations, approvals, and queued prompts are invalidated.
9. Model integrity: a single flipped byte in a weight file prevents load with `MODEL_INTEGRITY_FAILED`.
10. Android: `allowBackup` false, `FLAG_SECURE` on draft and confirmation screens, approval key is hardware-backed P-256 requiring per-use biometric, notification content contains no draft text.
11. Attestation policy: a desktop `LocalVerification` is refused under `RequireDeviceSignature`, refused on a non-loopback connection, refused for a mismatched `approvalId`, refused after 60 s, and refused on a second presentation.
12. Provider credential boundary: a filesystem and process-launch recorder over a full adapter session shows no read of a provider credential path, no credential-bearing argument or environment variable, and no provider secret in any Optimus-owned file.
13. Queue privacy and drop reporting: after 20 queued prompts and one successful Core restart, `sessions.json` contains no queued text, hash, receipt, or preview, every queue is empty, and each affected session reports `queueDroppedCount` once to each connecting device in that run. Fault injection inside both durable-write windows must yield a recovered count greater than or equal to the number actually lost. A double-startup-crash test records the accepted notice-loss limitation.
14. GPU exclusivity: a fault-injection run in which the TTS runner ignores `cancel` shows the ASR lease is granted only after verified process exit, never concurrently, and that the stuck path surfaces `GPU_SLOT_STUCK` at `W_preempt` (60 ms) rather than overlapping.
15. No duration-based rejection: a corpus of deliberate single-word commands as short as 150 ms produces transcripts, drafts, and confirmations, and appears in the G2 and G3 population; only the non-speech captures yield `NO_SPEECH_DETECTED`.
