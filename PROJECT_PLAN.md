# Optimus Voice OS — Canonical Project Plan

## Vision

Build the fastest local-first voice layer between a human and coding agents. The experience is deliberate: talk, inspect a cleaned draft, select an explicit destination, confirm, and receive concise event-driven voice feedback.

## Supported environment

- Windows 11 desktop with Intel Core i9-14900HX, 32 GB RAM, and RTX 4070 Laptop GPU with 8 GB VRAM.
- Pixel 9a running Android API 35.
- Claude Code and Codex as initial coding-agent destinations.
- LAN and direct Tailscale connectivity without a hosted relay.
- English recognition for v1.

## User experience

### Desktop

- Global hold-to-talk hotkey.
- Tiny frameless floating widget showing transcript snippet, destination card, state, and contextual controls.
- States: idle, listening, transcribing, cleaning, awaiting confirmation, sending, busy, approval, completed, and error.
- Tray settings for hotkeys, glossary, destinations, models, privacy, devices, voice, benchmarks, and diagnostics.

### Phone

- Native Kotlin/Jetpack Compose application targeting Android API 35.
- Tap-to-start and tap-to-stop recording.
- Talk screen mirrors transcript, cleaned draft, destination, and confirmation.
- Sessions screen exposes active agent sessions, approvals, Queue, Steer, Cancel, and New Session.
- Background approval notifications require biometric confirmation for consequential actions.
- Voice plays on the device that originated the prompt.

### Agent feedback

- Speak meaningful transitions: planning begins, tests pass or fail, approval is needed, the agent asks a question, work fails, and final completion summary.
- Repeated planning and ordinary file edits remain silent and use visual state only.
- Coalesce repeated events and rate-limit spoken summaries.

## Technical architecture

- .NET 8 WPF desktop application and tray process.
- ASP.NET Core/Kestrel local service.
- Isolated local inference runners with explicit health, warmup, cancellation, and crash-recovery contracts.
- Kotlin/Jetpack Compose Android application.
- One pinned-TLS WebSocket connection using structured control messages and binary PCM audio frames.
- QR pairing with PC identity, certificate fingerprint, endpoint, and one-time secret.
- Credentials stored through Windows DPAPI and Android Keystore.
- Modular Claude Code and Codex sender adapters behind one provider-neutral contract.

## Local inference

### Speech recognition

Benchmark:

1. NVIDIA Parakeet Unified English 0.6B.
2. Qwen3-ASR 0.6B.
3. Faster-Whisper Distil-Large-v3 as compatibility fallback.

The winner is the lowest-latency candidate that passes every gate. Within a 10% latency tie, prefer critical-token accuracy; if still tied, select Parakeet Unified for the simpler English streaming path.

### Prompt cleanup

Benchmark Gemma 4 E2B Q4, Gemma 3 1B, and Qwen3.5 0.8B. Ship the fastest model producing zero intent changes across the golden suite. Gemma 4 wins an otherwise equal result.

Cleanup may remove fillers and abandoned repetition, repair punctuation/casing, format identifiers, and apply acoustically grounded glossary corrections. It may not invent requirements, choose destinations, or reinterpret the request.

### Voice

- Create an original deep, calm, authoritative identity with Qwen3-TTS 1.7B VoiceDesign during setup.
- Prohibit imitation of identifiable people, actors, and characters.
- Benchmark Qwen3-TTS 0.6B against Pocket TTS using the selected synthetic reference.
- Prefer the fastest passing runtime; within a 30 ms tie, choose Pocket TTS to preserve GPU capacity.
- Pre-render common phrases with the selected voice.
- Keep Kokoro only as an emergency fallback.

## Agent behavior

- Discover destinations explicitly and display the active choice.
- Preserve provider sessions until the user chooses New Session.
- Require explicit Queue or Steer when an agent is busy.
- Show and speak agent questions and approval requests.
- Reject stale, modified, expired, replayed, or destination-mismatched confirmation actions.

The provider-neutral adapter exposes:

- `DiscoverDestinations`
- `CreateSession`
- `ResumeSession`
- `SendConfirmedPrompt`
- `QueuePrompt`
- `SteerActiveSession`
- `RespondToApproval`
- `Cancel`
- `SubscribeToEvents`

The desktop/mobile protocol includes:

- `DeviceSession`
- `Destination`
- `AudioFrame`
- `PromptDraft`
- `ConfirmationRequest`
- `SendAction`
- `AgentEvent`
- `ApprovalRequest`
- `VoiceSummary`

## Privacy and storage

- Do not retain routine audio.
- Store local text event history with configurable retention and clear-history control.
- Allow an explicit opt-in calibration set of 30–50 local recordings, independently deletable.
- Bind the PC service only to configured trusted interfaces and reject unpaired clients.
- Do not require a hosted account, relay, or telemetry service.

## Performance and acceptance gates

- Hotkey press to active capture below 50 ms p95.
- Hotkey release to final raw transcript below 250 ms p95 for utterances up to 20 seconds.
- Hotkey release to cleaned visible draft below 500 ms p95.
- Critical filename, symbol, number, flag, and coding-term accuracy at least 97%.
- Normalized WER at most 8% on the project coding corpus.
- Zero intent-changing transcription or cleanup failures in the golden suite.
- TTS first audible chunk below 200 ms p95 and real-time factor no greater than 0.5.
- No release without end-to-end success on the target Windows computer and Pixel 9a over LAN and Tailscale.

## Delivery milestones

1. Architecture, protocols, threat model, latency budget, and repository scaffold.
2. Desktop widget, hotkey, audio capture, state machine, and local diagnostics.
3. STT, cleanup, glossary, model management, and benchmark selection.
4. Original voice creation, runtime TTS, and event-summary engine.
5. Claude Code and Codex adapters with sessions, Queue, Steer, questions, and approvals.
6. Secure PC service, pairing, and complete Pixel 9a application.
7. Installer, model distribution, failure recovery, performance hardening, and release audit.
