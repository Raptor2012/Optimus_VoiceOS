# Optimus Voice OS — Personal-use implementation plan

## Goal

Build the fastest practical voice layer for one user, one Windows 11 PC, and one Pixel 9a.

The working flow is:

1. Hold push-to-talk on the PC or phone.
2. Speak.
3. Transcribe on the PC with fast local STT.
4. Clean the text with a small local LLM.
5. Show the exact cleaned draft and an explicitly selected destination.
6. Read the exact cleaned draft and destination aloud on the device that started the request.
7. Automatically listen for a spoken confirmation, redictation, or cancellation command.
8. Send only after an affirmative confirmation.
9. Observe the exact coding-agent window for visible progress and its visible final response.
10. Speak short event-driven progress updates and read the final response aloud on the originating device.

This is personal software, not a commercial product. The priority is a useful end-to-end build, not production-grade protocols, generalized infrastructure, exhaustive edge cases, or release certification.

## Target setup

- Windows 11 PC: Intel Core i9-14900HX, 32 GB RAM, RTX 4070 Laptop GPU with 8 GB VRAM.
- Pixel 9a running Android.
- One user, one PC, one phone.
- Claude, Antigravity, and Codex Windows applications as initial destinations.
- English speech first.
- Private LAN or Tailscale only. Never expose the PC service through public port forwarding.

## Core experience

### Windows widget

Use one tiny always-on-top widget with these states:

- `Idle`
- `Listening`
- `Processing`
- `Confirm`
- `ReadingDraft`
- `AwaitingApproval`
- `Redictating`
- `Sending`
- `Monitoring`
- `Speaking`
- `Sent`
- `Error`

It shows:

- transcript or cleaned draft;
- explicit destination card;
- spoken-review/approval state;
- edit, confirm, redictate, and cancel controls as fallbacks;
- one short status line.

The normal desktop path requires no mouse after the initial push-to-talk hold/release. Buttons
remain available as recovery controls, not as the primary confirmation mechanism.

### Pixel 9a app

Build a small native Kotlin/Jetpack Compose app that mirrors the useful parts of the widget:

- push-to-talk;
- transcript and cleaned draft;
- explicit destination selection;
- exact-draft voice playback;
- edit, spoken confirm, redictate, and cancel;
- current agent status;
- spoken visible final response.

The phone captures audio; the PC performs STT, cleanup, and agent sending. The phone is a remote control for the user's PC, not an independent inference server.

### Destination and confirmation

- The user manually configures each destination by selecting an open Windows application window and assigning a label.
- The selected destination is visible before every send.
- The app never guesses, substitutes, or silently changes a destination.
- A destination can be chosen by its explicitly configured voice alias, such as
  `To Codex Project Y`, or by a visible card. Spoken routing is accepted only when it resolves
  to exactly one configured destination; otherwise Optimus asks rather than guessing.
- A destination selection is cleared for every new utterance. Previous choices do not silently
  carry into the next prompt.
- After TTS finishes reading the exact draft and destination, Optimus plays a chime and listens
  automatically for an approval command. No second hotkey or mouse action is required.
- Initial affirmative vocabulary: `yes`, `yeah`, `yep`, `confirm`, `send`, `send it`,
  `go ahead`, and `do it`. Redictation vocabulary: `redictate`, `try again`, and `start over`.
  `cancel` cancels. Anything unclear causes a short reprompt and never sends.
- The microphone is disabled while TTS is playing and enabled only after the approval chime,
  preventing speaker output from confirming itself. The MVP is half-duplex; barge-in is later.
- `Redictate` discards the current draft, plays a chime, and immediately begins a replacement
  capture without requiring another hotkey.
- PC and phone confirmation use the same in-memory draft lifecycle. Confirm, cancel, sending,
  and completion on one surface must update the other so a second active send gate cannot remain.
- The PC sends the exact confirmed draft snapshot.
- No cryptographic receipt, MAC, attestation, replay ledger, or approval-signing system is needed for this personal-use MVP.

### Agent feedback

Each destination adapter gains a small observation side in addition to sending. It watches only
the exact bound application window and emits visible events such as planning, editing, running
tests, tests passed/failed, agent question, and completion. Start with Windows UI Automation and
use a separate observer per application where their accessibility trees differ.

Optimus may speak visible application status, visible reasoning/progress text, tool and skill
activity exposed by the UI, and the visible final response. It cannot access or narrate a model's
private hidden chain-of-thought. Provide two narration modes on both widget and phone:

- `Concise`: meaningful transitions, questions, failures, completion, and the final response.
- `Comprehensive`: all newly visible English reasoning/progress messages plus the final response.

Provide a separate `Narrate tools & skills` toggle. When enabled, announce visible tool/skill
names, intent, commands, and short result summaries. Do not dictate raw binary data or extremely
long logs character by character. Deduplicate repeated UI text so rerenders do not repeat speech.

The device that initiated the utterance owns the audio conversation. PC-originated requests play
TTS and collect approval on the PC. Pixel-originated requests receive synthesized audio from the
PC and return approval audio from the Pixel. Do not unexpectedly play the same response on both.

## Lean architecture

Prefer the smallest implementation that works on the target devices.

### PC

Use the existing .NET 8/WPF scaffold. Keep the MVP in one desktop process unless a measured runtime problem forces a boundary.

Required pieces:

- global hotkey;
- in-memory WASAPI capture;
- one local STT integration;
- one local cleanup integration;
- one local TTS integration;
- floating WPF widget;
- local destination configuration;
- one small Windows send/observe adapter per target application;
- the existing minimal direct TCP endpoint for the Pixel app;
- simple timing logs around the real path.

A sender adapter needs only:

- `IsAvailable`
- `ActivateConfiguredDestination`
- `InsertText`
- `Submit`

An observer needs only:

- bind to the exact configured window;
- emit coarse visible progress changes;
- detect an agent question or completion;
- return the visible final response.

Adapters may use Windows UI Automation, a configured window identity, and clipboard insertion where needed. If the configured window is missing or ambiguous, fail visibly. Never choose a substitute.

### Phone connection

Use one small, app-private JSON message set and binary/audio frames. Implement only messages used by the current UI, for example:

- `StartCapture`
- audio chunks
- `EndCapture`
- `DraftUpdated`
- `SelectDestination`
- `ConfirmSend`
- `Cancel`
- `StatusUpdated`
- synthesized speech audio and playback state
- `ApprovalAudio`
- visible agent event/final-response updates

Do not build a general public protocol or compatibility layer. There is one current PC build and one current phone build; update them together.

For development:

- prefer Tailscale IP or a manually configured private LAN IP;
- use a manually entered PC address;
- keep connection state in memory;
- reconnect simply after disconnection;
- show connection failure plainly.

No QR pairing, TLS pinning, custom certificates, device identity, challenge/response, signature verification, attestation, key stores, lockout system, protocol negotiation, resumption protocol, durable delivery, or cross-platform conformance-vector suite. These are deferred unless actual use later proves they are worth adding.

## Initial local models

Do not build a multi-engine model platform before the application works.

- STT: integrate one Parakeet 0.6B English model first and measure it on the RTX 4070.
- Cleanup: integrate one quantized Gemma 4 E2B instruct model with a strict cleanup prompt.
  Chosen over Qwen3.5 0.8B during S002 on measured behavior, at the user's direction: both are
  reasoning models, but with few-shot prompting Gemma 4 returns a clean one-line rewrite in
  ~15 tokens, while Qwen3.5 spent its whole token budget reasoning and returned no text.
- If a model fails to load or run, show an error. Do not silently switch to another engine.
- TTS: select one local engine through a narrow Kokoro-versus-Piper measurement on this PC.
  Measure warm time-to-first-audio, streaming gap, real-time factor, intelligibility, and
  suitability for an original deep, cinematic, calm-authoritative machine voice. It may evoke
  the broad qualities the user likes in Optimus Prime—weight, restraint, clarity, resonance—but
  must not copy a specific actor, character performance, or protected voice. Commit to the winner;
  do not build a TTS engine marketplace or runtime fallback chain.
- TTS latency is a release gate for this personal MVP: keep the selected model warm, synthesize
  incrementally, begin playback from the first safe phrase/sentence, and never wait for a complete
  long agent response before speaking. Initial targets on the RTX 4070 are under 250 ms warm
  time-to-first-audio, under 150 ms between queued speech segments, and under 200 ms from the end
  of the review question to approval-listener readiness. Measure these on the actual machine;
  amend only when real measurements show a concrete limit.
- After the full PC and phone flows work, compare at most one serious alternative per stage on 20–30 real coding utterances. Replace the initial choice only if measured results are materially better.

Cleanup may remove fillers, repair punctuation/casing, format dictated identifiers, and apply a small personal glossary. It may not invent requirements, select a destination, or reinterpret intent. The user sees and can edit every result before confirmation.

## Error handling appropriate for this app

Handle only failures likely in normal personal use:

- microphone unavailable;
- hotkey registration failed;
- STT or cleanup failed to load/run;
- phone disconnected;
- configured destination window unavailable or ambiguous;
- insert/submit failed;
- user cancelled.

Show a plain-language error, leave the UI usable, and log timestamp, component, duration, and a short technical message. Do not create an exhaustive public error-code registry, multi-stage retry policy, durable queue, recovery state machine, or compatibility fallback system.

## Privacy and accepted development risk

- Keep routine audio in memory and discard it after transcription.
- Do not add telemetry, hosted relays, or provider credential storage.
- Operate the user's already-authenticated Windows apps.
- Store only local configuration and optional diagnostic logs.
- Trust the user's private LAN/Tailscale environment during MVP development.
- Security hardening is deliberately deferred. This MVP must not be exposed directly to the public internet.

## Remove or park this unnecessary infrastructure

The original release-oriented architecture is no longer active. Claude and Gemini should remove it from the implementation path and delete unused scaffold/code where safe:

- the T003 cross-platform protocol/crypto implementation;
- canonical encoders and hundreds of conformance vectors;
- public message catalogues and error registries;
- QR pairing and certificate plans;
- device identity, signatures, attestation, replay protection, and key-store abstractions;
- version negotiation and compatibility fallbacks;
- durable queues, distributed sessions, approval signing, and crash-resume protocols;
- generalized GPU lease scheduling and preemption proofs;
- large threat-model, latency-budget, benchmark, and release-audit machinery;
- unused projects, packages, interfaces, tests, and documents created only for those systems.

Do not merge the existing T003 branch into the MVP. Git history is sufficient if any of it is wanted later.

Keep only code that supports the current PC/phone vertical slice. Do not rewrite old infrastructure into a smaller framework; delete or exclude it.

## First useful-build acceptance

The user can complete ten consecutive prompts from Windows and ten from the Pixel 9a through:

`push-to-talk -> speech -> local transcript -> cleaned draft -> exact spoken review -> automatic spoken approval -> send -> visible agent updates -> spoken final response`

The MVP is useful when:

- no prompt is sent without confirmation;
- the destination never changes implicitly;
- ordinary use after the initial hold/release requires no mouse or second hotkey;
- TTS speaks the exact cleaned draft and exact destination before approval;
- unclear approval speech never sends;
- redictation fully replaces the prior draft;
- cleanup does not change intent in the dogfood set;
- Claude, Antigravity, and Codex can each receive confirmed text;
- a phone confirm/cancel/send cannot leave an independently sendable duplicate draft on the PC;
- visible agent completion is detected and its final response is spoken on the originating device;
- the user can switch between concise and comprehensive visible narration and independently
  enable or disable visible tool/skill announcements;
- warm TTS meets the measured latency targets or records the smallest proven target-machine limit;
- phone disconnects and ordinary component failures return to a usable state;
- stage timings identify real latency bottlenecks.

These are dogfood checks, not release certification.

## Delivery order

1. Remove/exclude obsolete release-grade protocol and security infrastructure.
2. Windows widget, hotkey, and in-memory microphone capture.
3. Local STT and cleanup visible in the widget.
4. Claude sender adapter, then Antigravity and Codex.
5. Minimal PC endpoint and Pixel 9a push-to-talk/confirmation UI.
6. Exact local TTS draft review and automatic spoken approval/redictation on PC.
7. Explicit spoken destination aliases.
8. Route the same TTS/approval loop through the Pixel.
9. Observe visible agent progress/final responses and speak event-driven feedback.
10. Full PC and phone dogfood pass with measured latency fixes, then installation polish.

Executable slices are in `docs/BACKLOG.md`.
