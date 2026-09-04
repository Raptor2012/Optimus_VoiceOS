# Optimus Voice OS Agent Rules

These instructions apply to every coding agent working in this repository.

## Canonical sources

Read `PROJECT_PLAN.md`, this file, the assigned task contract, and all architecture decisions referenced by the task before changing anything. In a conflict, the newest accepted architecture decision wins over the project plan; the task contract may narrow scope but may not weaken a project invariant.

## Model roles

### Claude Opus 5 — technical lead

Owns architecture, protocols, security, privacy, concurrency, lifecycle design, latency-critical decisions, model-selection rules, difficult debugging, and final integration. Opus writes decision-complete task contracts before implementation begins.

Opus handles any change that:

- Changes an accepted architecture decision or public protocol.
- Touches three or more subsystems.
- Affects confirmation integrity, credentials, pairing, approvals, privacy, or deletion.
- Alters concurrency, process ownership, GPU scheduling, or recovery semantics.
- Follows two unsuccessful Gemini repair attempts.

### Gemini 3.8 Flash — workhorse implementer

Owns bounded implementation tasks: scaffolding, routine production code, UI components, provider wrappers, model runners, tests, fixtures, benchmarks, build scripts, documentation, and packaging.

Gemini must not change architecture decisions, expand task scope, silently add dependencies, or edit files outside the task's ownership. If the contract is insufficient or contradictory, stop and report the exact ambiguity to Opus.

### GPT-5.6 Sol — independent reviewer

Reviews every completed merge unit against its contract and accepted architecture. Sol independently runs relevant checks and writes a report under `reviews/`. Sol does not implement feature code or approve its own changes.

Sol returns exactly one verdict:

- `PASS` — all required evidence exists and no blocking finding remains.
- `CHANGES_REQUIRED` — one or more findings must be addressed.

## Mandatory workflow

1. Opus creates or approves a task contract with status `READY`.
2. One implementation agent works in the task's isolated worktree.
3. The implementer records evidence, commits, and marks the task `IMPLEMENTED`.
4. Sol reviews the full diff and writes a review report.
5. Routine findings return to Gemini. Architecture, security, concurrency, privacy, protocol, or latency-critical findings go to Opus.
6. Sol re-reviews all corrections.
7. Opus integrates only after the latest report says `PASS`.

Never run two agents concurrently in the same worktree. Parallel tasks require separate worktrees, non-overlapping file ownership, and no unresolved dependency between them.

## Review severities

- `P0`: confirmation bypass, credential exposure, arbitrary execution, privacy violation, destructive behavior, or data loss. Blocks all integration.
- `P1`: functional defect, race, protocol incompatibility, session corruption, or missed mandatory performance target. Blocks the task.
- `P2`: missing recovery, inadequate testing, maintainability problem, or accessibility defect. Blocks the milestone.
- `P3`: optional polish. Record in the backlog; does not block release.

## Product invariants

- Never send a prompt without a visible explicit destination and user confirmation.
- Never infer or silently change the destination.
- Cleanup may repair presentation but may not add, remove, or reinterpret intent.
- Ordinary voice processing and audio remain local.
- Routine audio is not retained.
- Agent cloud behavior remains unchanged and is isolated behind sender adapters.
- Approval requests are authenticated, expiring, replay-protected, and bound to the displayed operation.
- The phone is a capture, status, and approval client; the PC is the only inference and agent server.
- Speed is the principal optimization goal only after correctness, confirmation, security, and privacy gates pass.

## Change discipline

- Preserve unrelated user changes.
- Keep commits limited to one task.
- Add tests with behavior changes.
- Record commands and results in the task evidence section.
- Do not commit secrets, model weights, generated audio, benchmark outputs, or machine-local settings.
- Do not declare success when required checks were skipped or failed.
