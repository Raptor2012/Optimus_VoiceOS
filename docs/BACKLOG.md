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

Status: done, verified on the real Pixel 9a. One TCP endpoint on port 8770 with a 5-byte frame
header (kind + big-endian length); JSON control messages and binary PCM over the same socket.
Full run on device: connect, capture, stop, draft returned, disconnect, reconnect. A live
utterance produced `audio 11.5s / STT 927 ms / cleanup 1473 ms / total 2401 ms`.

The endpoint binds 0.0.0.0 and has no authentication, per this slice. Windows Firewall blocks
inbound 8770 by default, so on-device testing used `adb reverse`; direct LAN or Tailscale use
needs an inbound rule the user adds themselves.

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

Status: implemented. The phone shows the raw transcript, an editable draft, the PC's destination
list with live readiness, Confirm and Cancel, and a final summary of what actually happened.
Confirm sends the exact text on screen to the exact destination chosen; the PC never substitutes
its own copy of the draft, and Confirm stays disabled until a ready destination is picked, with
the reason shown.

Verified by test: exact-text delivery, no send without confirm, refusal for an unready or unknown
destination with nothing sent anywhere, failure reported as not sent, and destinations pushed with
readiness. Verified on the Pixel: connect, capture, and status round trip.

Remaining: the ten-prompt count. It needs speech and a window bound on the PC, so it belongs to
the user's dogfood pass.

Before closing S005, fix two state bugs found in review:

- phone Cancel and phone send completion must transition the desktop widget too, so the same
  draft cannot remain independently confirmable and be sent twice;
- starting any new phone utterance must clear the prior destination selection, requiring an
  explicit choice for the new prompt.

## S006 — Local TTS and exact spoken review

Owner: Claude Opus 5

Make spoken review part of the primary desktop path. Run a narrow measurement of Kokoro and
Piper on the target PC, select one engine, and integrate only that winner. Create one original
deep, cinematic, calm-authoritative machine voice profile. It may have weight, restraint, clarity,
and resonance, but must not imitate Optimus Prime, a specific actor, or an existing performance.
Keep it warm and stream playback toward the latency targets in `PROJECT_PLAN.md`.

After cleanup, speak the exact cleaned draft followed by the exact destination and the question:
`Send this to <destination>, or redictate?` Disable capture during playback and play a short chime
when approval listening begins.

Done when ten varied drafts, including identifiers and punctuation, are displayed and spoken
exactly; playback starts quickly enough for normal use; microphone input cannot hear the app's
own TTS; warm time-to-first-audio and segment gaps are measured; and the mouse buttons remain
available only as fallback controls.

Do not build multiple-engine routing, cloud TTS, voice cloning, impersonations, barge-in, or a
general audio graph.

## S007 — Spoken approval, redictation, and explicit voice routing

Owner: Claude Opus 5

After the TTS review ends, automatically capture one short local command without another hotkey.
Recognize the finite affirmative, redictate, and cancel vocabulary in `PROJECT_PLAN.md`. Do not
run Gemma cleanup on approval commands. An unclear command reprompts and sends nothing.

Support explicit configured destination prefixes such as `To Codex Project Y`. Resolve only an
exact unique voice alias, remove the routing phrase from the displayed prompt, and ask when it is
missing or ambiguous. Clear the selected destination on every new utterance.

Done when the user can complete ten desktop prompts using only one initial hotkey hold/release,
with affirmative variants sending the exact visible snapshot, redictate replacing it completely,
cancel sending nothing, and ambiguous routing never selecting a destination.

## S008 — Pixel spoken review and approval

Owner: Claude Opus 5

Route synthesized review audio to the device that initiated the request. For a Pixel utterance,
the PC synthesizes the same exact draft/destination review and streams it to the phone; the phone
plays it, then captures the approval command after the chime. Confirm/cancel/send/result state is
mirrored between phone and PC as one lifecycle.

Done when ten Pixel-originated prompts can be reviewed, confirmed or redictated, and sent without
touching the PC or pressing a phone confirmation button. Disconnect during review or approval
sends nothing and returns to a usable state.

## S009 — Observe coding-agent windows

Owner: Claude Opus 5

Extend each exact-window adapter with the smallest practical Windows UI Automation observer.
Prototype and prove one application first, then implement Claude, Antigravity, and Codex-specific
observers. Capture newly visible English reasoning/progress messages, coarse events, visible tool
and skill activity, agent questions, completion, and the visible final response. Never claim
access to private hidden chain-of-thought, and deduplicate text repeated by UI rerenders.

Done when a real prompt to each application produces correctly attributed visible progress and a
final response from only the bound window; activity in another window is ignored.

Status: implemented and connected to the live send path. Observation is baselined immediately
before submit, remains scoped to the exact bound HWND, ignores the user's echoed prompt, emits
only appended visible text, and stops on a new run or stale binding. Automated exact-window,
rerender, streaming-suffix, classification, and mapping tests pass. Remaining: one real prompt
through each of Claude, Antigravity, and Codex to characterize their current UI Automation trees.

## S010 — Event-driven voice feedback

Owner: Claude Opus 5

Add a visible narration-mode control to both widget and phone: `Concise` and `Comprehensive`.
Concise speaks transitions such as planning, editing files, running tests, tests passed or failed,
agent question, completed, and the final response. Comprehensive streams every newly visible
English reasoning/progress message and the final response. Add a separate `Narrate tools & skills`
toggle that announces visible tool/skill names, intent, commands, and short results. Long code,
binary data, and repetitive logs are announced/summarized instead of spelled out.

Speech must be incremental: start on complete safe phrases/sentences instead of waiting for the
whole agent response, preserve ordering, and avoid speaking stale text after a newer run starts.

Done when PC- and Pixel-originated jobs receive ordered, non-repetitive speech on the correct
device; both narration modes work; the tools/skills toggle works independently; and long visible
responses begin speaking within the warm streaming latency budget rather than after completion.

Status: implemented end to end. The observer feeds the generation-safe narration scheduler and
warm Piper process; PC-originated runs play locally and Pixel-originated runs use the ordered S008
PCM stream without fallback rerouting. Concise/Comprehensive and the independent tools/skills
toggle are visible on both devices, with phone changes applied to the PC scheduler. Existing text
is baselined before send and stale speech is cancelled when a newer utterance starts. Remaining:
real-device dogfood to tune per-app UI text classification and narration phrasing.

## S011 — Dogfood and measured latency fixes

Owner: Claude Opus 5; Sol reviews only blockers

Use the app during real coding from both Windows and Pixel. Record simple timestamps, fix the largest measured delays, and remove friction discovered in use.

Done when the user says the tool is useful enough to keep running during normal work.

## Optional later slices

- S012: run-at-startup, settings polish, and a simple installer.
- S013: always-listening wake word for truly zero-key initiation, only if the user wants it after
  the one-hold workflow is proven.
- S014: security hardening only if the user later wants access outside private LAN/Tailscale or sees a concrete risk worth addressing.

## Review rule

Sol reviews working slices, not speculative documents. Report only defects that can cause unintended sending, wrong destination, intent change, loss of the current prompt, broken normal use, or failure of the slice's done conditions. Optional hardening is a note, not a review loop.
