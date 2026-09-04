# Dependency-ordered implementation backlog

- Status: Accepted
- Owner: Claude Opus 5
- Governing documents: `AGENTS.md`, `PROJECT_PLAN.md`, ADR-001 through ADR-005

Every task below is a merge unit: one branch, one worktree, one implementer, one Sol review, one non-fast-forward merge into `main` (`docs/WORKFLOW.md`).

Owner assignment follows `AGENTS.md`. A task is owned by Claude Opus 5 when it changes an accepted architecture decision or public protocol, touches three or more subsystems, affects confirmation integrity, credentials, pairing, approvals, privacy or deletion, or alters concurrency, process ownership, GPU scheduling, or recovery semantics. Everything else is Gemini 3.8 Flash. GPT-5.6 Sol reviews every task and implements none.

A task contract is written by Claude Opus 5 and set to `READY` before its worktree is created. Only T002 is written as part of T001; the rest are drafted from this backlog when their dependencies land.

## Milestone 1 — Architecture and scaffold

| ID | Title | Owner | Reviewer | Depends on | Owned subsystem | Completion gate |
| --- | --- | --- | --- | --- | --- | --- |
| T001 | Architecture foundation and implementation backlog | Opus | Sol | none | `docs/adr/`, `docs/specs/`, `docs/security/`, `docs/performance/`, `docs/BACKLOG.md`, `tasks/` | Every T001 deliverable exists, links resolve, `tasks/T002-scaffold.md` is decision-complete |
| T002 | Repository, solution, and application scaffold | Gemini | Sol | T001 | Solution and project files, `Directory.Build.props`, Android Gradle project, test projects, architecture tests | `dotnet build`, `dotnet test`, `gradlew assembleDebug`, `gradlew test` all pass; dependency-direction tests enforce ADR-001 sections 5 and 6 |

## Milestone 2 — Desktop capture, protocol, and state machine

| ID | Title | Owner | Reviewer | Depends on | Owned subsystem | Completion gate |
| --- | --- | --- | --- | --- | --- | --- |
| T003 | Protocol contracts, canonical encoding, and conformance vectors | Gemini | Sol | T002 | `Optimus.Contracts`, Android `:core:protocol`, `tests/protocol-vectors/` | Both platforms pass every vector directory in `docs/specs/WIRE_PROTOCOL.md` section 9, including the `signatures/` ECDSA P-256 vectors, the `signatures-lows/` low-S normalization vectors cross-checked between an Android-backed and a .NET-backed signer, and the non-null `sessionId` confirmation vectors |
| T004 | Kestrel host, device endpoint, negotiation, resume, error model | Gemini | Sol | T003 | `Optimus.Service`, device endpoint, `Optimus.Client` | Integration tests in ADR-002 section "Verification" pass; the server sends `Challenge` first and rejects a reused or unissued challenge; listener binds loopback only |
| T005 | Desktop shell, hotkey, floating widget, and state machine | Gemini | Sol | T004 | `Optimus.Shell` | All ten device states in `docs/specs/WIRE_PROTOCOL.md` section 7 render; the confirmation view model is immutable and feeds both render and echo; G1 measured below 50 ms p95 |
| T006 | WASAPI capture pipeline, VAD, framing, and playback | Gemini | Sol | T005 | `Optimus.Shell` audio components | 20 ms framing matches the binary header spec; no audio path writes to disk; G1 holds with capture active; simultaneous desktop/phone capture admits exactly one globally and returns `CAPTURE_ALREADY_ACTIVE` to the other |
| T007 | Runner supervisor, named-pipe transport, and GPU scheduler | **Opus** | Sol | T004 | `Optimus.Inference` | All ADR-004 verification tests pass, including the four preemption outcomes at the 20/40/60 ms boundaries; `cancel-received` never releases a lease; terminal `cancelled` proves GPU quiescence; counters prove no concurrent leases or GPU execution; the ASR lease is held from capture start to `result`; pre-lease buffering and the 5-in-5 terminal restart rule pass |

## Milestone 3 — Speech, cleanup, and model selection

| ID | Title | Owner | Reviewer | Depends on | Owned subsystem | Completion gate |
| --- | --- | --- | --- | --- | --- | --- |
| T008 | ASR runner host and streaming transcription | Gemini | Sol | T006, T007 | `runners/asr`, `IAsrRunner` | Streaming decode under a lease held for the whole utterance, with a flat tail-latency slope; `speechDetected` reported on every job and driving `NO_SPEECH_DETECTED`; single-word commands at 150 ms transcribe successfully; cancelled jobs yield no partial transcript |
| T009 | Cleanup runner, prompt template, and intent guard | Gemini | Sol | T008 | `runners/cleanup`, `ICleanupRunner` | Golden-suite harness runs; cleanup failure degrades to the raw transcript with the confirmation gate intact |
| T010 | Glossary, normalization, and identifier formatting | Gemini | Sol | T009 | Core text pipeline, glossary storage and settings UI | Glossary corrections are acoustically grounded and never introduce content absent from the transcript |
| T011 | Benchmark harness, corpora, and reporting | Gemini | Sol | T009 | `benchmarks/`, harness CLI, `--verify` and `--regression` modes | Produces every artifact in `docs/performance/BENCHMARK_PLAN.md` section 7, runs the `active-tts-to-hotkey` and `stuck-tts-to-hotkey` scenarios at all four utterance lengths, records `gpu.leaseWait` and `asr.backlogDrain` per run, and reproduces a recorded run |
| T012 | ASR and cleanup model selection | **Opus** | Sol | T011 | `ADR-006`, model configuration defaults | Winners chosen only from recorded target-hardware results; every correctness gate passes |

## Milestone 4 — Voice identity, TTS, and event summaries

| ID | Title | Owner | Reviewer | Depends on | Owned subsystem | Completion gate |
| --- | --- | --- | --- | --- | --- | --- |
| T013 | Voice design setup flow and exclusive mode | Gemini | Sol | T007, T012 | Voice design UI, `runners/voicedesign` | Entering the mode suspends capture and exits only when all warm runners are `Ready`; no imitation of an identifiable person |
| T014 | TTS runner, streaming playback, pre-render cache, and selection | Gemini | Sol | T013 | `runners/tts`, `ITtsRunner`, `ADR-007` input data | G4 gates pass on target hardware; preempted utterances resume at chunk granularity |
| T015 | Event summary engine, coalescing, and rate limiting | Gemini | Sol | T014 | Core summary engine | Speech policy in `docs/specs/SENDER_ADAPTER.md` section 9 is enforced; summaries route to the originating device only |

## Milestone 5 — Provider adapters and session semantics

| ID | Title | Owner | Reviewer | Depends on | Owned subsystem | Completion gate |
| --- | --- | --- | --- | --- | --- | --- |
| T016 | Sender-adapter contract, conformance suite, and Claude Code adapter | Gemini | Sol | T004 | `Optimus.Providers`, `Optimus.Providers.Conformance`, `Providers/ClaudeCode/` | Conformance suite passes; `MAPPING.md` completeness test passes; hostile-output corpus causes no state transition; the credential-boundary recorder shows no provider credential is read, constructed, or stored |
| T017 | Codex adapter | Gemini | Sol | T016 | `Providers/Codex/` | Same conformance suite passes, including the credential boundary, with no change to Core, protocol, or UI |
| T018 | Session store, Queue, Steer, questions, and cancellation | **Opus** | Sol | T016 | Core session subsystem | All ADR-005 section 5 and 6 verification tests pass; session metadata survives a Core restart; queue content does not and is normally reported through durable `pendingQueueDepth`; fault tests cover both write windows and the documented double-startup notice limitation; `NewSession` coalescing and idempotency behave as specified |
| T019 | Confirmation authority and destination binding | **Opus** | Sol | T018 | Core confirmation subsystem | Every one of the ten ordered checks has a passing negative test; `RequestConfirmation` maps absent to `SESSION_REQUIRED` and unknown, closed, or wrong-destination to `SESSION_NOT_FOUND`, one code per input, creating no provider session; concurrent double-send admits exactly one |

## Milestone 6 — Secure service, pairing, and the phone

| ID | Title | Owner | Reviewer | Depends on | Owned subsystem | Completion gate |
| --- | --- | --- | --- | --- | --- | --- |
| T020 | PC identity, QR pairing, device trust, and revocation | **Opus** | Sol | T019 | Core security subsystem, pairing endpoint, tray devices UI | All ADR-003 verification tests pass; P-256 signature validation rejects non-canonical DER and high-S; challenge reuse, replay, expiry, and lockout behave as specified; the attestation policy defaults flip correctly when a phone is paired |
| T021 | Android application foundation, transport, pinning, and pairing | Gemini | Sol | T020 | `:app`, `:core:transport`, `:core:security`, `:feature:pairing` | Pairs with the target PC over LAN and Tailscale using the OkHttp stack and custom SPKI trust manager fixed in ADR-003 section 3; hardware-backed P-256 keys generated; `allowBackup` false |
| T022 | Android Talk screen: capture, transcript, draft, destination, confirmation | Gemini | Sol | T021 | `:core:audio`, `:feature:talk` | Confirmation card renders the exact text and echoes it byte-for-byte; `FLAG_SECURE` set |
| T023 | Android Sessions screen, approvals, and biometric attestation | Gemini | Sol | T022 | `:feature:sessions` | Consequential approvals produce a `DeviceSignature` through a `BiometricPrompt`-bound `CryptoObject`; signatures bound to the approval nonce and connection challenge; replayed responses rejected; notifications carry no draft text |
| T024 | Privacy, retention, history, and opt-in calibration set | **Opus** | Sol | T022 | Core history and privacy subsystem, settings UI | Filesystem sweep finds no audio outside the calibration directory and no queued prompt text, hash, receipt, or preview in `sessions.json`, which carries only the `pendingQueueDepth` integer; clear-history and per-recording delete work |

## Milestone 7 — Packaging, recovery, performance, and release

| ID | Title | Owner | Reviewer | Depends on | Owned subsystem | Completion gate |
| --- | --- | --- | --- | --- | --- | --- |
| T025 | Diagnostics, tray settings, and local troubleshooting | Gemini | Sol | T024 | Tray settings, diagnostics export | Export contains counters and coded events only; last-run latency breakdown is visible |
| T026 | Installer, model distribution, and integrity verification | Gemini | Sol | T025 | Installer project, model manifests | Clean-machine install reaches a working warm path; a flipped weight byte blocks load |
| T027 | Failure recovery hardening across all processes | **Opus** | Sol | T026 | Cross-process recovery paths | Every recovery behavior in ADR-001 section 4 and ADR-004 section 6 is proven by a kill test |
| T028 | Performance hardening against the latency budget | **Opus** | Sol | T027 | Warm-path hot spots across subsystems | G1 to G4 pass at p95 on target hardware with the full stack running, including G2 and G3 under `active-tts-to-hotkey` and for the micro and sub-second command buckets, with zero observed concurrent GPU leases and no sample excluded by duration |
| T029 | Release audit: security, privacy, and acceptance gates | **Opus** | Sol | T028 | Release checklist and audit report | Every item in `docs/security/THREAT_MODEL.md` section 8 passes; end-to-end success on the target PC and Pixel 9a over LAN and Tailscale |

## Parallelism

Per `docs/WORKFLOW.md`, parallel work requires non-overlapping ownership, dependencies already on `main`, and separate worktrees. The following pairs are pre-approved once their dependencies are merged:

| Pair | Condition |
| --- | --- |
| T005 and T007 | Both depend only on T004; `Optimus.Shell` and `Optimus.Inference` do not overlap |
| T011 and T010 | Harness and text pipeline touch different files; T011 must not modify the cleanup runner |
| T016 and T013 | Providers and voice design share no files |
| T021 and T017 | Android modules and the Codex adapter share no files |

Everything else runs sequentially. In particular, T018, T019, and T020 are strictly serial: each defines interfaces the next consumes.

## Escalation rules

- Two unsuccessful Gemini repair attempts on one finding escalate the task to Claude Opus 5 (`AGENTS.md`).
- A blocked or contradictory contract is reported to Opus rather than resolved by the implementer.
- A missed mandatory performance gate is a P1 finding and blocks its task; it is never resolved by relaxing the gate in an implementation task.
- Any change to an accepted ADR, the wire protocol, the adapter contract, or the latency budget is an Opus task with its own ADR amendment.
