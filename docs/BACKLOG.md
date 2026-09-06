# Optimus Voice OS — Agile Backlog: Natural-Language Desktop Operator

- **Status**: Active
- **Target**: Windows 11 PC (RTX 4070 Laptop GPU) + Google Pixel 9a
- **Product Specification**: `PROJECT_PLAN.md`
- **Agent Rules**: `AGENTS.md`

This backlog supersedes the obsolete voice-command, mandatory draft review, destination locking, and enumerated intent vocabulary backlog. Each slice must end with something runnable and verified.

---

## Slice 1 — Baseline Audit: Recording, Background, and Interruption

- **Owner**: Gemini 3.8 Flash
- **Status**: **Completed**
- **Artifact**: `docs/BASELINE_AUDIT.md`

### Deliverable
Audit and document current recording, background audio, and interruption behavior across Windows and Android platforms with pass/fail evidence.

### Done Criteria & Evidence
1. **Windows Echo Cancellation Bridge**: Evaluated `EchoCanceller.cs` and native bridge. Confirmed missing `optimus_webrtc_aec.dll` causes fallback to raw audio passthrough, meaning AEC does not function on laptop speakers (**FAIL**).
2. **Android Recording Gesture Bug**: Reproduced the bug in `VoiceOrb.kt` where keying `pointerInput` on `isCapturing` cancels the press coroutine and triggers immediate capture abortion. Added automated regression test suite `VoiceOrbGestureRegressionTest.kt` (**FAIL / Reproduced & Regression Test Added**).
3. **Barge-in / Interruption (300 ms Target)**: Verified Android path stops hardware `AudioTrack` within ~40–60 ms of speech detection (**PASS**). Verified Windows WinMM reset stops in ~5 ms on hotkey/headphones, but fails on open laptop speakers due to lack of AEC (**CONDITIONAL**).
4. **Pixel Background Audio Ownership**: Evaluated `VoiceConversationService.kt`. Confirmed the foreground service is a cosmetic notification shell started/stopped by UI `LaunchedEffect`, while `TalkController` owns the mic (**FAIL**).
5. **Phone Disconnect / Reconnect**: Verified clean audio buffer cancellation on disconnect. Identified UX gap on reconnect where previous draft is not restored and voice device ownership transfer is missing (**PARTIAL PASS**).
6. **Documentation & Planning**: Documented all findings in `docs/BASELINE_AUDIT.md`. Updated `PROJECT_PLAN.md`, `AGENTS.md`, and `docs/BACKLOG.md` to establish the new natural-language operator direction.

---

## Slice 2 — Android Recording: Gesture Repair & Service Lifecycle

- **Owner**: Gemini 3.8 Flash
- **Dependencies**: Slice 1

### Deliverable
Replace the flawed gesture handling in `VoiceOrb.kt` and transition microphone and connection ownership into `VoiceConversationService`.

### Scope
- Decouple `pointerInput` from dynamic recording state (stable key).
- Implement required gesture semantics:
  - Pointer down starts temporary utterance capture.
  - Release ≤ 300 ms initiates/retains continuous conversation mode.
  - Release > 300 ms completes and submits the held utterance.
  - Drag-off / cancellation aborts the current gesture without terminating the session.
- Move `MicCapture` and `PhoneClient` management into `VoiceConversationService` so capture and audio streaming reliably survive phone screen lock and application switching.
- Add notification actions for Pause and End conversation.

### Done Condition
Ten short taps, ten varied holds, rapid successive interactions, and screen locking during capture pass on the physical Pixel 9a without self-cancellation.

---

## Slice 3 — Model Evaluation Setup & Benchmark

- **Owner**: Claude Opus 5 / Codex
- **Dependencies**: Slice 1

### Deliverable
Create the model evaluation framework, test harness, and 30 fixed workflow scenarios for selecting the resident GPU model on the RTX 4070 Laptop GPU (8 GB VRAM).

### Scope
- Candidates:
  1. Existing Gemma 4 E2B Q4 (baseline).
  2. Qwen3.5-4B Q4_K_M (with vision projector).
  3. Holo3.1-4B Q4_K_M (computer-use specialist).
- 30 scenarios: 6 per app (Codex, Claude, Antigravity, AO) + 6 cross-app/context scenarios.
- Metric collection: end-of-speech to first action latency, task completion rate, peak VRAM.
- Evaluation harness runnable via `dotnet test`.

### Done Condition
Evaluation report identifying which candidate passes the selection gates (≥ 90% completion, zero wrong-target sends, GPU resident within 8 GB VRAM).

---

## Slice 4 — Runtime Capability: Streaming, Cancellation & Tool Execution

- **Owner**: Claude Opus 5
- **Dependencies**: Slice 3

### Deliverable
Upgrade the local `llama.cpp` process integration to support streaming output, instant token cancellation, image input, and structured tool calling.

### Scope
- Verified CUDA backend offload for the selected model candidate.
- Native tool-call parsing (JSON schema) with execution only after full parse completion.
- Image injection pipeline for window/region screenshots.
- Cancellation handling that aborts generation within ≤ 100 ms when the user interrupts.

### Done Condition
Automated tests prove structured tool emission, cancellation of active prompt processing, and screenshot input ingestion without VRAM exhaustion.

---

## Slice 5 — Conversational Core: Natural Interaction & Ambiguity Resolution

- **Owner**: Gemini 3.8 Flash
- **Dependencies**: Slice 4

### Deliverable
Implement conversational turn coordination without fixed command syntax, destination locking, or universal draft confirmation.

### Scope
- Single conversational entry point accepting natural language.
- Contextual reference resolution using recent conversation and foreground window.
- Implicit authorization: ordinary commands execute directly without asking for confirmation.
- Contextual clarification: ask only when ambiguous, parameters are missing, or action is destructive.
- Spoken "stop" immediately aborts desktop actions and pending speech.

### Done Condition
Conversation suite passes with paraphrased requests, pronoun resolution, and contextual clarifications instead of generic confirmation cards.

---

## Slice 6 — Desktop Tools: Observe, Act, Verify Loop

- **Owner**: Claude Opus 5
- **Dependencies**: Slice 5

### Deliverable
Implement the desktop automation toolset: window management, UI Automation inspection, coordinate clicks, text input, and send verification.

### Scope
- Tools: `ListWindows`, `FocusWindow`, `InspectAccessibility`, `CaptureRegion`, `InvokeElement`, `ClickCoordinates`, `EnterText`, `PressShortcut`, `ReadVisibleContent`.
- Prefer accessible element invocation over raw coordinates.
- Verify outcome before declaring success (clicking send is not proof of acceptance).
- Gracefully pause execution when manual user input is detected.

### Done Condition
Automated and live tests verify reliable window focusing, control invocation, text entry, and outcome observation across test windows.

---

## Slice 7 — Agent Orchestrator (AO) Natural Voice Workflow

- **Owner**: Gemini 3.8 Flash
- **Dependencies**: Slice 6

### Deliverable
Connect the natural-language conversational operator to the live AO daemon API.

### Scope
- Query project status, active workers, and conversation history via AO REST API.
- Submit instructions to specified AO sessions.
- Voice inspection and approval of pending decisions.
- Background observation of AO events with concise local summarization.

### Done Condition
User can ask "What needs me in AO?", inspect pending approvals, and instruct workers entirely by voice from PC and Pixel.

---

## Slice 8 — Provider GUI Workflows: Codex, Claude & Antigravity

- **Owner**: Claude Opus 5 / Gemini
- **Dependencies**: Slice 6

### Deliverable
Robust application adapters for Codex, Claude Desktop, and Antigravity.

### Scope
- Accessible tree characterization for each app: selected conversation, composer, message history, agent responses.
- Application search/history navigation to locate discussions.
- Context-aware text insertion and send verification.
- Safe boundary handling: refuse to guess when conversations are ambiguous or unreadable.

### Done Condition
Each application passes its dedicated live task suite: find conversation, send request, observe public response, report summary.

---

## Slice 9 — Personal Memory & Response Monitoring

- **Owner**: Gemini 3.8 Flash
- **Dependencies**: Slice 8

### Deliverable
Local SQLite memory store for user preferences, application aliases, and conversation history; passive response monitor.

### Scope
- Store app/project aliases and behavioral preferences.
- Retain rolling 30-day interaction history and up to 100 recent task summaries.
- Revalidate remembered conversation references before acting.
- Passive response monitoring that does not steal window focus.

### Done Condition
Memories are correctly recalled during turns; user can inspect and forget memories; response summaries are concise (1–3 sentences).

---

## Slice 10 — Companion Overhaul: Fluid-Ink Visuals & System Tray

- **Owner**: Gemini 3.8 Flash
- **Dependencies**: Slice 5, Slice 9

### Deliverable
Complete visual and lifecycle overhaul for Windows WPF companion and Pixel Compose app.

### Scope
- Shared fluid-ink visual design (Periwinkle `#8C9EFF` / Cyan `#67D9E8`) via AGSL on Android and HLSL Shader Effect on Windows.
- Windows tray icon: Show/Hide, Start/Pause, Mute/Unmute, Stop, Quit.
- Proper window clamping, DPI scaling, and non-activating window styles.
- Pixel 3-tab layout polish (Talk, Projects, Updates) with unified tokens.

### Done Condition
Both surfaces reflect synchronized state transitions (Idle, Listening, Interpreting, Acting, Speaking) with smooth 180–300 ms animations.

---

## Slice 11 — End-to-End Polish & Latency Tuning

- **Owner**: Claude Opus 5 / Gemini
- **Dependencies**: Slices 2–10

### Deliverable
Physical-device dogfood pass on host laptop and Pixel 9a, latency optimization, and device handoff.

### Scope
- Single-owner voice arbitration between PC and Pixel.
- Seamless reconnection restoring visible state on Pixel without replaying speech.
- Latency profiling against targets:
  - Speech to first simple action ≤ 1.5 s median.
  - Speech to first spoken response ≤ 2.0 s median.
  - Interruption to silence ≤ 300 ms.
- Tune voice endpointing (pre-roll and trailing silence) to the user's natural speaking rhythm.

### Done Condition
Full physical dogfood checklist passed on real hardware under normal development workload.

---

## Slice 12 — Cleanup & Final Handoff

- **Owner**: Gemini 3.8 Flash
- **Dependencies**: Slice 11

### Deliverable
Remove obsolete test harnesses and deprecated scaffold; deliver a standalone, runnable personal build.

### Scope
- Eliminate dead code, unused flags, and obsolete destination-lock artifacts.
- Single documented launch command.
- Personal user guide covering conversational operation, takeover, tray controls, and memory.

### Done Condition
User can launch and operate Optimus during daily development on Windows and Pixel without developer tooling.
