# T001 — GPT-5.6 Sol review, round 3

- Reviewer: GPT-5.6 Sol
- Task contract: `tasks/T001-architecture.md`
- Previous review commit: `969a032`
- Correction commit: `92b939e`
- Reviewed branch head: `1066a93`
- Round: 3

## Evidence reviewed

- Complete `969a032..1066a93` diff: 11 files, 227 insertions, 80 deletions.
- Final ADR, wire-protocol, threat-model, latency, benchmark, backlog, and task text for every round-2 finding.
- `git diff --check 969a032..HEAD`: clean, exit 0.
- `git status --short`: clean before this review was written.
- Targeted consistency sweeps for preemption acknowledgement/quiescence, ASR lease lifetime, concurrent capture, durable queue-drop reporting, low-S signing, and exact session error mapping.

## Round-2 finding disposition

- Queue content privacy: resolved. Prompt bodies, hashes, receipts, and previews remain memory-only, while a count-only record is explicitly permitted.
- Streaming ASR lifecycle: resolved. The ASR job now obtains its lease at capture start and retains it through final result.
- Duration-based rejection: resolved. Short voiced commands are retained and benchmarked; no-speech rejection is content-based.
- Preemption clock arithmetic: the 20/40/60 ms clock is now internally aligned, but the acknowledgement semantics violate GPU exclusivity; see P1-1.
- Low-S signing: resolved with an exact signing-side normalization algorithm and shared cross-platform vectors.
- Session error mapping: resolved with one deterministic code per input class.

## Findings

### P0

None.

### P1

#### 1. Resolved in review closeout: cancellation acknowledgement was not GPU quiescence

`docs/adr/ADR-004-inference-lifecycle-and-gpu-scheduling.md:134` says the next lease cannot be granted while the previous holder might still touch the GPU. Line 141 grants the next lease as soon as `cancelled` arrives. But lines 78, 147, and 195 require a dedicated control thread to emit `cancelled` **before unwinding**, explicitly allow an already-launched kernel to continue, and only say the runner will stop launching new work. The 40 ms verified-exit path is used only when no acknowledgement arrives. Therefore an early acknowledgement can cause Core to grant the ASR lease while the previous kernel is still running—the exact overlap the single-slot invariant forbids.

Resolution applied in the review closeout commit: the runner protocol now separates immediate `cancel-received` from terminal `cancelled`. Only terminal `cancelled`, emitted after all GPU work is quiescent, or verified process exit can transfer the lease. A long-kernel fault test must prove zero overlapping GPU execution, not merely zero overlapping lease objects.

### P2

#### 1. Resolved in review closeout: cross-device capture arbitration was unspecified

`docs/specs/WIRE_PROTOCOL.md:524` defines `CAPTURE_ALREADY_ACTIVE` only when a stream is already open “for this device.” That permits the desktop and phone to start ASR captures simultaneously. P0 does not preempt P0, so the second capture can wait behind the first for up to the 20-second utterance limit, not `W_preempt = 60 ms`; its 2-second pre-lease buffer then overflows. `AudioAborted.LeaseUnavailable` incorrectly calls this a stuck GPU slot even though the slot may be healthy and serving the first capture. The short-utterance latency reasoning also assumes no same-priority ASR job is ahead.

Resolution applied in the review closeout commit: Core permits one global capture. A simultaneous phone/desktop start returns retryable `CAPTURE_ALREADY_ACTIVE` immediately, and T006 owns the concurrency test.

### P3

#### 1. Accepted v1 limitation: a second startup crash can lose an undelivered queue-drop notice

The count-only recovery record correctly preserves privacy and normally reports dropped prompts. Persisting a per-device notice ledger solely for the window between startup reset and first reconnect is disproportionate for v1. ADR-005 and the threat model now state this narrow repeated-startup-failure case honestly, and T018 must test and report it. It does not block the scaffold or the core user path.

## Contract compliance

- Scope: Pass. Changes remain documentation and task contracts only.
- Round-2 corrections: The requested mechanisms are present and materially improved.
- GPU single-slot invariant: Pass after the reviewer-applied terminal-quiescence clarification.
- Restart recovery: Pass for v1 with the double-startup notice edge explicitly accepted and assigned to T018 diagnostics.
- Public error semantics: Pass after global capture exclusivity was specified.
- Required structural checks: Pass.
- Unrelated changes: None found.

## Verdict

`PASS`

The architecture foundation is sufficiently decision-complete to begin T002. The reviewer applied the one safety-critical clarification directly to avoid another design loop; the remaining notice edge is documented and non-blocking.
