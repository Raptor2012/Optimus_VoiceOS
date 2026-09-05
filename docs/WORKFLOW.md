# Fast multi-model workflow

The user controls the Claude, Antigravity/Gemini, and Codex sessions. Sol normally reviews only;
the user's September 5 request explicitly authorizes Codex implementation for this pass.

## Current handover — dogfooding pass, September 5

Workspace: `D:\SamHaydenVoiceTool\Optimus_VoiceOS`, main. This supersedes the section it replaces.
Head at handover: `91b5515`. 389 tests pass (`dotnet test Optimus.sln -c Release`).

### What this pass did

Ran the first real end-to-end dogfood on the PC path and fixed what it found. Every fix below came
from actually speaking to the tool, not from reading code.

- `a182db3` — continuous session. The first hold opens it; afterwards utterances need no key. A
  tap under 350 ms closes it. Session listening reuses the approval voice-activity detection in
  short re-armed windows, so idle audio is never accumulated.
- `fe3a01f` — a phone that announced a capture and dropped left the widget stuck in
  "Listening on Pixel..." forever. `PhoneSession` saw the disconnect but only wrote a status
  string. Pre-existing, not caused by this pass.
- `66fd0f4` — regression from the session work: session listening raised the same capture event a
  hold raises, so the widget announced "release key to finish" with no key down, and the loop then
  refused to act on a state only a key release could clear. Unrecoverable without a restart.
- `7203fc3` — `SendAsync` reported `Sent` when `SendInput` returned success, which says nothing
  about where the characters went. With Antigravity's project list focused, a draft became
  type-ahead navigation: the prompt never reached the conversation, the projects panel opened, and
  narration read the revealed menu items aloud as agent activity. Delivery is now verified before
  Return is pressed.
- `115a6fc` — raising a window leaves focus wherever the app put it, so the caret is now placed in
  the message box first. The box is found structurally, not by name: keyboard focusable, enabled,
  writable value, real screen space. Verified live — Claude `Edit 'Prompt'`, Antigravity
  `ComboBox 'Message input'`, ChatGPT `Edit 'Do anything'`. Property-condition searches are
  unreliable on these trees and miss the composer; the full descendant walk finds it.
- `91b5515` — approval matching accepted only exact phrases, so "send this to Claude" failed while
  the tool itself reads out "Send this to Claude, or redictate?". Replies are now read as words:
  loose matching for recoverable outcomes, and approval must lead the reply. Writing the refusal
  guard exposed a live hole — normalisation splits "don't" into "don" and "t", so "don't send
  that" had been classifying as approval.

### Decisions the user made in this pass

- Commander voice: kept the shipped `0.93 / 0.06` on `en_GB-northern_english_male` after a nine
  candidate A/B. Accepted that pitch coloration costs ~40 percent review length. Do not reopen.
- Voice cloning is out of scope. Requests to train on Optimus Prime and TARS audio were declined
  because both are living actors' performances; the Home Assistant community thread's voices are
  unlicensed character clones. Bryce Beattie's public-domain LibriVox voices are a clean source if
  a base change is ever wanted; `OPTIMUS_TTS_VOICE` overrides the model path with no code change.
- Continuous session: every hold opens one; only a hotkey tap closes it; no idle timeout. The user
  was told the mic then stays open indefinitely and accepted it.

### Next, agreed with the user and not yet started

1. LLM intent interpretation. The user finds deterministic command matching poor UX and wants
   Gemma to interpret intent. Agreed shape: keep the deterministic matcher as an instant fast
   path, fall through to Gemma for anything it does not recognise. Note Gemma is currently not
   running at all — `llama-server` only warms when cleanup is enabled, which is off by default —
   so this means keeping it resident.
2. Voice barge-in. The user wants to interrupt speech by talking, with the interruption
   interpreted as a new command (approval, correction, or fresh prompt). The blocker is acoustic,
   not logical: the mic is muted during TTS precisely so it cannot hear itself. The user uses
   headphones and speakers depending on the day, and chose: build the headphone path properly,
   gate it behind a setting defaulting to off, and do not claim it works on speakers until
   acoustic echo cancellation is proven.
3. Live workspace index. The user's idea, and a good one: the UIA walk already enumerates every
   project and conversation name in each app. Keeping that indexed gives deterministic matching
   over a vocabulary that is the actual workspace rather than memorised phrases. This is also the
   per-app conversation navigation that was previously listed as unimplemented.

### Known and unfixed

- Parakeet mistranscribes product names: "Antigravity" becomes "anti-gravity", "Optimus VoiceOS"
  became "Optimus voice or this project". No intent model fixes this; it needs a vocabulary bias
  list or cleanup repair.
- The ChatGPT app exposes two writable boxes; the one holding the caret wins. If neither holds it
  the send is refused rather than guessed.
- There is no log file anywhere in the shell. Diagnosis during this pass was done by dumping the
  widget's own window over UI Automation and inspecting processes and sockets, which shows current
  state but never how it was reached. A small rolling log of state transitions would have saved
  several round trips and is worth adding before the next dogfood.
- The UIA send verification and message-box focusing have no unit coverage; they read live
  focused elements and need a real send to exercise.
- Computer-use cannot attach to the widget window, so no screenshot QA was possible. The session
  badge has never been visually confirmed, only proven to parse.

### Useful during dogfooding

Read the widget's own state without a screenshot, using the project's UIA source against
`Optimus.Shell`. This is how every wedge in this pass was diagnosed. A scratch probe that walks a
window and prints its visible text takes about ten lines against `Optimus.Providers`.

The shell holds DLL locks; stop it before building. Settings live at
`%LOCALAPPDATA%\OptimusVoiceOS\preferences.json`.

## Starting work

Use the next slice from `docs/BACKLOG.md`. Give the implementer:

- the goal and done conditions;
- the exact repository/worktree;
- an instruction to build and run;
- an instruction not to add protocols, security layers, fallback engines, generalized frameworks, or unrelated hardening.

Ask Opus only a narrow question when a concrete difficult blocker exists. Do not ask Opus to design the whole product before Gemini can code.

## Implementation

Gemini makes the smallest complete change, runs targeted tests/builds, and explains exactly how the user can try it. Worktrees are useful for isolating code, but documentation gates and multi-round design reviews are not required.

## Review

Sol asks:

- Can it send without confirmation?
- Can it send to the wrong destination?
- Can cleanup change intent?
- Is ordinary PC or phone use broken?
- Does the promised runnable path actually work?

If none apply, pass and move on. Future hardening and hypothetical release risks do not block this personal tool.

## Current direction

The T003 protocol/crypto branch is parked and must not be merged into the MVP. S000-S005 provide
the first Windows/Pixel vertical slice. Finish the two recorded S005 state fixes, then implement
S006-S010 in order: exact local TTS review, automatic spoken approval and routing, Pixel audio,
exact-window observation, and event-driven spoken feedback. Do not turn any slice into a protocol,
security, or framework project.
# Current implementation handoff — 2026-09-05

Use branch `optimus/S002-stt-cleanup` in `D:\SamHaydenVoiceTool\Optimus_VoiceOS`.

Integrated commits:

- `e3846f6` — S008 ordered Pixel TTS transport and Android playback foundation.
- `25910f9` — S009/S010 exact-window observer → narration scheduler → Piper playback,
  including Windows/Pixel narration controls and origin-aware output.
- `9efde88` — Gemini S007 automatic desktop spoken approval, redictation, and exact
  voice-destination routing, conflict-resolved on top of S008–S010.

Verification after integration:

- `dotnet build Optimus.sln -c Release` succeeded with zero warnings.
- `dotnet test Optimus.sln -c Release --no-build` passed: 1 Contracts, 14 Inference,
  21 Providers, and 231 Core tests (267 total).
- Android tests and debug APK passed immediately before the S007 cherry-pick; S007 changed no
  Android files. Re-run the command below for final handoff evidence.

Next commands:

```powershell
cd D:\SamHaydenVoiceTool\Optimus_VoiceOS
dotnet test Optimus.sln -c Release
$env:JAVA_HOME='C:\Program Files\Microsoft\jdk-17.0.20.101-hotspot'
.\android\gradlew.bat -p android testDebugUnitTest assembleDebug
```

Next implementation priority:

1. Connect Piper review synthesis to `PhoneEndpoint.SendTtsAudio` for Pixel-originated drafts.
   S008 currently provides the transport/player and S010 uses it for agent narration, but the
   pre-send spoken draft review still plays on the PC through `SpokenReviewPlayer`.
2. After the Pixel review drains, capture its approval command automatically and feed the same
   finite S007 classifier. Disconnect must send nothing.
3. Dogfood one real prompt each in Claude, Antigravity, and Codex. Tune only concrete UIA text
   classification problems; do not restart architecture/security review.

Important invariants already preserved: explicit exact destination, exact visible draft, no
guessing, no send on unclear approval, one-send guard, observer baselined before submit, only the
bound HWND observed, stale narration cancelled, and Pixel audio never silently rerouted to PC.
