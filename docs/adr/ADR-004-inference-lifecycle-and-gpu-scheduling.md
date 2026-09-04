# ADR-004: Inference process lifecycle, model residency, GPU scheduling, cancellation, and crash recovery

- Status: Accepted
- Date: 2026-09-04
- Owner: Claude Opus 5
- Related tasks: T001, T007, T008, T009, T013, T014, T027

## Context

The target machine has an RTX 4070 Laptop GPU with 8 GB VRAM shared with the Windows desktop compositor, the browser, and whatever the coding agents are running. Three models must be warm on the prompt path (ASR, cleanup, TTS), and a fourth, the 1.7B VoiceDesign model, is needed only during setup and must never contend with the warm path.

Loading a model costs seconds; the warm path has a 500 ms total budget. Model residency therefore cannot be demand-driven. At the same time, concurrent CUDA workloads on one consumer GPU cause unpredictable tail latency and VRAM pressure, so concurrency must be scheduled, not left to the driver.

## Decision

### 1. Runner taxonomy

| Runner | Model family | Tier | VRAM budget | Started |
| --- | --- | --- | --- | --- |
| `asr` | Parakeet Unified 0.6B, Qwen3-ASR 0.6B, or Distil-Large-v3 | Warm resident | 1.6 GB | At Core start |
| `cleanup` | Gemma 4 E2B Q4, Gemma 3 1B, or Qwen3.5 0.8B | Warm resident | 1.8 GB | At Core start |
| `tts` | Qwen3-TTS 0.6B or Pocket TTS | Warm resident | 1.2 GB | At Core start |
| `voicedesign` | Qwen3-TTS 1.7B VoiceDesign | Setup only, transient | 3.0 GB | Only from the Voice Design screen |

Configuration values referenced elsewhere in this ADR: `gpu.residentBudgetBytes` (section 1), `capture.maxPreLeaseBufferMs` = 2000 (section 4), `runner.startupTimeoutMs` = 45000 (section 2).

Warm-resident total is 4.6 GB, which is 4.28 GiB of the 8 GiB device, leaving roughly 3.7 GiB for the desktop compositor and other applications. The residency ceiling is a hard configuration value, `gpu.residentBudgetBytes`, default 4 831 838 208 bytes (4.5 GiB). A runner that reports post-warmup VRAM above its budget fails startup with `RUNNER_BUDGET_EXCEEDED` rather than being allowed to squeeze the desktop.

Which model each runner loads is decided by the benchmark in `docs/performance/BENCHMARK_PLAN.md` and recorded in a follow-up ADR (T012). The lifecycle contract in this ADR is identical regardless of the winner.

### 2. Runner process contract

Runners are started by the Core supervisor as child processes in a job object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so no runner can outlive Core.

Command line:

```
optimus-runner-<kind> --pipe <pipeName> --model-id <id> --model-path <dir> --device cuda:0 --log-level warn
```

Transport is a duplex named pipe (`\\.\pipe\optimus-<kind>-<32 hex>`), message mode, DACL restricted to the current user SID, created by Core before spawn.

Frame format on the pipe, little-endian:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | totalLength (bytes after this field) |
| 4 | 1 | kind: 1 = control JSON, 2 = binary payload |
| 5 | 4 | jobId (uint32, 0 for connection-scoped control) |
| 9 | 4 | payloadLength |
| 13 | n | payload |

Control messages, JSON, one per frame:

| Direction | Message | Payload |
| --- | --- | --- |
| runner to core | `ready` | `modelId`, `modelSha256`, `vramBytes`, `warmupMs`, `capabilities` |
| core to runner | `warmup` | `iterations` |
| core to runner | `job` | `jobId`, `kind`, `deadlineMs`, `params` |
| core to runner | `cancel` | `jobId` |
| core to runner | `shutdown` | `graceMs` |
| core to runner | `ping` | `nonce` |
| runner to core | `pong` | `nonce`, `jobQueueDepth`, `vramBytes` |
| runner to core | `partial` | `jobId`, incremental result |
| runner to core | `result` | `jobId`, final result, `computeMs`. An `asr` result additionally carries `speechDetected` (bool) and `voicedMs` (int), the runner's own voice-activity verdict over the submitted audio |
| runner to core | `cancelled` | `jobId` |
| runner to core | `error` | `jobId` or null, `code`, `message`, `fatal` |

Binary payload frames carry PCM into `asr` and audio out of `tts`, using the same `jobId`.

Rules:

- A runner loads exactly one model, chosen by Core, and never selects or downloads a model itself.
- A runner never writes audio, transcripts, or drafts to disk, never opens a network socket, and runs with `%TEMP%` pointed at a per-runner directory created empty and deleted on exit.
- A runner must emit `ready` within `startupTimeoutMs` (default 45 000, which covers a cold first CUDA context) or it is killed.
- A runner processes at most one job at a time. Queueing is Core's responsibility.
- An `asr` job is long-lived: it is opened at capture start and receives binary frames until the frame carrying the `final` flag. `partial` results may be emitted throughout. Every other job is request-response.
- A preemptible runner (`tts`, or any runner executing a `P2Background` job) must read the control pipe on a dedicated thread and emit `cancelled` within 20 ms of a preemptive `cancel`, before unwinding its own state (section 4 rule 2).

### 3. .NET-side contracts

```csharp
public interface IInferenceRunner : IAsyncDisposable
{
    RunnerKind Kind { get; }
    RunnerState State { get; }          // Starting, WarmingUp, Ready, Busy, Degraded, Failed, Stopped
    RunnerInfo Info { get; }            // modelId, modelSha256, vramBytes, warmupMs
    event EventHandler<RunnerStateChanged> StateChanged;
    Task StartAsync(CancellationToken ct);
    Task StopAsync(TimeSpan grace, CancellationToken ct);
}

public interface IAsrRunner : IInferenceRunner
{
    // The caller supplies the GPU lease, acquired at capture start and held until
    // the returned sequence completes. Frames buffered before the lease was granted
    // are yielded first by the caller's channel, in order.
    IAsyncEnumerable<AsrUpdate> TranscribeAsync(
        GpuLease lease, IAsyncEnumerable<AudioChunk> audio, AsrOptions options, CancellationToken ct);
}

// AsrUpdate carries IsFinal, Text, Confidence, and, on the final update,
// SpeechDetected and VoicedMs. A final update with SpeechDetected == false
// produces NO_SPEECH_DETECTED and no draft (section 4).


public interface ICleanupRunner : IInferenceRunner
{
    Task<CleanupResult> CleanAsync(CleanupRequest request, CancellationToken ct);
}

public interface ITtsRunner : IInferenceRunner
{
    IAsyncEnumerable<AudioChunk> SynthesizeAsync(SynthesisRequest request, CancellationToken ct);
}
```

Every method takes a `CancellationToken`, and every request record carries `JobId`, `DeadlineMs`, and `Priority`. There are no long-running methods without a token and no fire-and-forget calls.

`TranscribeAsync` is the only method that takes a `GpuLease` explicitly, because it is the only job whose lease spans a user interaction rather than a single call. Passing the lease makes the lifetime visible in the signature instead of implied by a scheduler convention, and lets the architecture test assert that no ASR call exists without one.

### 4. GPU scheduling

Core owns a single `IGpuScheduler` with one execution slot. Exactly one GPU job runs at a time. This is deliberate: on a single consumer GPU, overlapping a 180 ms cleanup generation with a TTS decode produces worse p95 for both than serializing them, and it makes VRAM headroom predictable.

Priority classes, highest first:

| Priority | Jobs | Preempts |
| --- | --- | --- |
| `P0Interactive` | ASR decode, cleanup generation for a live utterance | Preempts `P1Speech` and `P2Background` |
| `P1Speech` | TTS synthesis for event summaries | Preempts `P2Background` |
| `P2Background` | Warmup beyond the first, pre-render of common phrases, benchmark runs | Nothing |

**The single-slot invariant is absolute.** The next lease is never granted while the previous holder might still touch the GPU. "Might still touch" ends only at one of two verifiable events: an acknowledged `cancelled` message, or observed process exit, which tears down the CUDA context with it. A timer expiring is not one of those events.

Scheduling rules:

1. Admission is by priority, then FIFO within a priority.
2. **Preemption sequence.** When a higher-priority job is queued while a lower-priority job holds the slot, the scheduler:
   1. sends `cancel { jobId }` immediately and starts a **20 ms** acknowledgement timer;
   2. grants the lease as soon as `cancelled` arrives;
   3. at 20 ms with no acknowledgement, terminates the runner process and waits for the process handle to signal, budget **40 ms**, then grants the lease and restarts the runner under section 6;
   4. if the process has still not exited at **60 ms** from the `cancel`, marks the slot `Stuck`, refuses the lease with `GPU_SLOT_STUCK`, and fails the waiting job rather than running it concurrently.

   **`W_preempt` = 60 ms is the single preemption bound** used by this ADR, `docs/specs/WIRE_PROTOCOL.md`, `docs/performance/LATENCY_BUDGET.md`, and `docs/performance/BENCHMARK_PLAN.md`. There is no interval in which two runners hold the GPU, and there is no gap between the stated worst case and the point at which failure is declared: both are 60 ms.

   The 20 ms acknowledgement deadline is a requirement on the only preemptible code paths, `P1Speech` and `P2Background`. Those runners must read the control pipe on a dedicated thread and emit `cancelled` **before** unwinding, then stop launching new GPU work. They are not required to abandon an already-launched kernel, which is why 40 ms of verified-exit budget follows.
3. `P1Speech` preempts `P2Background` by the same sequence.
4. A preempted TTS job is resumable at chunk granularity: Core records the last delivered chunk index and re-issues the remainder as a new job. A preempted benchmark run is discarded and the benchmark reports the interruption rather than the timing.
5. Job deadlines, each measured from a stated origin:

   | Job | Deadline | Measured from |
   | --- | --- | --- |
   | `P0` ASR finalization | 200 ms | `EndCapture`, not lease acquisition |
   | `P0` cleanup generation | 260 ms | lease acquisition |
   | `P1` TTS first chunk | 200 ms | lease acquisition |

   Exceeding a deadline cancels the job and raises a degraded-path event; it never blocks the pipeline. The ASR origin differs because an ASR job is admitted at capture start and lives for the whole utterance (rule 7), so an origin at admission would scale with how long the user spoke.
6. Setup-time VoiceDesign runs as an exclusive mode, section 8.
7. **The ASR job holds the lease for the whole utterance.** This is stated as a scheduling rule because it is the only long-lived `P0` job and it determines how the warm path is measured. See "ASR lease lifecycle" below.

**The interactive window.** Accepting `StartCapture` opens `IGpuScheduler.EnterInteractiveWindow(streamId)`, which:

- immediately runs the preemption sequence against any `P1Speech` or `P2Background` holder;
- bars admission of `P1` and `P2` jobs until the window closes;
- closes when the `PromptDraft` is delivered, or when the utterance is aborted or cancelled, after which `P1` and `P2` admission resumes.

**ASR lease lifecycle.** An earlier draft admitted the ASR job at hotkey release, which contradicted the streaming design that the latency budget depends on: audio cannot be decoded during capture by a runner that does not hold the lease. The lifecycle is therefore:

| # | Moment | Scheduler action |
| --- | --- | --- |
| 1 | `StartCapture` accepted | Enter the interactive window and immediately request the ASR lease at `P0Interactive` |
| 2 | Frames arrive before the lease is granted | Core buffers PCM in pooled memory, bounded by `capture.maxPreLeaseBufferMs` (2000 ms). Overflow aborts the stream with `AudioAborted { code: "LeaseUnavailable" }` |
| 3 | Lease granted, at most `W_preempt` after step 1 | Core opens the ASR job, drains the buffer into it, then streams live frames. Decoding proceeds while the key is held |
| 4 | `EndCapture` | Core writes the final frame with the `final` flag. The runner finalizes; only the tail plus any undrained backlog remains |
| 5 | `result` returned | The ASR lease is released; the cleanup job acquires it next, FIFO within `P0Interactive` |

The lease is held continuously from step 3 to step 5. The single-slot invariant is unaffected: cleanup simply queues behind ASR, which is what the sequential latency budget already assumes.

**Short utterances are never judged by duration.** A previous draft discarded any utterance below 250 ms as an accidental tap. That guessed intent from a clock, and it silently removed valid short commands such as "run", "stop", "yes", and "no" from both the product and the measured population. It is replaced by a content-based rule:

- The ASR runner reports `speechDetected` for every job, derived from its own voice-activity decision over the submitted audio.
- If `speechDetected` is false, Core answers `NO_SPEECH_DETECTED`, produces no transcript and no draft, and closes the window. This is a decision about audio content, not about how long a key was held.
- If `speechDetected` is true, the utterance is transcribed, drafted, confirmed, and **counted in G2 and G3 like any other**, at any duration.

The interaction with `W_preempt` is bounded rather than hidden. Let `D` be the utterance duration and `W` the remaining lease wait at `EndCapture`. For `D >= W_preempt` the lease is already held and `W` is zero. For `D < W_preempt` the whole utterance is still buffered, so the work remaining at release is `W + decode(D)` where `W <= 60 ms` and `D < 60 ms`. At the gate-mandated RTF of at most 0.5 plus fixed per-job overhead, that total stays inside the 130 ms already allocated to ASR finalization in `docs/performance/LATENCY_BUDGET.md` section 3. The budget is therefore met by arithmetic rather than by exclusion, and `gpu.leaseWait` is recorded on every run so the assumption is measured instead of asserted.

The scheduler exposes `Task<GpuLease> AcquireAsync(GpuJobRequest, CancellationToken)`; a lease is released on dispose. All GPU work goes through a lease; a runner call outside a lease is a programming error and is asserted in debug builds and rejected in release builds. The scheduler records, per admission: whether the interactive window was open, whether preemption occurred, which of the four preemption outcomes applied, the admission wait in microseconds, and, for ASR jobs, the lease wait still outstanding at `EndCapture`.

### 5. Cancellation

Cancellation is cooperative with a hard fallback. There are two deadlines, and which one applies depends on whether anything is waiting for the GPU slot:

1. Core cancels the `CancellationToken`; the runner client sends `cancel { jobId }` on the pipe.
2. The runner must abandon the job and reply `cancelled`. The deadline depends on the path: **20 ms** for a preemptive cancel, **100 ms** otherwise. Runners check a cancellation flag between decode or generation steps; preemptible runners additionally acknowledge from a dedicated control-reader thread before unwinding, which is what makes 20 ms achievable.
3. **Preemptive cancellation** (a higher-priority job or an interactive window is waiting for the slot) escalates at 20 ms: terminate, wait for verified exit up to 40 ms, then `GPU_SLOT_STUCK` at 60 ms from the `cancel`. Section 4 rule 2 is normative and its `W_preempt` of 60 ms is the only bound quoted anywhere.
4. **Non-preemptive cancellation** (a user cancel or a deadline expiry with nothing queued behind it) allows the runner **500 ms** to acknowledge before Core marks it `Degraded`, kills it, and restarts it under section 6. The in-flight job fails with `RUNNER_UNRESPONSIVE`. The longer window is safe here precisely because no one is waiting for the slot, so it cannot produce overlap.
5. Cancellation is idempotent; a `cancel` for an unknown or finished `jobId` is a no-op and is acknowledged.

User-visible cancellations map to this path: pressing Escape on the widget, `CancelRequest` from a device, and preemption by a higher-priority job. Releasing the hotkey is **not** a cancellation; it finalizes the running ASR job. An utterance that contained no speech is ended by the `NO_SPEECH_DETECTED` path in section 4, which is a content decision made by the runner, not a cancellation inferred from duration.

An important consequence for correctness: cancelling an ASR or cleanup job discards its partial output entirely. Partial cleanup output is never shown as a draft and never sent, because a truncated generation could change the meaning of the request.

### 6. Health, crash detection, and recovery

- Core pings every runner every **2 s**. Three consecutive misses (6 s) or a pipe break marks the runner `Failed`.
- The supervisor restarts a failed runner with backoff 1 s, 2 s, 4 s, 8 s, 16 s, capped at 30 s.
- More than **5 restarts within 5 minutes** puts the runner in `Failed` terminal state; automatic restarts stop until the user retries from tray diagnostics or Core restarts.
- Each restart re-verifies the model file SHA-256 against the manifest before load. A mismatch is terminal and reported as `MODEL_INTEGRITY_FAILED`; Core never loads a model whose hash does not match.

Degradation policy per runner, chosen so that no failure can violate a product invariant:

| Failed runner | Behavior |
| --- | --- |
| `asr` | Capture is disabled. Hotkey press shows the `error` state with "Speech recognition unavailable". No prompt path exists without a transcript, so nothing is guessed. |
| `cleanup` | The **raw transcript becomes the draft**, the UI marks it "cleanup unavailable", and the normal confirmation gate applies unchanged. This is safe precisely because cleanup may never change intent (`PROJECT_PLAN.md`); its absence therefore cannot change intent either. |
| `tts` | Voice output is disabled and events are shown visually only. A banner reports it once per Core start. |
| `voicedesign` | The Voice Design screen reports failure; exclusive mode ends and the warm path resumes. |

A runner crash never cancels an agent session, never drops a queued prompt, and never auto-retries a user-visible action such as a send.

### 7. Audio and text residency

- PCM buffers are rented from an `ArrayPool<byte>` in Core, and cleared with `CryptographicOperations.ZeroMemory` before return.
- Audio crosses exactly one boundary beyond capture: device to Core, Core to `asr` runner over the pipe. Nothing else receives PCM.
- Runners run with Windows Error Reporting local dump collection disabled for their image names, and Core installs an unhandled-exception handler that writes stack traces and runner state only.
- Core writes no crash dumps of its own process. Diagnostics capture state, counters, and timings, never buffers.
- The only exception to non-persistence is the explicit opt-in calibration set (`PROJECT_PLAN.md`): 30 to 50 recordings written under `%LOCALAPPDATA%\Optimus\calibration\`, each with a metadata record, listed in settings, and individually deletable. Writing to that directory requires an explicit per-recording user action and is impossible while a normal prompt capture is running.
- Cleanup and ASR runners receive text only for the current job and retain nothing between jobs.

### 8. VoiceDesign exclusive mode

The 1.7B model must not contend with the warm path, so it does not merely get a low priority; it gets an exclusive mode:

1. The user opens Voice Design. Core enters `SetupExclusive`.
2. Core refuses new capture (`SERVICE_BUSY_SETUP` on `StartCapture`) and disables the global hotkey via a `ServiceStatus` update. Any in-flight prompt completes first; entering the mode waits up to 10 s for the warm path to drain and otherwise refuses to enter.
3. Core stops the `tts` runner and, if VRAM headroom requires it, the `cleanup` runner, then starts `voicedesign`.
4. On exit, `voicedesign` is stopped, the warm runners are restarted and re-warmed, and Core leaves `SetupExclusive` only after all warm runners report `Ready`.
5. Agent sessions and approvals continue to work throughout; only the voice-capture path is suspended.

Pre-rendering of common phrases (`PROJECT_PLAN.md`) runs as `P2Background` after warmup completes and is cancelled by any `P0` or `P1` job.

### 9. Model store and manifests

```
%LOCALAPPDATA%\Optimus\models\<family>\<version>\
    manifest.json      { modelId, family, version, files:[{path, sha256, bytes}], license, runner, minRunnerVersion }
    <weight files>
```

Core verifies every listed file hash at startup and after every runner restart. Model acquisition is out of scope for the application runtime and is handled by the installer task (T026); Core never downloads models on the prompt path.

## Alternatives considered

- **Demand-loaded models.** Rejected: a multi-second load cannot fit a 500 ms budget, and load-on-first-use would make the first utterance of every session fail its gate.
- **All models in one Python process.** Rejected on crash isolation and because the setup-time 1.7B model could not be excluded from the warm process.
- **Free-running concurrency with CUDA streams.** Rejected: on 8 GB of consumer VRAM, concurrent decode plus generation produced unbounded tail latency in the design analysis, and VRAM headroom would depend on arrival patterns. A single-slot scheduler makes p95 a function of queueing that we control.
- **MPS-style multi-process service or CUDA Green Contexts.** Not available or not dependable on consumer Windows drivers. Rejected as a v1 dependency.
- **Priority by deadline (EDF) instead of fixed classes.** Rejected as over-engineered for three job classes with an order-of-magnitude priority separation; fixed classes are auditable and easy to test.
- **Granting the next lease when the cancellation timer expires, without waiting for exit.** Rejected: it permits two processes to hold CUDA contexts on an 8 GB device simultaneously, which is exactly the VRAM and tail-latency failure the single-slot design exists to prevent. Verified exit is cheap (`TerminateProcess` plus a handle wait) and bounded.
- **Admitting the ASR job at hotkey release.** Rejected because it is not implementable alongside streaming decode: a runner cannot consume audio during capture without the lease, and the flat tail-latency slope that G2 depends on requires exactly that. The ASR job now holds the lease for the utterance.
- **Discarding utterances below a duration threshold.** Rejected on two grounds. It guesses intent from a clock, which the product forbids, and short deliberate commands are valid input. It also concealed a performance question instead of answering it: the honest answer is that a short utterance carries proportionally less audio to decode, so the bounded lease wait fits inside the existing ASR allocation.
- **Keeping separate 100 ms and 250 ms preemption bounds.** Rejected as internally inconsistent. A single `W_preempt` of 60 ms is now quoted everywhere, and the point of declared failure is that same number.
- **Keeping VoiceDesign resident.** Rejected: 3.0 GB plus 4.6 GB exceeds the residency ceiling and would push the desktop compositor into shared memory, which is exactly the tail-latency failure mode the budget cannot absorb.

## Consequences

- Warm-path latency is predictable because only one GPU job runs at a time, and because the interactive window clears the slot at hotkey press rather than at ASR admission.
- Worst-case preemption is `W_preempt` = 60 ms, bounded by verified process exit rather than by a timer alone. The pathological case surfaces as `GPU_SLOT_STUCK` and a failed job, not as silent GPU contention.
- The ASR runner holds the GPU for the duration of every utterance. Cleanup queues behind it, which the sequential budget already assumes, and TTS is barred for the window anyway.
- Voice output can be cut off mid-sentence when the user starts speaking, and it is cut off at press rather than at release. This is the correct trade: the user is the higher priority.
- Preemptible runners must acknowledge a cancel within 20 ms, which constrains how the TTS and background loops are written. This is a real implementation cost and is called out in T007 and T014.
- A key press that captures no speech produces nothing, and the reason given is `NO_SPEECH_DETECTED` rather than a duration judgement. A one-word command is processed normally and appears in the performance data.
- Voice Design temporarily suspends dictation. This is visible and explained in the UI.
- Every runner failure has a defined, invariant-preserving degradation, so a crash never produces a silently different prompt.
- The single-slot design leaves GPU idle time when ASR is waiting on I/O. Measured utilization is a benchmark output, not a target.

## Verification

- T007 unit tests: pipe framing vectors, `ready` timeout, ping-miss detection, restart backoff and the 5-in-5 terminal rule, cancellation acknowledged inside 100 ms, kill after 500 ms, lease required for every GPU call.
- T007 scheduler tests: priority admission order; the four preemption outcomes in section 4 rule 2, each asserted with a fake runner that acknowledges promptly, acknowledges late, exits only on terminate, and never exits, with the 20 / 40 / 60 ms boundaries asserted explicitly; an assertion that no two leases are ever concurrently live, verified by a counter that fails the test on a second grant; deadline expiry cancels rather than blocks; TTS chunk-resume after preemption; `EnterInteractiveWindow` preempts `P1`/`P2` and bars their admission until the window closes.
- T007 and T008 lease-lifecycle tests: the ASR lease is requested at `StartCapture` and released only after `result`; frames arriving before the grant are buffered and then decoded in order with no loss and no reordering; exceeding `capture.maxPreLeaseBufferMs` aborts with `LeaseUnavailable`; cleanup admits only after the ASR lease is released; `gpu.leaseWait` is zero for every utterance longer than `W_preempt`.
- T008 tests: `speechDetected` false yields `NO_SPEECH_DETECTED` with no transcript and no draft; `speechDetected` true yields a transcript at every duration tested, including 150 ms single-word commands, and those runs appear in the G2 and G3 population.
- T011 and T028 measure G2 and G3 with TTS actively speaking at hotkey press (the `active-tts-to-hotkey` scenario in `docs/performance/BENCHMARK_PLAN.md` section 2.4) and record preemption outcome and admission wait for every run.
- T008 and T009 tests assert that a cancelled job yields no partial draft and that a `cleanup` failure produces a raw-transcript draft flagged `cleanupUnavailable` that still requires confirmation.
- T013 tests assert that entering `SetupExclusive` disables capture and that exit restores all warm runners to `Ready` before capture re-enables.
- T024 privacy test enumerates the process working directories, `%TEMP%`, and the application data tree after 50 utterances and asserts that no audio file exists outside the opt-in calibration directory.
- T028 records GPU utilization, VRAM high-water mark, and preemption counts in the benchmark report.

## Related documents

- `docs/performance/LATENCY_BUDGET.md`
- `docs/performance/BENCHMARK_PLAN.md`
- `docs/adr/ADR-001-process-and-subsystem-boundaries.md`
