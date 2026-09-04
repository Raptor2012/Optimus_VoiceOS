# T001 — GPT-5.6 Sol review, round 2

- Reviewer: GPT-5.6 Sol
- Task contract: `tasks/T001-architecture.md`
- Previous review commit: `4846a72`
- Correction commit: `a1ddef4`
- Reviewed branch head: `7d72be1`
- Round: 2

## Evidence reviewed

- Complete `4846a72..7d72be1` diff: 14 files, 468 insertions, 149 deletions.
- All documents named in the round-1 resolution table, including the amended ADRs, wire protocol, threat model, performance documents, backlog, and T002 contract.
- `git diff --check 4846a72..HEAD`: clean, exit 0.
- `git status --short`: clean before this review was written.
- Stale-design sweeps for Ed25519, TLS exporters, nullable confirmation sessions, provider-owned credentials, queue persistence, GPU admission, and approval attestation.
- Cross-check of the amended behavior against `PROJECT_PLAN.md` and the user's stated no-guessing requirement, especially the global hold-to-talk path and G2/G3.

## Round-1 finding disposition

- P1-1 approval primitives: materially resolved by P-256 device signatures and an honestly bounded desktop `LocalVerification` policy.
- P1-2 TLS exporter: materially resolved by the server-first per-connection challenge design and a concrete OkHttp pinning path.
- P1-3 queue privacy: prompt bodies are now consistently memory-only, but the replacement restart-notification contract is not implementable; see P1-1 below.
- P1-4 explicit sessions: materially resolved by explicit `NewSession`/`ResumeSessionRequest` and a non-null confirmation session.
- P1-5 GPU overlap: concurrent leases are now prohibited, but the replacement ASR lifecycle and short-utterance policy are inconsistent; see P1-2 below.
- P1-6 provider credentials: resolved; Optimus no longer owns or injects provider authentication material.
- P2-1 rendered-text claim: resolved; the document now states the honest client-defect boundary and residual compromised-client risk.

## Findings

### P0

None.

### P1

#### 1. Exact queue-drop reporting cannot survive the restart that destroys its only evidence

`docs/adr/ADR-005-confirmation-sessions-and-approvals.md:160` says persisted `queueDepth` is always `0`. Lines 191-192 say the queue exists only in memory, yet after a Core restart each affected session reports the exact `queueDroppedCount`. `docs/specs/WIRE_PROTOCOL.md:300-311` and `docs/security/THREAT_MODEL.md:214` make that exact per-session count a required, testable behavior after a forced restart. A process cannot reconstruct a destroyed in-memory count after a crash when the persisted count was deliberately zero, so the contract and release test are impossible.

Required resolution: choose one implementable privacy-preserving design. Either persist only the numeric queue depth per session, atomically reset it on startup, and emit that count exactly once, or emit a generic restart notice with no per-session exact count. Prompt text and previews must remain memory-only. Update the session schema, crash/clean-shutdown semantics, protocol fields, threat model, and T018/T024/T029 tests consistently.

#### 2. The amended ASR lifecycle cannot both stream during capture and acquire its GPU lease after release

`docs/ARCHITECTURE.md:63` and `docs/performance/LATENCY_BUDGET.md:59` require audio to be streamed to the ASR runner and consumed during capture so final-decode latency stays flat through 20-second utterances. But `docs/adr/ADR-004-inference-lifecycle-and-gpu-scheduling.md:140` says the ASR job is admitted only at hotkey release, while lines 119 and 144 prohibit any GPU work without the sole lease. Those statements cannot be implemented together. The resulting 10 ms release-time admission budget is therefore justified by a lifecycle that contradicts the streaming assumption it depends on.

The workaround at ADR-004 line 142 and `WIRE_PROTOCOL.md:203-215` creates a second product violation: every utterance below 250 ms is guessed to be an accidental tap and discarded. Short deliberate commands such as “run,” “stop,” “yes,” or “no” are valid voice input. Silently excluding them from G2/G3 hides a failure instead of making the fastest voice path work, and conflicts with the user's explicit “No guessing” requirement.

Required resolution: specify one coherent job lifecycle. A natural design is to open the interactive window at capture start, grant the ASR job the lease as soon as preemption completes, buffer frames until then, and stream/decode while the key is held; release then finalizes the already-running job. Do not infer intent from duration. A no-speech/VAD cancellation is acceptable, but non-empty short speech must be processed and included in performance data. Add short-command buckets below 250 ms and count successful short captures in G2/G3. If they cannot meet the gate under TTS contention, change model/runtime placement or amend the budget explicitly rather than discarding the sample.

Also make the escalation clock exact: ADR-004 lines 127-129 currently wait 100 ms, then describe a 60 ms exit budget, but do not declare failure until 250 ms while still calling 160 ms the worst case. Define what happens from 160-250 ms and use one consistent bound in the ADR, protocol, budget, and benchmarks.

### P2

#### 1. Low-S verification is specified, but low-S production is not

`docs/adr/ADR-003-pairing-authentication-and-secrets.md:53` rejects every high-S ECDSA signature, while the rest of the protocol merely asks Android Keystore and CNG to produce DER ECDSA signatures. ECDSA providers are not contractually required by this document to return low-S. The round-2 evidence says signatures are “low-S normalized,” but no signing-side normalization algorithm or owner is specified. A valid provider-produced signature can therefore be rejected by the peer.

Required resolution: either require every signer to DER-decode and replace `s` with `n - s` when `s > n/2` before transport, with exact P-256 order and vectors, or accept both canonical DER forms and remove the high-S rejection. Assign the shared normalization/validation helper to T003 and cover both Android- and .NET-produced signatures.

#### 2. `RequestConfirmation` assigns two different errors to an unknown session

`docs/adr/ADR-005-confirmation-sessions-and-approvals.md:31` says a null **or unknown** session fails with `SESSION_REQUIRED`; the session-resolution rules immediately below and `docs/specs/WIRE_PROTOCOL.md:260` say an unknown session fails with `SESSION_NOT_FOUND`. The verification text at ADR-005 line 329 permits either, which defeats the protocol's stated deterministic first-failure semantics.

Required resolution: use `SESSION_REQUIRED` only for absent/null and `SESSION_NOT_FOUND` for unknown, closed, or wrong-destination IDs, or document another single exact mapping. Update the ordered validation and negative vectors so each input has one expected code.

### P3

None.

## Contract compliance

- Scope: Pass. The correction remains documentation and task contracts only.
- Original round-1 issues: Five are resolved; two replacement designs retain blocking contradictions.
- Interfaces and invariants: Fail on queue restart reporting and ASR job ownership/admission.
- Performance methodology: Fail because valid short utterances are removed from the measured population by policy.
- Required checks: Diff and workspace cleanliness pass.
- Unrelated changes: None found.

## Verdict

`CHANGES_REQUIRED`

T002 remains blocked. Claude Opus 5 should resolve the two P1 findings and two P2 precision issues, update T001 evidence, and stop for round-3 GPT-5.6 Sol review. No scaffold or Gemini implementation work should begin from this branch yet.
