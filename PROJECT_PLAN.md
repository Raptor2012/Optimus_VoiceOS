# Optimus Voice OS — Natural-Language Desktop Operator Implementation Plan

## 1. Goal

Optimus is your **fully local, natural-language desktop operator** for Windows, accessible from the PC companion and your Pixel 9a.

You describe what you want in natural language. Optimus understands the request, inspects the relevant application, performs the necessary steps, and reports the result.

It must feel like a conversation with an assistant—not voice access to a set of buttons or a rigid syntax tree.

Supported target applications:
- **Codex**
- **Agent Orchestrator (AO)**
- **Claude Desktop**
- **Antigravity**

Agent Orchestrator continues managing coding projects, workers, and subscription-based coding work. Optimus operates your desktop and communicates your requests across applications. It does not introduce another coding scheduler or use cloud frontier models to decide desktop actions.

---

## 2. Target Hardware & Environment

- **Host PC**: Windows 11, Intel Core i9-14900HX, 32 GB RAM, NVIDIA GeForce RTX 4070 Laptop GPU with 8 GB VRAM (~1.9 GB occupied baseline).
- **Mobile Companion**: Google Pixel 9a running Android 15.
- **Topology**: One user, one PC, one phone over private LAN or Tailscale. Never expose the PC service through public port forwarding.
- **Local Runtime**: Local execution only; no cloud dependencies for speech, vision, or desktop orchestration.

---

## 3. Non-Negotiable User Experience

The normal interaction must **not** require:
- Prescribed command phrases or rigid syntax.
- “Tell” or “send” routing prefixes.
- Switching between "Control" and "Dictate" modes.
- Manually selecting or locking a destination before every request.
- Reviewing every generated prompt before it can be sent.
- Saying “confirm” after an already explicit instruction.
- Understanding which API, accessibility mechanism, or model performed an action.

An optional text composer, history panel, and manual app selection remain available as secondary recovery controls, not as the required workflow.

### Natural Interaction Semantics

| You say | Optimus does |
|---|---|
| “Go to Claude and find our discussion about the microphone bug.” | Opens or focuses Claude, finds the conversation, and asks if multiple match. |
| “Ask it to investigate why tapping stops recording.” | Resolves “it,” composes the request from context, sends it, and watches for the response. |
| “Mention that holding sometimes fails too.” | Appends to an unsent request, or prepares a follow-up if already sent. |
| “Actually, use AO.” | Replaces pending target with AO; if already sent elsewhere, explains plainly instead of pretending it was redirected. |
| “What did it say?” | Summarizes the latest relevant response in 1–3 concise sentences. |
| “Read the whole answer.” | Reads the underlying public response verbatim. |
| “Show me what needs me in AO.” | Retrieves and presents pending decisions/approvals. |
| “Don’t do anything yet—just explain.” | Answers conversationally without issuing desktop actions. |
| “Stop.” | Immediately stops issuing desktop actions and cancels active/queued speech. |

### Authority & Follow-Through
- **Implicit Authorization**: A clear instruction authorizes ordinary navigation, composition, typing, and intended sends.
- **Clarifications**: Ask only when:
  1. The target or intended result has multiple plausible interpretations.
  2. An essential requirement is missing.
  3. A requested operation is destructive.
  4. Continuing would unilaterally expand the user’s objective.
  5. The target application cannot be operated reliably with current observations.
- **After Agent Replies**:
  1. Capture its public response.
  2. Summarize the relevant answer locally.
  3. Present any question or decision it raised.
  4. Wait for the next instruction.
  *(Do not automatically answer coding agents or start another round without explicit follow-through instructions).*

### Context & Targeting (No Destination Locking)
- Remove the permanent voice-destination lock.
- Bind **each executable request** to an exact target once resolved; that binding persists through execution.
- Browsing another application must not redirect a pending send.
- Separate three distinct concepts:
  - Where the user is currently browsing.
  - What the current request concerns.
  - Which conversation is being monitored for a reply.

---

## 4. Surfaces & Audio Architecture

### 4.1 Windows Companion
A floating, borderless WPF companion with two presentations:
- **Collapsed**: ~280 × 64 logical pixels. Fluid-ink animation, one-line context, stop control when active, expand affordance. Does not steal focus from underlying applications.
- **Expanded**: ~420 logical pixels wide. Recent user utterance, assistant response, pending questions, scrollable conversation history, optional text input, and settings.
- **System Tray**: Real Windows tray icon with Show/Hide, Start conversation / Pause listening, Mute/Unmute, Stop desktop task, and Quit. Minimize and Close hide to tray without killing active tasks.

### 4.2 Pixel 9a Companion App
Three primary navigation tabs:
- **Talk**: Conversation, fluid-ink animation, current context, optional text input.
- **Projects**: Live AO projects, sessions, tasks, and worker activity.
- **Updates**: Concise summaries of completed work and outstanding decisions.
- **Foreground Service**: The Android foreground service (`VoiceConversationService`) genuinely owns the `MicCapture`, `AudioRecord`, and TCP socket lifecycles, enabling continuous background conversation through screen lock and app switching.

### 4.3 Audio Controls & Interruption
- **Separate States**: Explicitly separate microphone capture, utterance detection, task execution, and speech playback.
- **Barge-In**: User speech stops audible assistant playback promptly (≤ 300 ms target).
- **Echo Cancellation**: Rendered playback is fed into AEC reference; speech detection runs on echo-reduced microphone samples. Pre-roll buffer (300 ms) preserves the initial syllable.
- **Device Ownership**: Only one device owns active voice input/output at a time. Explicitly starting conversation on one device pauses the other.

### 4.4 Fluid-Ink Visual Design
- Flat organic shape with Periwinkle (`#8C9EFF`) and Cyan (`#67D9E8`) gradients on a dark background (`#070A0F`).
- Dynamic states: Idle, Listening, Interpreting, Desktop acting, Speaking, Clarification, and Paused.
- Implemented via AGSL on Android and HLSL WPF Shader Effect on Windows.

---

## 5. Local Model Stack & Hardware Strategy

### 5.1 Verified Target Hardware
- Host: Intel i9-14900HX, 32 GB RAM, NVIDIA RTX 4070 Laptop GPU (8 GB VRAM).
- Reserved VRAM headroom: 1–1.5 GB under normal load for OS and desktop applications.

### 5.2 Resident Model Selection
Evaluate three local candidates under identical GUI workflow scenarios:
1. **Gemma 4 E2B Q4** (baseline; currently integrated).
2. **Qwen3.5-4B Q4_K_M** (general conversation, tool calling, vision support).
3. **Holo3.1-4B Q4_K_M** (computer-use / GUI specialist).

Selection gates:
- ≥ 90% task completion across a 30-scenario evaluation suite.
- Zero wrong-target sends.
- Reliable stop and manual-takeover handling.
- Zero frontier cloud inference.
- GPU resident within 8 GB VRAM budget.

### 5.3 Speech Models
- **STT**: Parakeet 0.6B English model (CUDA-accelerated on host PC).
- **TTS**: Piper neural TTS with deep robotic commander coloration; streamed incremental synthesis.

---

## 6. Execution & Desktop Tools

Optimus uses structured tool execution against application targets:
- **List windows** / **Focus window**.
- **Inspect accessibility controls** (Windows UI Automation tree snapshots).
- **Capture window/region** (focused screenshot for vision/OCR analysis).
- **Invoke accessible element** / **Click coordinates** (prefer accessible elements; coordinates only against fresh screenshots).
- **Enter text** / **Press shortcut**.
- **Read visible content** / **Observe action result**.

Execution state machine:
- `Idle` -> `Resolving` -> `Acting` -> `Waiting for recipient` -> `Completed` / `Needs clarification` / `Stopped` / `Unresolved`.

---

## 7. Backlog Roadmap (12 Slices)

| Slice | Title | Deliverable | Done Condition |
|---|---|---|---|
| **Slice 1** | Baseline Audit | Reproduce recording, background, and interruption behavior | Written evidence in `docs/BASELINE_AUDIT.md` identifying actual failures and working paths |
| **Slice 2** | Android Recording | Stable tap/hold gesture and true background service lifecycle | Physical gestures do not self-cancel; recording survives screen lock |
| **Slice 3** | Model Evaluation | 30 scenarios evaluated on Gemma 4 E2B, Qwen3.5-4B, and Holo3.1-4B | Candidate passes 90% gate and VRAM constraints on RTX 4070 |
| **Slice 4** | Runtime Capability | Streaming, cancellation, image/tool support in llama.cpp runtime | Structured tool execution and immediate cancellation verified |
| **Slice 5** | Conversational Core | Natural requests, context resolution, clarifications, corrections | Zero rigid command vocabulary; no universal draft confirmation |
| **Slice 6** | Desktop Tools | Observe, act, verify loop with manual takeover | Reliable window focus, accessibility clicks, text entry, and scroll |
| **Slice 7** | AO Workflow | Natural project/session operations via existing AO API | Query status, send tasks, resolve decisions by voice |
| **Slice 8** | Provider GUI Workflows | Codex, Claude, and Antigravity application adapters | Each passes live GUI task suite with verify-before-send |
| **Slice 9** | Memory & Monitoring | Local SQLite memory store for preferences, aliases, and history | Accurate context recall; passive response monitoring |
| **Slice 10** | Companion Overhaul | Fluid-ink shader, tray icon, window clamping, focus polish | Both PC and Pixel reflect unified visual tokens and states |
| **Slice 11** | End-to-End Polish | Device handoff, background operation, latency tuning | Complete acceptance matrix passed on physical devices |
| **Slice 12** | Cleanup & Handoff | Remove obsolete flows; deliver runnable personal build | Fully functional Windows + Pixel setup without developer tools |
