# Latency budget

- Status: Accepted
- Owner: Claude Opus 5
- Governing decisions: ADR-001, ADR-002, ADR-004
- Measured by: T005 (capture), T008 (ASR), T009 (cleanup), T014 (TTS), T011 (harness), T028 (hardening)

## 1. Gates this budget serves

From `PROJECT_PLAN.md`:

| Gate | Target |
| --- | --- |
| G1 | Hotkey press to active capture below 50 ms p95 |
| G2 | Hotkey release to final raw transcript below 250 ms p95, utterances up to 20 s |
| G3 | Hotkey release to cleaned visible draft below 500 ms p95 |
| G4 | TTS first audible chunk below 200 ms p95, real-time factor at most 0.5 |

**Scope interpretation.** G1 to G3 are written in terms of the hotkey, which exists only on the desktop. They are therefore the mandatory release gates for the **desktop warm path**. The phone path is measured against derived targets in section 6; those targets are tracked and reported but are not the release gate, because the network segment is a property of the user's LAN or Tailnet rather than of the product. Any change to that interpretation is an architecture decision, not an implementation choice.

"Warm" means: Core running, all three warm runners in `Ready`, models resident, the WASAPI capture client initialized, and the device connection established. Cold-start numbers are reported separately by the benchmark plan and are not gated.

## 2. G1 — hotkey press to active capture (50 ms p95)

| # | Segment | Owner | p95 budget | Measurement point |
| --- | --- | --- | --- | --- |
| 1.1 | Low-level keyboard hook callback to Shell handler | Shell | 4 ms | `capture.hook` span: QPC at hook entry to handler entry |
| 1.2 | State transition, buffer rent, stream id allocation | Shell | 3 ms | `capture.arm` span |
| 1.3 | `IAudioClient.Start()` on a pre-initialized client | Shell | 25 ms | `capture.start` span, ends when `Start()` returns |
| 1.4 | First captured packet available from WASAPI | Shell | 10 ms | `capture.firstPacket` span, ends at first non-empty `GetBuffer` |
| | **Subtotal** | | **42 ms** | |
| 1.5 | Reserve | | 8 ms | |
| | **Total** | | **50 ms** | |

Design notes that make this achievable:

- The Shell keeps the WASAPI capture client **initialized but stopped**. `Start()` on an initialized client avoids format negotiation and buffer allocation. The client is not left running, so the Windows microphone indicator appears only while capture is genuinely active.
- Capture start does **not** wait for Core. The Shell starts the local stream immediately and sends `StartCapture` concurrently, buffering up to 200 ms of audio locally until `CaptureStarted` returns the `streamId`. A `CaptureStarted` failure discards the buffer and shows the error state.
- The hook is registered on a dedicated thread with a message loop that does no work beyond posting to the capture thread, so a slow UI thread cannot delay G1.

## 3. G2 — hotkey release to final raw transcript (250 ms p95)

| # | Segment | Owner | p95 budget | Measurement point |
| --- | --- | --- | --- | --- |
| 2.1 | Capture finalization: stop, drain WASAPI, flush tail frames | Shell | 25 ms | `capture.finalize`, starts at key-up, ends when the last `AudioFrame` is queued |
| 2.2 | Transport of the final frames plus `EndCapture` (loopback) | Shell to Core | 10 ms | `transport.uplinkTail`, ends when Core has the frame carrying `final` |
| 2.3 | ASR job admission: GPU lease acquisition and job dispatch | Core | 10 ms | `gpu.admit(asr)`, from `AcquireAsync` call to runner `job` frame written |
| 2.4 | ASR final decode beyond what streaming already consumed | ASR runner | 130 ms | `asr.finalize`, runner-reported `computeMs` plus pipe round trip |
| 2.5 | Normalization, glossary application, punctuation policy | Core | 15 ms | `text.normalize` |
| | **Subtotal to final transcript in Core** | | **190 ms** | |
| 2.6 | `Transcript` control frame delivery to the device | Core to Shell | 20 ms | `transport.downlink`, ends at device receive |
| 2.7 | Widget text update render | Shell | 25 ms | `ui.render(transcript)`, ends at frame present |
| | **Subtotal visible** | | **235 ms** | |
| 2.8 | Reserve | | 15 ms | |
| | **Total** | | **250 ms** | |

Segment 2.4 assumes streaming ASR: audio is fed to the runner during capture, so only the tail plus final decoding remains at key-up. This is why the 20 s utterance limit in G2 does not change the budget; the streaming portion is absorbed during speech. The benchmark plan verifies this by reporting 2.4 against utterance duration and failing the model if the slope is not flat.

## 4. G3 — hotkey release to cleaned visible draft (500 ms p95)

| # | Segment | Owner | p95 budget | Measurement point |
| --- | --- | --- | --- | --- |
| 3.1 | Capture finalization (= 2.1) | Shell | 25 ms | `capture.finalize` |
| 3.2 | Uplink tail (= 2.2) | Shell to Core | 10 ms | `transport.uplinkTail` |
| 3.3 | ASR admission (= 2.3) | Core | 10 ms | `gpu.admit(asr)` |
| 3.4 | ASR final decode (= 2.4) | ASR runner | 130 ms | `asr.finalize` |
| 3.5 | Normalization and glossary (= 2.5) | Core | 15 ms | `text.normalize` |
| 3.6 | Cleanup admission: lease handover, prompt assembly, tokenization | Core | 15 ms | `gpu.admit(cleanup)` |
| 3.7 | Cleanup generation | Cleanup runner | 180 ms | `cleanup.generate`, runner `computeMs` plus pipe round trip |
| 3.8 | Cleanup validation: intent guard, edit classification, diff build | Core | 15 ms | `cleanup.validate` |
| 3.9 | `PromptDraft` control frame delivery | Core to Shell | 20 ms | `transport.downlink` |
| 3.10 | Draft card render, including diff view | Shell | 35 ms | `ui.render(draft)`, ends at frame present |
| | **Subtotal** | | **455 ms** | |
| 3.11 | Reserve | | 45 ms | |
| | **Total** | | **500 ms** | |

Reserve policy: the 45 ms reserve absorbs GC pauses, scheduler jitter, and desktop compositor contention. It is not allocatable to a component. If a component consistently exceeds its budget, the fix is that component, or an ADR that reallocates the budget explicitly.

Serialization: 3.4 and 3.7 are both GPU jobs and, by ADR-004 section 4, never run concurrently. The budget already reflects sequential execution. There is no hidden parallelism assumption anywhere in this table.

Cleanup token budget: 180 ms at a target of at least 90 generated tokens per second on the selected model means the cleanup output must be bounded. Core caps cleanup generation at `min(1.3 x inputTokens + 24, 512)` tokens and stops on the model's end token. Exceeding the cap is a deadline violation, which cancels the job and falls back to the raw transcript rather than showing a truncated draft (ADR-004 section 5).

## 5. G4 — TTS first audible chunk (200 ms p95) and RTF

| # | Segment | Owner | p95 budget | Measurement point |
| --- | --- | --- | --- | --- |
| 4.1 | Summary text construction and coalescing decision | Core | 10 ms | `voice.compose` |
| 4.2 | Pre-render cache lookup | Core | 2 ms | `voice.cacheLookup` |
| 4.3 | GPU lease admission at `P1Speech` | Core | 20 ms | `gpu.admit(tts)` |
| 4.4 | First audio chunk generated | TTS runner | 110 ms | `tts.firstChunk` |
| 4.5 | Chunk delivery to the target device | Core to device | 15 ms | `transport.voiceChunk` |
| 4.6 | Device buffer fill and playback start | Device | 30 ms | `audio.playbackStart`, ends at first sample rendered |
| | **Subtotal** | | **187 ms** | |
| 4.7 | Reserve | | 13 ms | |
| | **Total** | | **200 ms** | |

A pre-rendered phrase (4.2 hit) skips 4.3 and 4.4 entirely and lands near 50 ms. The pre-render set covers the fixed summary openings, so common transitions benefit.

Real-time factor is measured as `runnerComputeMs / synthesizedAudioMs` over the whole utterance, excluding admission and transport, and must be at most 0.5 at p95. RTF is reported per utterance length bucket so a model that is fast on short phrases and slow on long ones is visible.

Preemption interacts with G4: a `P0Interactive` job cancels an in-flight TTS job within 100 ms (ADR-004 section 4). The G4 measurement excludes preempted utterances and reports the preemption count separately, because a user who starts speaking has deliberately interrupted the voice.

## 6. Phone path derived targets (measured, not gated)

The phone budget replaces the loopback segments with network segments and the WPF render with a Compose render.

| Segment | Desktop | Phone LAN | Phone Tailscale |
| --- | --- | --- | --- |
| Capture finalization | 25 | 25 | 25 |
| Uplink tail | 10 | 25 | 45 |
| ASR admission | 10 | 10 | 10 |
| ASR final decode | 130 | 130 | 130 |
| Normalization | 15 | 15 | 15 |
| Cleanup admission | 15 | 15 | 15 |
| Cleanup generation | 180 | 180 | 180 |
| Cleanup validation | 15 | 15 | 15 |
| Downlink | 20 | 25 | 45 |
| Render | 35 | 30 | 30 |
| Subtotal | 455 | 470 | 510 |
| Reserve | 45 | 30 | 50 |
| **Target** | **500** | **500** | **560** |

The Tailscale target is 560 ms and is reported alongside the measured link RTT so a slow result can be attributed to the network rather than to the product. If measured Tailscale RTT exceeds 45 ms one-way, the report states that the target is network-bound and the run is excluded from product regression tracking.

## 7. Instrumentation

- Every segment above is an `System.Diagnostics.Activity` span with the exact name in the tables, emitted to a local ETW listener. Names are stable identifiers and are asserted by a unit test so a rename cannot silently break the harness.
- Timestamps use `Stopwatch.GetTimestamp()` (QPC) on Windows and `System.nanoTime()` on Android. Wall-clock time is never used for measurement.
- Cross-device spans are stitched using a clock offset estimated from 20 `Ping`/`Pong` round trips at connection time, taking the minimum-RTT sample. The residual offset error is reported with every phone measurement and any run with an estimated offset error above 3 ms is discarded.
- The harness records, per run: every segment, GPU queue depth at admission, VRAM high-water mark, preemption count, runner `computeMs`, and whether the run was warm.
- The desktop widget exposes the last run's segment breakdown in tray diagnostics so a user can report a slow path with data.

## 8. Budget violation policy

| Situation | Behavior |
| --- | --- |
| A single segment exceeds its budget on one run | Recorded; no user-visible change |
| ASR job exceeds its 200 ms deadline | Job cancelled, `ASR_UNAVAILABLE` for this utterance, error state shown; nothing is guessed |
| Cleanup job exceeds its 260 ms deadline | Job cancelled, draft is the raw transcript with `cleanupApplied: false`; confirmation gate unchanged |
| TTS first chunk exceeds 200 ms | Chunk still played; the run is counted against G4 |
| p95 regression above the gate in CI-style benchmark runs | The task fails its completion gate; see `docs/performance/BENCHMARK_PLAN.md` section 8 |

No degradation path shortens or skips the confirmation gate. Latency is optimized only after correctness, confirmation, security, and privacy gates pass (`AGENTS.md`).

## 9. Open allocations resolved by benchmarking

The 130 ms ASR and 180 ms cleanup allocations are the two largest and are the ones the model selection must satisfy. `docs/performance/BENCHMARK_PLAN.md` treats them as hard admission criteria: a candidate model that cannot meet its allocation at p95 on the target hardware is eliminated regardless of accuracy, because no accuracy gain can buy back a missed gate. If **no** candidate in a category meets its allocation, that is an architecture escalation to Claude Opus 5 and a budget re-allocation ADR, not a silent gate relaxation.
