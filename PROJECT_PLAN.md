# Optimus Voice OS — Personal-use implementation plan

## Goal

Build the fastest practical voice layer for one user, one Windows 11 PC, and one Pixel 9a.

The working flow is:

1. Hold push-to-talk on the PC or phone.
2. Speak.
3. Transcribe on the PC with fast local STT.
4. Clean the text with a small local LLM.
5. Show the exact draft and an explicitly selected destination.
6. Send only after the user confirms.
7. Show short event-driven status and completion feedback.

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
- `Sending`
- `Sent`
- `Error`

It shows:

- transcript or cleaned draft;
- explicit destination card;
- edit, confirm, and cancel controls;
- one short status line.

### Pixel 9a app

Build a small native Kotlin/Jetpack Compose app that mirrors the useful parts of the widget:

- push-to-talk;
- transcript and cleaned draft;
- explicit destination selection;
- edit, confirm, and cancel;
- current agent status;
- short final summary.

The phone captures audio; the PC performs STT, cleanup, and agent sending. The phone is a remote control for the user's PC, not an independent inference server.

### Destination and confirmation

- The user manually configures each destination by selecting an open Windows application window and assigning a label.
- The selected destination is visible before every send.
- The app never guesses, substitutes, or silently changes a destination.
- PC confirmation is a local in-memory UI action.
- Phone confirmation is a simple message to the PC over the existing private connection.
- The PC sends the exact confirmed draft snapshot.
- No cryptographic receipt, MAC, attestation, replay ledger, or approval-signing system is needed for this personal-use MVP.

### Agent feedback

Start with visual event updates and a final text summary. After the core flow works, add concise local voice phrases for meaningful transitions such as tests failed, agent question, and work completed. Do not narrate ordinary file edits.

Custom voice design and sophisticated streaming TTS are optional later work, not prerequisites for a testable build.

## Lean architecture

Prefer the smallest implementation that works on the target devices.

### PC

Use the existing .NET 8/WPF scaffold. Keep the MVP in one desktop process unless a measured runtime problem forces a boundary.

Required pieces:

- global hotkey;
- in-memory WASAPI capture;
- one local STT integration;
- one local cleanup integration;
- floating WPF widget;
- local destination configuration;
- one small Windows sender adapter per target application;
- a minimal HTTP/WebSocket endpoint for the Pixel app;
- simple timing logs around the real path.

A sender adapter needs only:

- `IsAvailable`
- `ActivateConfiguredDestination`
- `InsertText`
- `Submit`

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

`push-to-talk -> speech -> local transcript -> cleaned draft -> explicit destination -> confirmation -> send`

The MVP is useful when:

- no prompt is sent without confirmation;
- the destination never changes implicitly;
- cleanup does not change intent in the dogfood set;
- Claude, Antigravity, and Codex can each receive confirmed text;
- phone disconnects and ordinary component failures return to a usable state;
- stage timings identify real latency bottlenecks.

These are dogfood checks, not release certification.

## Delivery order

1. Remove/exclude obsolete release-grade protocol and security infrastructure.
2. Windows widget, hotkey, and in-memory microphone capture.
3. Local STT and cleanup visible in the widget.
4. Claude sender adapter, then Antigravity and Codex.
5. Minimal PC endpoint and Pixel 9a push-to-talk/confirmation UI.
6. Full PC and phone dogfood pass with measured latency fixes.
7. Optional event summaries/TTS and installation polish.

Executable slices are in `docs/BACKLOG.md`.
