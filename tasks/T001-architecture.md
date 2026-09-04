# T001 — Architecture foundation and implementation backlog

- Status: IMPLEMENTED
- Owner: Claude Opus 5
- Reviewer: GPT-5.6 Sol
- Base: initial repository commit
- Branch: `optimus/T001-architecture`
- Dependencies: None

## Goal

Convert the canonical vision into an internally consistent, decision-complete architecture foundation and create the first implementation task for Gemini 3.8 Flash.

## In scope

- Process and subsystem boundaries.
- Desktop/mobile wire protocol.
- Inference lifecycle and GPU scheduling.
- Pairing, authentication, confirmation integrity, approval security, and privacy threat model.
- Claude Code and Codex adapter contract and session semantics.
- End-to-end latency allocation and benchmark methodology.
- Dependency-ordered task backlog.
- Decision-complete T002 repository/application scaffold contract.

## Out of scope

- Production application code.
- Installing or downloading inference models.
- Implementing desktop, Android, network, speech, TTS, or provider features.
- Selecting benchmark winners without target-hardware results.
- Changing the product invariants in `PROJECT_PLAN.md`.

## Owned files and subsystems

- `docs/adr/`
- New architecture and protocol specifications under `docs/`
- New task contracts under `tasks/`
- `PROJECT_PLAN.md` only to correct a demonstrated contradiction, with an explicit explanation in the ADR

## Required deliverables

1. ADR for process boundaries, technology ownership, and dependency direction.
2. ADR for the versioned desktop/mobile protocol, transport, compatibility, and error behavior.
3. ADR for pairing, certificate pinning, authentication, replay protection, and secret storage.
4. ADR for inference process lifecycle, model residency, GPU/CPU scheduling, cancellation, and crash recovery.
5. ADR for prompt confirmation, destination binding, sessions, Queue, Steer, questions, and approvals.
6. Provider-neutral sender-adapter specification.
7. Wire-protocol specification covering every public message type from `PROJECT_PLAN.md`.
8. Threat model with assets, trust boundaries, attacker capabilities, mitigations, and residual risks.
9. Latency budget allocating the 500 ms warm-path target across capture finalization, ASR, cleanup, IPC, and rendering.
10. Benchmark plan covering corpus construction, hardware conditions, warm/cold runs, statistics, correctness gates, and reproducibility.
11. Dependency-ordered implementation backlog with task IDs, owner model, reviewer, dependencies, owned subsystem, and completion gate.
12. `tasks/T002-scaffold.md`, ready for Gemini 3.8 Flash without further technical decisions.

## Architecture constraints

- Windows implementation uses .NET 8 WPF and ASP.NET Core/Kestrel.
- Android implementation uses Kotlin and Jetpack Compose targeting API 35.
- The PC is the only inference and agent server.
- Mobile traffic uses a pinned-TLS WebSocket with binary audio and structured control messages.
- Provider behavior stays behind replaceable sender adapters.
- Model implementations stay behind replaceable inference contracts.
- The 1.7B voice-design model is setup-time only and must not contend with the warm prompt path.
- A confirmation is cryptographically/logically bound to exact prompt content, destination, session, device, expiry, and a one-time nonce.
- Routine audio must not be persisted, including in logs, crash dumps, or temporary files.

## Acceptance criteria

- Every required deliverable exists and contains a concrete decision rather than an unresolved option list.
- Public interfaces include versioning, cancellation, timeout, retry, idempotency, and error semantics.
- Process ownership and recovery responsibilities are unambiguous.
- The threat model covers local malware, LAN attackers, stolen phone, replay, stale approval, malicious provider output, and accidental prompt disclosure.
- The latency budget totals no more than 500 ms on the warm path and names how each component is measured.
- T002 lists exact scaffold outputs, dependency restrictions, test projects, build commands, and acceptance checks.
- The backlog preserves the Opus/Gemini/Sol division of labor from `AGENTS.md`.
- No production implementation is introduced.

## Required checks

```powershell
git diff --check
git status --short
```

Manually verify every referenced path and cross-document link. Record all checks below.

## Evidence

- Base commit: `fdca807` (`chore: establish multi-model project workflow`)
- Round 1 commit: `27d065c`, containing every deliverable.
- Round 2 commit: `a1ddef4`, carrying every change described under "Round 2 corrections" below.
- Round 3 commit: `92b939e`, carrying every change described under "Round 3 corrections" below. The only later commit on this branch is the one that writes this hash into the evidence, so Sol should review the branch head against the base commit.

### Deliverables produced

| Required deliverable | File |
| --- | --- |
| 1. Process boundaries, ownership, dependency direction | `docs/adr/ADR-001-process-and-subsystem-boundaries.md` |
| 2. Versioned protocol, transport, compatibility, errors | `docs/adr/ADR-002-device-protocol-and-compatibility.md` |
| 3. Pairing, pinning, authentication, replay, secrets | `docs/adr/ADR-003-pairing-authentication-and-secrets.md` |
| 4. Inference lifecycle, residency, scheduling, cancellation, recovery | `docs/adr/ADR-004-inference-lifecycle-and-gpu-scheduling.md` |
| 5. Confirmation, destination binding, sessions, Queue, Steer, questions, approvals | `docs/adr/ADR-005-confirmation-sessions-and-approvals.md` |
| 6. Provider-neutral sender-adapter specification | `docs/specs/SENDER_ADAPTER.md` |
| 7. Wire-protocol specification, every public message type | `docs/specs/WIRE_PROTOCOL.md` |
| 8. Threat model | `docs/security/THREAT_MODEL.md` |
| 9. Latency budget | `docs/performance/LATENCY_BUDGET.md` |
| 10. Benchmark plan | `docs/performance/BENCHMARK_PLAN.md` |
| 11. Dependency-ordered implementation backlog | `docs/BACKLOG.md` |
| 12. Decision-complete T002 contract | `tasks/T002-scaffold.md` |
| Entry-point overview and document map | `docs/ARCHITECTURE.md` |

### Commands executed

```
git status --short
git log --oneline -5
git worktree list
git add -A
git diff --check
git diff --cached --check
git status --short
git config core.autocrlf
git show HEAD:AGENTS.md | file -
git show :docs/adr/ADR-001-process-and-subsystem-boundaries.md | file -
grep -rhoE '`[A-Za-z0-9_./-]+\.(md|json|csproj|sln|props|config|kts|toml|xml|ps1|txt|jsonl)`' docs tasks *.md | tr -d '`' | sort -u
for f in <every referenced existing document>; do [ -f "$f" ] && echo OK || echo MISS; done
grep -rhoE 'ADR-00[1-7]' docs tasks | sort | uniq -c
grep -rhoE '\bT0[0-9]{2}\b' docs tasks | sort -u
grep -oE '^\| T0[0-9]{2}' docs/BACKLOG.md | tr -d '| ' | sort -u
```

### Results

- `git diff --check` and `git diff --cached --check`: no output, exit code 0. No whitespace errors and no conflict markers.
- `git status --short` before commit: 13 added files, all under `docs/` and `tasks/`, plus the modification to this file. No untracked build output, no secrets, no models, no audio, no benchmark output.
- Path verification: all 22 documents referenced as currently existing resolve on disk, including `AGENTS.md`, `PROJECT_PLAN.md`, `README.md`, `docs/WORKFLOW.md`, the five ADRs, the two specs, the threat model, both performance documents, `docs/ARCHITECTURE.md`, `docs/BACKLOG.md`, all three templates, and both task contracts.
- Forward references verified as intentionally future artifacts, each labelled as owned by a later task: `ADR-006`, `ADR-007`, `docs/performance/RESULTS-<category>.md`, `benchmarks/normalization.md`, `tests/protocol-vectors/**`, `MAPPING.md`, and every scaffold file T002 creates.
- Task-ID cross-check: the set of task IDs referenced anywhere in `docs/` and `tasks/` is exactly `T001`-`T029` (plus `T000` from `tasks/TASK_TEMPLATE.md`), and every one of `T001`-`T029` appears as a row in the `docs/BACKLOG.md` tables. No dangling task reference.
- ADR cross-check: every `ADR-00N section M` reference resolves to a numbered section that exists in the target ADR.
- Latency arithmetic re-verified by hand: G1 42 + 8 reserve = 50 ms; G2 190 in-Core + 20 delivery + 25 render + 15 reserve = 250 ms; G3 455 + 45 reserve = 500 ms; G4 187 + 13 reserve = 200 ms; phone LAN 470 + 30 = 500 ms; phone Tailscale 510 + 50 = 560 ms. Every per-model latency allocation in `docs/performance/BENCHMARK_PLAN.md` section 6 matches its budget line.
- Line endings: staged files store LF in the index, matching the existing repository files under `core.autocrlf=true`.
- No production application code, no model files, no dependency manifests, and no build configuration were introduced; the diff is documentation and task contracts only.

### Known limitations

- The desktop and phone latency gates G1-G4 are allocations, not measurements. Nothing here is validated on the target hardware, and it cannot be until T005, T008, T009, T011, and T014 exist. `docs/performance/BENCHMARK_PLAN.md` section 9 defines what happens if an allocation proves unreachable: an Opus-authored re-allocation ADR, never a silent gate relaxation.
- Model winners are deliberately not selected. ADR-004 fixes the lifecycle that applies to any winner; `ADR-006` (ASR and cleanup) and `ADR-007` (TTS) are written by Claude Opus 5 after T012 and T014 produce target-hardware results.
- The VRAM figures in ADR-004 section 1 are design budgets derived from published model sizes at the stated quantization. They are enforced at runtime by `gpu.residentBudgetBytes` and a startup check, so a wrong estimate fails loudly at T008/T009/T014 rather than silently degrading the desktop.
- `PROJECT_PLAN.md` states the G1-G3 gates in terms of the hotkey, which exists only on the desktop. `docs/performance/LATENCY_BUDGET.md` section 1 records the explicit interpretation that these are desktop-path release gates and that phone targets are derived, measured, and reported but not release gates. This is called out rather than assumed; if the intended reading is stricter, it is an Opus decision and a budget amendment, not an implementation choice.
- The threat model states plainly that same-user malware is not defended against by cryptography (`docs/security/THREAT_MODEL.md` section 5, T2, and residual risk R1). This is a documented accepted risk, not an unaddressed gap.
- `PROJECT_PLAN.md` was not modified. No contradiction requiring correction was demonstrated during this task.
- Android and .NET toolchain versions pinned in `tasks/T002-scaffold.md` section 1 are known-good, conservative choices. If a pinned version is unavailable on the build machine, T002 instructs the implementer to report a blocker rather than substitute a version.

### Round 2 corrections (response to `reviews/T001-sol.md`, round 1)

All six P1 findings and the single P2 finding were resolved. Each was resolved as a design decision propagated across every affected document, not as a local edit.

| Finding | Resolution | Documents changed |
| --- | --- | --- |
| P1-1 Approval primitives not implementable | All asymmetric signatures are now ECDSA P-256 with SHA-256, DER, low-S normalized, because `AndroidKeyStore` offers no Ed25519 through `KeyPairGenerator` and .NET 8 has no Ed25519 primitive. Desktop approval is re-specified as `attestationKind: LocalVerification`, an honest local verification result with its trust boundary stated, never described as a token or a signature. A new policy `approvals.consequentialAttestationPolicy` defaults to `RequireDeviceSignature` once a phone is paired, routing consequential decisions to the device that can actually sign. | ADR-003 sections 3, 4, 6, 8, alternatives, verification; ADR-005 section 8, alternatives, verification; `WIRE_PROTOCOL.md` `Hello`, `Welcome`, `DeviceSession`, `ApprovalResponse`, error registry, vectors; `THREAT_MODEL.md` A5, T2, T4, T7, R8, release gates 10-11; `BACKLOG.md` T020, T021, T023 |
| P1-2 TLS exporter not reachable | RFC 5705 exporter binding is removed entirely. The server now speaks first with a single-use, per-connection `Challenge`, and every signature and pairing proof covers it. The Android TLS stack is fixed as OkHttp 4.12.0 with a custom `X509TrustManager` performing SPKI pin comparison, with an explicit note that `CertificatePinner` cannot be used against a self-signed certificate. | ADR-002 sections 1, 5, alternatives, verification; ADR-003 sections 3, 4, 6, alternatives, verification; `WIRE_PROTOCOL.md` sections 1, 5.1, 6.0, 6.4, vectors; `THREAT_MODEL.md` T5; `BACKLOG.md` T003, T004, T020, T021; `T002-scaffold.md`, which now states that T002 adds neither OkHttp nor cryptography |
| P1-3 Queue persistence contradiction | Queues are in-memory only and are never persisted. A Core restart drops them and reports `queueDroppedCount`, a number and not the text. `sessions.json` now enumerates exactly what it stores, and the one prompt-derived field, the session title, is called out explicitly rather than left implicit. | ADR-005 sections 5, 6, alternatives, consequences, verification; ADR-001 section 4; ADR-002 section 5; `WIRE_PROTOCOL.md` `SessionList`, `SessionUpdate`; `THREAT_MODEL.md` T8, R9, release gate 13; `BACKLOG.md` T018, T024; `ARCHITECTURE.md` |
| P1-4 Explicit-session invariant unenforced | `RequestConfirmation.sessionId` is now required and non-null, with a new `SESSION_REQUIRED` code. A session is obtainable only through an explicit `NewSession` or `ResumeSessionRequest`; `Destination.defaultSessionId` is renamed `resumableSessionId` and is informational. Concurrent `NewSession` is defined: envelope idempotency plus a 2 s per-destination coalescing window. The confirmation MAC no longer contains a nullable field, removing an encoding ambiguity. | ADR-005 sections 1, 2, 3, alternatives, consequences, verification; `WIRE_PROTOCOL.md` `Destination`, `RequestConfirmation`, `SessionUpdate`, `NewSession`, sequence 6.1, state machine, error registry, vectors; `BACKLOG.md` T018, T019; `ARCHITECTURE.md` |
| P1-5 Preemption overlap and budget inconsistency | The single-slot invariant is now absolute: the next lease is granted only on acknowledged cancellation or verified process exit, never on a timer alone. The preemption sequence is a four-outcome ladder bounded at 160 ms, ending in `GPU_SLOT_STUCK` and a failed job rather than concurrency. Preemption is moved off the measured path by opening the GPU interactive window at `StartCapture`, and utterances below 250 ms are rejected as accidental taps, so contention cannot reach a G2 or G3 sample. Two benchmark scenarios, `active-tts-to-hotkey` and `stuck-tts-to-hotkey`, plus a release gate, now cover the path. | ADR-004 sections 4, 5, alternatives, consequences, verification; `LATENCY_BUDGET.md` sections 2, 3, 7, 8; `BENCHMARK_PLAN.md` sections 2.4, 4, 6.1, 9; `WIRE_PROTOCOL.md` `StartCapture`, `EndCapture`, state machine, error registry; `THREAT_MODEL.md` release gate 14; `BACKLOG.md` T007, T011, T028 |
| P1-6 Provider credential ownership | Optimus now stores no provider credential at all. Adapters drive a CLI the user authenticated in the provider's own tool, must not read, copy, transform, persist, inject, log, or display any provider secret, and report `AuthRequired` and `PROVIDER_AUTH_REQUIRED` instead. Provider credentials are removed from the asset list and reframed as explicitly not an Optimus asset. | ADR-003 section 8; ADR-001 section 1; `SENDER_ADAPTER.md` sections 1, 5, 6, 7, 11; `WIRE_PROTOCOL.md` `Destination`, error registry; `THREAT_MODEL.md` A7, T2, release gate 12; `BACKLOG.md` T016, T017; `ARCHITECTURE.md` |
| P2-1 Overstated `displayedText` claim | The claim is narrowed to what the echo actually proves: a binding check against honest client defects, not an attestation of rendering. Clients must build one immutable confirmation view model feeding both render and echo. A compromised paired client presenting misleading UI is now residual risk R8. | ADR-005 section 3, alternatives, verification; `WIRE_PROTOCOL.md` `ConfirmationRequest`; `THREAT_MODEL.md` R8; `BACKLOG.md` T005 |

Two consistency corrections were made while propagating the above. Both are recorded rather than left silent:

- ADR-002 section 4 listed `CreateSession` and `ResumeSession` among idempotent device messages; neither is a wire message. The list now names the actual message types.
- An interim edit introduced an `awaitingSession` device state. That would have added an eleventh user-visible state beyond the ten in `PROJECT_PLAN.md`, so it was removed. Destination and session selection are instead specified as occurring inside `cleaning`, which previously had an undefined gap between `PromptDraft` and `ConfirmationRequest`.

### Round 2 commands and results

Commands executed, in order:

```
git log --oneline -6
git show --stat 4846a72
git show 4846a72:reviews/T001-sol.md
grep -rn "exporter|EXPORTER|RFC 5705|Ed25519|defaultSessionId" docs tasks
grep -rn "ADR-003 section" docs tasks
grep -rhoE "ADR-00[1-5] section [0-9]+" docs tasks | sort -u
grep -oE "^### [0-9]+\." docs/adr/ADR-00*.md
grep -oE "^#### .[A-Za-z]+." docs/specs/WIRE_PROTOCOL.md
python  (error-code audit: every SCREAMING_SNAKE_CASE code used in docs and tasks against the WIRE_PROTOCOL section 8 registry)
git add -A
git diff --check
git diff --cached --check
git status --short
```

Results:

- Stale-term sweep: the only remaining occurrences of `Ed25519` and `RFC 5705` are the four sentences in ADR-003 that record why each was rejected. No specification text depends on either. `defaultSessionId` and every `exporter` field are gone.
- Section-reference audit: every `ADR-00N section M` reference resolves to an existing numbered section. ADR-003 gained a section, so its old sections 5, 6, and 7 became 6, 7, and 8; the five external references in ADR-001, ADR-002, ADR-005, `WIRE_PROTOCOL.md`, and `THREAT_MODEL.md` were updated to match.
- Error-code audit: every code used anywhere in `docs/` or `tasks/` appears in the `WIRE_PROTOCOL.md` section 8 registry, excluding platform API constants such as `FLAG_SECURE` and `ANDROID_HOME`. Five codes were added: `SESSION_REQUIRED`, `UTTERANCE_TOO_SHORT`, `GPU_SLOT_STUCK`, `PROVIDER_AUTH_REQUIRED`, and `APPROVAL_ATTESTATION_NOT_PERMITTED`.
- Message-catalogue check: all nine public message types named in `PROJECT_PLAN.md` are specified. `Challenge` was added as a connection-level message, and `PairChallenge`, `PairRequest`, and `PairResult` are pointed at ADR-003 section 4 from the transport summary.
- Latency arithmetic re-verified after the preemption change. The segment tables are unchanged, so G1 42+8=50, G2 235+15=250, G3 455+45=500, G4 187+13=200, phone LAN 470+30=500, and Tailscale 510+50=560 all still hold. The 10 ms ASR admission allocation is now justified in `LATENCY_BUDGET.md` section 3 rather than assumed, and a table-formatting break introduced during editing was found and fixed.
- `git diff --check` and `git diff --cached --check`: no output, exit code 0.
- `git status --short`: only files under `docs/` and `tasks/` are modified. No build output, secrets, models, audio, or benchmark output.

### Round 2 known limitations

- The OkHttp custom-trust-manager path and the `AndroidKeyStore` P-256 biometric binding are specified from documented platform behavior. Neither is yet demonstrated on the Pixel 9a. T021 and T023 carry that proof and their completion gates name it. If either proves unworkable in practice, that is an Opus escalation and an ADR amendment, not an implementation workaround.
- `LocalVerification` is deliberately weaker than `DeviceSignature`. The product now says so, defaults away from it once a phone is paired, and records it as R8. It is not a defence against a compromised same-user machine and is not presented as one.
- Dropping queues on a restart is a deliberate privacy-over-convenience trade, recorded as R9. If durable queues later prove product-critical, ADR-005 requires an amendment covering opt-in, encryption, retention, deletion, crash-dump, and backup behavior.
- The 250 ms minimum utterance is derived from the 160 ms worst-case preemption plus margin. It is a design value; T011 measures the actual short-tap rate so an unexpectedly high number is visible rather than hidden.
- Round 1 limitations recorded above still stand unchanged: latency figures are allocations rather than measurements, and model winners remain unselected pending target-hardware results.

### Round 3 corrections (response to `reviews/T001-sol-round2.md`)

Both P1 findings and both P2 precision issues are resolved. The two P1 findings were replacement designs from round 2 that did not survive contact with implementability; each is replaced by a design whose steps a process can actually execute.

| Finding | Resolution | Documents changed |
| --- | --- | --- |
| P1-1 Queue-drop count could not survive the restart that destroyed its evidence | The count now has its own durable record containing no prompt content: a per-session integer `pendingQueueDepth` in `sessions.json`. Write ordering is specified and biased so the record can never under-report a loss: the increment is durable before `SendResult` is sent, the decrement is durable after the adapter accepts the send, so a crash leaves the count at most one high, never low. Startup reads the values, copies them into memory, writes `0` back, and flushes **before the listener starts**, so the reset is atomic with respect to devices and a second restart cannot re-report. Delivery is once per connecting device per Core run, not "once" globally, which is what makes it testable with two devices. Queued text, hashes, receipts, and previews remain memory-only. | ADR-005 sections 5, 6, alternatives, consequences, verification; ADR-001 section 4; `WIRE_PROTOCOL.md` `SessionList`, `SessionUpdate`; `THREAT_MODEL.md` T8, release gate 13; `BACKLOG.md` T018, T024; `ARCHITECTURE.md` |
| P1-2 ASR could not both stream during capture and acquire its lease at release | The ASR job is now admitted at `StartCapture` and holds the lease until its `result`, which is what streaming decode actually requires. Frames arriving before the grant are buffered in Core, bounded by `capture.maxPreLeaseBufferMs` (2000 ms), overflowing to `AudioAborted { LeaseUnavailable }`. Cleanup queues behind ASR, which the sequential budget already assumed. Latency segment 2.3 becomes final-frame dispatch to an already-leased runner, and 2.4 becomes finalization including any outstanding lease wait and backlog drain. | ADR-004 sections 2, 3, 4, 5, alternatives, consequences, verification; `LATENCY_BUDGET.md` sections 2, 3, 4, 7, 8; `WIRE_PROTOCOL.md` `StartCapture`, `AudioAborted`, sequence 6.1; `ARCHITECTURE.md`; `BACKLOG.md` T007, T008 |
| P1-2 (b) Short utterances were discarded by duration | The 250 ms rule and `UTTERANCE_TOO_SHORT` are removed entirely. Rejection is now content-based: the ASR runner returns `speechDetected` and `voicedMs` on every job, and only a negative verdict yields `NO_SPEECH_DETECTED`. Any utterance containing speech is transcribed, drafted, confirmed, and counted in G2 and G3 at any duration. The corpus gains a 20-utterance micro bucket (single words under 250 ms) and a 20-utterance sub-second bucket, both gated at the same thresholds, plus 15 non-speech captures used only to test the verdict. The budget arithmetic is stated rather than dodged: for `D < W_preempt` the work at release is `W + decode(D)` with both terms under 60 ms, which fits the existing 130 ms ASR allocation because a shorter utterance carries proportionally less audio. | ADR-004 section 4, alternatives, consequences, verification; `LATENCY_BUDGET.md` sections 3, 8; `BENCHMARK_PLAN.md` sections 2.1, 2.4, 4, 6.1, 8, 9; `WIRE_PROTOCOL.md` `StartCapture`, `EndCapture`, state machine, error registry; `THREAT_MODEL.md` release gate 15; `BACKLOG.md` T008, T011, T028; `ARCHITECTURE.md` |
| P1-2 (c) Escalation clock was self-inconsistent | One bound, named once and quoted everywhere: `W_preempt` = 60 ms, composed of a 20 ms acknowledgement deadline and a 40 ms verified-exit budget, with `GPU_SLOT_STUCK` declared at exactly that same 60 ms. There is no longer a gap between the stated worst case and the point of declared failure. The 20 ms acknowledgement is stated as a requirement on the only preemptible paths, `P1Speech` and `P2Background`, met by a dedicated control-reader thread; non-preemptive cancellation keeps its 100 ms and 500 ms path because nothing waits for the slot. | ADR-004 sections 4, 5, alternatives, consequences, verification; `LATENCY_BUDGET.md` sections 2, 3, 8; `BENCHMARK_PLAN.md` section 2.4; `WIRE_PROTOCOL.md` error registry; `THREAT_MODEL.md` release gate 14; `BACKLOG.md` T007 |
| P2-1 Low-S was verified but never produced | The obligation moves to the signer. A four-step normalization is specified with the exact P-256 order, delivered as one shared helper per platform by T003, exercised by a new `signatures-lows/` vector directory carrying provider-shaped high-S signatures normalized identically by an Android-backed and a .NET-backed signer. Strict canonicalization is kept deliberately over accept-both, so one key and message yield exactly one byte representation. | ADR-003 section 3, verification; `WIRE_PROTOCOL.md` vectors; `BACKLOG.md` T003 |
| P2-2 Two codes for an unknown session | One code per input, with no overlap: `SESSION_REQUIRED` if and only if the field is absent or null, `SESSION_NOT_FOUND` for present but unknown, `Closed`, or wrong-destination. The verification text now names four distinct negative vectors instead of permitting either code. | ADR-005 sections 1, verification; `BACKLOG.md` T019 |

One consequential rename was made while propagating: the runner heartbeat field `queueDepth` became `jobQueueDepth`, because `AgentSession.queueDepth` was replaced by `pendingQueueDepth` and two unrelated fields sharing a name across the runner and session layers would be a genuine source of confusion.

### Round 3 commands and results

```
git log --oneline -6
git show --stat 969a032
git show 969a032:reviews/T001-sol-round2.md
grep -rn "minUtteranceMs|UTTERANCE_TOO_SHORT|160 ms|accidental tap|queueDepth|gpu.admit" docs tasks
grep -rn "W_preempt|60 ms|20 ms acknowledg|250 ms" docs
grep -rhoE "ADR-00[1-5] section [0-9]+" docs tasks | sort -u
grep -oE "^### [0-9]+\." docs/adr/ADR-00*.md
python  (error-code audit: codes used across docs and tasks against the registry)
git add -A
git diff --check
git diff --cached --check
git status --short
```

Results:

- Stale-design sweep: no occurrence of `minUtteranceMs`, `UTTERANCE_TOO_SHORT`, the 160 ms bound, or "accidental tap" remains in `docs/`. The only surviving mentions of the removed design are the explicit rejection paragraphs in ADR-004, which exist so a future reader knows the option was considered and why it was wrong, and the round-2 record in this file, which is left accurate as a historical entry.
- Clock consistency: `W_preempt` = 60 ms is the only preemption bound quoted in ADR-004, `LATENCY_BUDGET.md`, `BENCHMARK_PLAN.md`, and `WIRE_PROTOCOL.md`. The 100 ms and 500 ms figures survive only on the non-preemptive cancellation path, where the ADR states why a longer window cannot cause overlap.
- Section-reference audit: every `ADR-00N section M` reference resolves to an existing numbered section. No renumbering was needed this round.
- Error-code audit: `UTTERANCE_TOO_SHORT` was removed from the registry and every use; `NO_SPEECH_DETECTED` replaces it. The one remaining occurrence in this file is inside the round-2 record, which describes what round 2 did and is deliberately not rewritten.
- Latency arithmetic re-verified after the lifecycle change. Only the meaning of segments 2.3 and 2.4 changed, not their values, so G1 42+8=50, G2 190+20+25+15=250, G3 455+45=500, G4 187+13=200, phone LAN 470+30=500, and Tailscale 510+50=560 all still hold.
- Corpus arithmetic re-verified after adding buckets: 20+20+40+70+40+25+15+10 = 240 utterances, and the per-candidate sample count in section 4 was updated from 2000 to 2400 to match. The 15 non-speech captures are counted and scored separately.
- `git diff --check` and `git diff --cached --check`: no output, exit code 0.
- `git status --short`: only files under `docs/` and `tasks/` modified. No build output, secrets, models, audio, or benchmark output.

### Round 3 known limitations

- The claim that a sub-60 ms utterance finishes inside the 130 ms ASR allocation is arithmetic over the gate-mandated RTF plus fixed overhead, not a measurement. `BENCHMARK_PLAN.md` section 2.4 now runs the contention scenarios at 0.15 s specifically to test it, and `gpu.leaseWait` is recorded on every run. If the arithmetic is wrong, the resolution is model or runtime placement, or an explicit budget amendment; excluding the sample is no longer an available answer.
- The 20 ms preemptive acknowledgement is a real constraint on how the TTS and background loops are written, not a free parameter. It is called out in ADR-004 consequences and in the T007 and T014 gates. If a selected TTS runtime cannot meet it, that is an Opus escalation.
- `pendingQueueDepth` can over-report by one after a crash inside a write window. This is deliberate and stated: telling a user that nothing was lost when something was is the worse failure. The bias direction is asserted by fault-injection tests in T018.
- Round 1 and round 2 limitations still stand: latency figures remain allocations rather than measurements, model winners remain unselected pending target-hardware results, and the OkHttp and `AndroidKeyStore` paths remain specified from documented platform behavior rather than demonstrated on the Pixel 9a.

## Review history

- Round 1: `reviews/T001-sol.md`
- Reviewed commit: `122cbbf`
- Verdict: `CHANGES_REQUIRED`
- Blocking findings: Android/Windows approval primitives, Android TLS-exporter feasibility, queue privacy contradiction, explicit-session enforcement, GPU preemption/latency consistency, and provider credential ownership.
- Round 2: corrections committed on this branch. All six round-1 P1 findings and the round-1 P2 finding were addressed.
- Round 2 review: `reviews/T001-sol-round2.md`, reviewed commit `7d72be1`, verdict `CHANGES_REQUIRED`. Five round-1 findings confirmed resolved; two replacement designs (queue-drop reporting, ASR lease lifecycle and short-utterance policy) carried blocking contradictions, plus two precision issues.
- Round 3: corrections committed on this branch. Both P1 findings and both P2 findings are addressed as recorded above. Awaiting round-3 review by GPT-5.6 Sol against the new branch head.
- Round 2 review: `reviews/T001-sol-round2.md`
- Reviewed branch head: `7d72be1`
- Verdict: `CHANGES_REQUIRED`
- Remaining blockers: restart-safe queue-drop reporting and a coherent streaming-ASR/GPU-lease lifecycle without discarding deliberate sub-250 ms commands. P2 corrections are also required for signing-side low-S behavior and deterministic unknown-session errors.
