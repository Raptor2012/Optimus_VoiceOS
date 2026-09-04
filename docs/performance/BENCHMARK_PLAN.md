# Benchmark and model-selection plan

- Status: Accepted
- Owner: Claude Opus 5
- Governing decisions: ADR-004, `docs/performance/LATENCY_BUDGET.md`
- Implemented by: T011 (harness and corpus), executed by T012 (ASR and cleanup selection) and T014 (TTS selection)

`PROJECT_PLAN.md` names the candidates and the tie-break rules but forbids selecting a winner without target-hardware results. This plan defines how those results are produced so the selection is mechanical rather than argued.

## 1. Selection rules restated

| Category | Candidates | Rule |
| --- | --- | --- |
| ASR | Parakeet Unified English 0.6B, Qwen3-ASR 0.6B, Faster-Whisper Distil-Large-v3 | Lowest latency among candidates that pass every gate. Within a 10 percent latency tie, prefer critical-token accuracy. Still tied: Parakeet Unified. |
| Cleanup | Gemma 4 E2B Q4, Gemma 3 1B, Qwen3.5 0.8B | Fastest model with zero intent changes across the golden suite. Otherwise equal: Gemma 4. |
| TTS | Qwen3-TTS 0.6B, Pocket TTS | Fastest passing runtime. Within a 30 ms tie on first-chunk p95: Pocket TTS. |

Distil-Large-v3 is explicitly a compatibility fallback: it is benchmarked and shipped as a selectable option even if it does not win, so a machine where the winner fails to load has a working path.

"Tie" is evaluated on the p95 of the primary latency metric for the category: ASR `asr.finalize`, cleanup `cleanup.generate`, TTS `tts.firstChunk`.

## 2. Corpus construction

Four corpora, all English, all recorded or written for this project. Nothing is copied from a third-party dataset.

### 2.1 Coding corpus (ASR accuracy and WER), 200 utterances

| Bucket | Count | Description |
| --- | --- | --- |
| Short commands | 40 | 2 to 5 s, for example a single refactor instruction |
| Medium instructions | 70 | 5 to 12 s, typical dictation |
| Long instructions | 40 | 12 to 20 s, multi-clause |
| Identifier-dense | 25 | File paths, class and method names, package names |
| Numeric and flag-dense | 15 | Version numbers, ports, CLI flags, percentages |
| Adverse | 10 | Background keyboard noise, fan noise, a second distant voice, 5 each at two noise levels |

Recording conditions: the target Windows machine's built-in array microphone and one USB microphone, both at 16 kHz mono, in a normal room. Every utterance is recorded twice, once per microphone. Speaker set: the primary user plus at least two additional speakers, to avoid tuning to one voice.

Each utterance carries a reference transcript and a **critical-token list**: the filenames, symbols, numbers, flags, and coding terms whose exact recognition matters. Critical-token accuracy is measured only against that list.

Storage: corpora live outside the repository under `%LOCALAPPDATA%\Optimus\benchmarks\corpus\` with a checked-in `corpus-manifest.json` that records per-utterance id, duration, bucket, speaker id, microphone, SHA-256, reference transcript hash, and critical tokens. **Audio never enters Git** (`.gitignore` already excludes `*.wav`, `benchmarks/results/`, and `data/`). The manifest is checked in so a run is reproducible and reviewable; reference transcripts are checked in as text under `benchmarks/references/`.

Consent: every speaker is recorded with explicit consent for this purpose. The corpus is local-only and is never uploaded.

### 2.2 Golden intent suite (cleanup correctness), 150 items

Text in, expected-behavior out. Each item is a raw transcript plus:

- `mustPreserve`: substrings that must survive verbatim (identifiers, numbers, flags, quoted strings, negations).
- `mayRemove`: filler tokens and abandoned repetitions that a correct cleanup may drop.
- `forbiddenAdditions`: any content word not present in the input.
- `expectedClass`: `NoChange`, `FillerOnly`, `PunctuationOnly`, `IdentifierFormatting`, `GlossaryCorrection`, or `Mixed`.

Composition: 40 clean inputs that must come back unchanged, 30 filler-heavy, 20 self-corrections ("no wait, make it"), 20 identifier-heavy, 20 negation and conditional heavy, 10 ambiguous inputs that must be left alone rather than resolved, 10 adversarial inputs containing instruction-like text that cleanup must treat as content.

### 2.3 Voice evaluation set, 60 lines

Event summaries drawn from the real summary templates plus 20 free-form sentences, covering short interjections, numbers, identifiers, and long sentences, used for TTS latency, RTF, and intelligibility.

### 2.4 End-to-end scenario set, 25 scenarios

Full runs from hotkey press to `SendResult` against a stub adapter, on desktop and on the Pixel 9a, over LAN and Tailscale, used for the composite gates and the release check.

## 3. Hardware and environment conditions

Every run records these and a run is invalid if a condition is violated:

| Condition | Requirement |
| --- | --- |
| Machine | The target Intel Core i9-14900HX / 32 GB / RTX 4070 Laptop 8 GB system |
| Power | AC connected, Windows power mode `Best performance`, battery above 50 percent |
| Thermal | GPU and CPU package temperatures below 60 C at run start; a 120 s idle settle between candidates |
| GPU state | No other CUDA process; `nvidia-smi` VRAM used below 600 MB before Core starts |
| Background | No browser, no IDE, no provider CLI running; Windows Update paused; Defender real-time scan not mid-scan |
| Display | Widget on the primary display at 100 percent scaling, 60 Hz or the panel's native rate, recorded |
| Driver and runtime | NVIDIA driver version, CUDA runtime, cuDNN, .NET SDK, and Python versions recorded per run |
| Network (phone runs) | Measured one-way RTT recorded per run; Wi-Fi band and signal strength recorded |

Runs that violate a condition are recorded as invalid with the reason and excluded from statistics rather than silently dropped.

## 4. Run protocol

For each candidate model in a category:

1. **Cold measurement.** Start Core with only that candidate configured. Record process start to runner `ready`, model load time, first-inference latency, and VRAM after load. One cold run per candidate per session, 5 sessions.
2. **Warmup.** Run 20 discard iterations across the utterance-length distribution.
3. **Warm measurement.** Run the full corpus in a fixed pseudo-random order seeded by `seed = SHA-256(candidateId || sessionIndex)` truncated to 32 bits, so ordering is reproducible and not identical across sessions.
4. **Repetitions.** 5 independent sessions per candidate, each preceded by a Core restart and a 120 s thermal settle. Total warm samples per candidate: 200 utterances x 2 microphones x 5 sessions = 2000 for ASR; 150 x 5 = 750 for cleanup; 60 x 5 = 300 for TTS.
5. **Interleaving.** Candidates are run in a rotating order across sessions (A,B,C / B,C,A / C,A,B / …) so thermal drift cannot systematically favor one candidate.
6. **Contention run.** One additional session per candidate with a representative background load (an IDE, a browser with 10 tabs, and an idle provider CLI) to record the degradation factor. Contention runs do not decide the winner but must not exceed 1.5x the clean p95, or the candidate is flagged.

## 5. Metrics and statistics

Per candidate, per bucket, and overall:

| Metric | Definition |
| --- | --- |
| Primary latency | Category primary span, p50 / p95 / p99 / max |
| Segment breakdown | Every span in `docs/performance/LATENCY_BUDGET.md` |
| Composite gates | G2 and G3 end-to-end p95 |
| WER | Normalized word error rate, section 6.1 |
| Critical-token accuracy | Exact-match rate over the critical-token list |
| Intent changes | Count of golden-suite failures, section 6.2 |
| RTF | `computeMs / audioMs`, p50 / p95, per length bucket |
| VRAM | Post-load and high-water bytes |
| Stability | Crashes, restarts, deadline violations, cancellation-timeout kills per 1000 runs |

Statistics:

- Percentiles are computed by the nearest-rank method on the pooled warm samples across sessions.
- Every reported p95 carries a bootstrap 95 percent confidence interval, 10 000 resamples, seeded and recorded.
- **Tie determination is by confidence interval, not by point estimate.** Two candidates are tied when their p95 confidence intervals overlap, or when the point estimates are within the rule's tolerance (10 percent for ASR, 30 ms for TTS). Only then is the documented tie-break applied.
- Between-session variance is reported. A candidate whose per-session p95 varies by more than 25 percent is flagged as unstable and cannot win without an explicit Opus decision recorded in the selection ADR.

## 6. Correctness gates

A candidate is eliminated by any gate failure regardless of speed.

### 6.1 ASR gates

| Gate | Threshold |
| --- | --- |
| Normalized WER on the coding corpus | at most 8 percent |
| Critical-token accuracy | at least 97 percent |
| Intent-changing transcription errors | exactly 0 |
| `asr.finalize` p95 | at most 130 ms (budget allocation) |
| G2 composite p95 | below 250 ms |

WER normalization, applied identically to reference and hypothesis before scoring, and specified once in `benchmarks/normalization.md` delivered by T011:

- Case-folded; punctuation removed except inside identifiers and paths.
- Numbers normalized to digits ("forty two" to "42").
- Common code spellings normalized ("dot net" to ".net", "dash dash" to "--").
- Filler tokens removed from both sides.
- Identifier segmentation preserved: `getUserName` is one token, not three.

An **intent-changing transcription error** is one where the normalized hypothesis, read by a competent developer, would produce a different action: a negation lost or added, a different identifier or path, a different number, a different flag, or a different verb of consequence (delete, push, deploy, drop). Scoring is automated by rule against the critical-token list and the negation list, and every automated failure plus a 10 percent random sample of passes is human-reviewed. Any disagreement between the rule and the reviewer is a defect in the rule and is fixed before the selection is finalized.

### 6.2 Cleanup gates

| Gate | Threshold |
| --- | --- |
| Intent changes across the golden suite | exactly 0 |
| `mustPreserve` violations | exactly 0 |
| `forbiddenAdditions` violations | exactly 0 |
| `NoChange` items returned unchanged | 100 percent |
| Ambiguous items left unresolved | 100 percent |
| Adversarial items treated as content, never as instructions | 100 percent |
| Destination or provider names introduced by cleanup | exactly 0 |
| `cleanup.generate` p95 | at most 180 ms |
| G3 composite p95 | below 500 ms |

Scoring is automated against the item annotations. Every failure and a 10 percent sample of passes are human-reviewed, as above.

### 6.3 TTS gates

| Gate | Threshold |
| --- | --- |
| `tts.firstChunk` p95 | at most 110 ms |
| G4 composite p95 | below 200 ms |
| RTF p95 | at most 0.5 |
| Intelligibility: identifier and number lines transcribed correctly by the selected ASR model | at least 98 percent |
| Audible artifacts (clipping, dropouts, truncation) in the 60-line set | 0 |
| VRAM after load | at most the 1.2 GB runner budget |

Intelligibility is measured mechanically by round-tripping synthesized audio through the selected ASR model. This is a proxy, and it is supplemented by one human listening pass over the 60 lines.

### 6.4 Voice identity gates (setup-time, T013)

The created voice must be deep, calm, and authoritative, and must not imitate an identifiable person, actor, or character. Verification is a documented review step: the design prompt and its parameters are recorded, three candidate voices are generated, and the accepted voice is checked by the reviewer against the prohibition. No similarity claim to any real person is recorded, sought, or tested for, because the design intent is originality. The selected reference sample is stored locally and is the fixed reference for the TTS runtime benchmark, so both TTS candidates are compared on the same voice.

## 7. Reproducibility

Every run writes `benchmarks/results/<utc-timestamp>-<category>-<candidate>/` (git-ignored) containing:

| File | Contents |
| --- | --- |
| `run.json` | Harness version, git commit of the harness, seed, condition readings, driver and runtime versions, model id and file SHA-256 values, corpus manifest hash |
| `samples.jsonl` | One record per utterance: id, bucket, all span timings, computeMs, tokens, VRAM, validity flag |
| `metrics.json` | All statistics from section 5 with confidence intervals |
| `gates.json` | Per-gate pass or fail with the measured value and the threshold |
| `environment.txt` | `nvidia-smi`, `dotnet --info`, OS build, power plan |

A run is reproducible when re-executing the harness at the same commit, with the same seed, corpus manifest hash, and model hashes, on the same machine, produces p95 values within the reported confidence interval. T011 delivers a `--verify` mode that re-runs a recorded configuration and reports whether it reproduces.

Results are summarized into `docs/performance/RESULTS-<category>.md` and the selection is recorded in a new ADR authored by Claude Opus 5 (`ADR-006` for ASR and cleanup, `ADR-007` for TTS). Gemini never selects a winner.

## 8. Regression tracking

Once a winner is selected:

- The harness gains a `--regression` mode that runs a 60-utterance subset and the full golden suite in about 10 minutes.
- Any task that touches capture, ASR, cleanup, TTS, the scheduler, or the protocol must run `--regression` and record the result in its evidence section.
- A p95 regression above 10 percent against the recorded baseline, or any new correctness-gate failure, is a P1 finding and blocks the task.
- The baseline is updated only by an Opus-authored ADR amendment, never by an implementation task.

## 9. Release benchmark

Before release (T029), the end-to-end scenario set runs on the target machine and the Pixel 9a over both LAN and Tailscale, and the report records G1 to G4, the phone derived targets, the network RTT, and the full gate table. A release requires every mandatory gate to pass on the target hardware, with the run artifacts attached to the release audit.
