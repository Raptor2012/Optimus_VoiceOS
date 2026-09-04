# Agile personal-use backlog

- Status: Active
- Owner: User
- Current implementer: Claude Opus 5 while Gemini's five-hour allocation recovers
- Product plan: `PROJECT_PLAN.md`

This backlog supersedes the original T003–T029 release plan. Each slice must end with something runnable or directly testable by the user. Do not add speculative protocols, security systems, fallback layers, or generalized frameworks.

## S000 — Remove obsolete infrastructure

Owner: Gemini

Clean the active MVP branch before more feature work:

- do not merge the T003 protocol/crypto branch;
- keep only these active Markdown files: `README.md`, `PROJECT_PLAN.md`, `AGENTS.md`, `docs/BACKLOG.md`, and `docs/WORKFLOW.md`;
- delete every other tracked `*.md` file, including old ADRs, architecture/spec/security/performance documents, T001/T002 task contracts, historical reviews, directory placeholder READMEs, and obsolete Claude/Gemini/Sol prompts;
- remove unused release-oriented protocol, security, conformance-vector, pairing, approval-signing, durable-session, and GPU-scheduler scaffold from active solution/project files;
- delete obsolete generated tests and dependencies that serve only that infrastructure;
- reduce active documentation to the personal-use plan and executable slices;
- preserve reusable WPF, Android, capture, and basic adapter scaffolding;
- keep the repository building.

Do not archive obsolete Markdown elsewhere and do not replace deleted systems with new abstractions. Git history is the archive. The cleanup prompt itself may be deleted after it has been read.

Done when the Windows and Android scaffolds build, `rg --files -g "*.md"` returns exactly the five active files above, the active solution contains only components needed for the PC/Pixel vertical slice, and the final chat response briefly lists what was removed.

## S001 — Windows hotkey, widget, and microphone

Owner: Gemini

Status: Implemented at `8ddb0a9`; the real-device WASAPI format/release correction is folded into S002.

Build a runnable WPF app with a configurable hold-to-talk hotkey, in-memory WASAPI capture, and a tiny floating widget.

Done when:

- the user can launch it;
- press starts capture and release stops it;
- widget state is visible;
- no audio is written to disk;
- hotkey/microphone failures show a useful error and recover.

## S002 — Local STT and cleanup

Owner: Claude Opus 5

Connect captured audio to one Parakeet 0.6B English runtime. Display the raw transcript, then run one local Gemma 4 E2B cleanup pass. Allow editing and cancellation.

Status: WASAPI repair and both runtimes implemented; widget shows raw transcript, editable cleaned draft, and stage timings. Cleanup model switched from Qwen3.5 0.8B to Gemma 4 E2B at the user's direction during the slice, on measured behavior (`PROJECT_PLAN.md`, "Initial local models"). Remaining before this slice is done: the ten-real-prompt dogfood run, which needs a working microphone.

Before model integration, repair the S001 WASAPI interop blocker: give `WAVEFORMATEX`/`WAVEFORMATEXTENSIBLE` their native layout, correctly recognize extensible float/PCM input, always release every successfully acquired non-empty WASAPI packet, and prove the returned buffer is 16 kHz mono PCM16. Continue directly into S002 after this focused repair; do not create a separate design or review cycle.

Done when ten real coding prompts produce visible drafts, stage timings are logged, and the confirmed snapshot exactly matches what is shown.

Do not implement alternate engines, automatic fallbacks, a model marketplace, GPU arbitration, or benchmark infrastructure.

## S003 — Windows destination adapters

Owner: Claude Opus 5

Let the user bind destination cards to exact open Claude, Antigravity, and Codex Windows application targets. Implement focus, insertion, and submit through the tiny adapter interface.

Done when:

- Claude receives ten confirmed prompts;
- Antigravity and Codex each receive five;
- switching is always manual and visible;
- an unavailable or ambiguous window fails without sending elsewhere.

Status: adapters implemented and verified. Configured targets are the `claude`, `Antigravity`
and `ChatGPT` processes; Codex is a workspace inside the ChatGPT desktop app, so the adapter
binds that app's window and cannot detect which workspace is selected.

Verified on the real machine: the last two done-conditions hold — switching is manual (nothing
is selected or bound by default, and a lone candidate still needs an explicit bind), and the
ambiguous case fires for real (the ChatGPT app has two identical top-level windows, reported as
`AmbiguousWindow` with no send possible). The live Win32 path was proven end to end against a
controlled window: focus, Unicode typing and submit, with the received text compared
byte-for-byte against the confirmed draft.

Remaining: the prompt-volume conditions (ten to Claude, five each to Antigravity and Codex).
Those submit real turns to live agent sessions, and the Claude desktop app hosts the session
doing this work, so they belong to the user's dogfood pass rather than an automated run.

## S004 — Minimal PC-to-phone connection

Owner: Claude Opus 5

Add one direct PC endpoint for the Pixel app over private LAN/Tailscale. Use a tiny current-version JSON/audio message set. Keep connection state in memory and use simple reconnect behavior.

Done when the Pixel can connect using a manually configured PC address, start/stop capture, receive draft/status updates, and disconnect/reconnect.

Explicitly forbidden in this slice:

- QR pairing;
- TLS pinning or custom certificates;
- authentication challenge protocols;
- signatures, MACs, attestation, key stores, replay ledgers, or biometric signing;
- version negotiation, compatibility fallbacks, resume tokens, durable delivery, or protocol conformance vectors.

## S005 — Pixel 9a UI and confirmation

Owner: Claude Opus 5

Build the small Jetpack Compose phone UI: push-to-talk, transcript/cleaned draft, explicit destination, edit, confirm, cancel, status, and final summary.

Done when ten phone-originated prompts are processed on the PC and sent to the exact visible destination only after confirmation.

## S006 — Dogfood and measured latency fixes

Owner: Claude Opus 5; Sol reviews only blockers

Use the app during real coding from both Windows and Pixel. Record simple timestamps, fix the largest measured delays, and remove friction discovered in use.

Done when the user says the tool is useful enough to keep running during normal work.

## Optional later slices

- S007: concise event summaries and minimal local TTS.
- S008: run-at-startup, settings polish, and a simple installer.
- S009: security hardening only if the user later wants access outside private LAN/Tailscale or sees a concrete risk worth addressing.

## Review rule

Sol reviews working slices, not speculative documents. Report only defects that can cause unintended sending, wrong destination, intent change, loss of the current prompt, broken normal use, or failure of the slice's done conditions. Optional hardening is a note, not a review loop.
