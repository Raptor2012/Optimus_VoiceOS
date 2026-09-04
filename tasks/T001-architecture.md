# T001 — Architecture foundation and implementation backlog

- Status: READY
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

- Commit:
- Commands executed:
- Results:
- Known limitations:

## Review history

- Review report: Not yet reviewed
- Verdict: Pending
