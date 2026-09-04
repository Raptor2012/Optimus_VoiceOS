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
- Commit: `27d065c` — `T001: architecture foundation, protocol and security specs, and T002 contract`. This commit contains every deliverable. The only later commit on this branch is the one that writes this hash into the evidence, so Sol should review the branch head against the base.

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

## Review history

- Review report: Not yet reviewed
- Verdict: Pending
