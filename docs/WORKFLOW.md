# Fast multi-model workflow

The user controls the Claude, Antigravity/Gemini, and Codex sessions. Sol normally reviews only;
the user's September 5 request explicitly authorizes Codex implementation for this pass.

## Current handover — voice-first UX pass, September 5

Workspace: `D:\SamHaydenVoiceTool\Optimus_VoiceOS`, main, based on `c01d87d`.
This section supersedes the historical handoff below. Do not redo S007/S008 phone approval.

Implemented: remembered agent and exact-window titles, voice aliases, first-use lone-window binding,
spoken destination recovery, spoken draft edits with fresh review, cleanup off by default, short/full
review preference, shared PC/Pixel pipeline, phone saved address/launch reconnect, matching dark UI,
and a modest lower-register metallic commander effect using the existing Piper engine.

Verification: 362 .NET tests passed including the window-sizing adjustment; Android unit
tests and debug APK build succeeded. Actual Piper warm first-byte median 248 ms, RTF 0.165 over five
runs. This measures engine bytes, not first audible output. Windows app launched, then was stopped
for rebuilding. Computer-use capture failed (`no screenshot targets found`); visual QA unverified.
ADB reported no attached Pixel, so the new APK has NOT been installed or visually tested there.

Next actions, no architecture detour:

1. Run `dotnet test Optimus.sln -c Release`, then launch the shell and dogfood one actual prompt.
2. Install `android/app/build/outputs/apk/debug/app-debug.apk` when the Pixel reconnects; verify
   same voice workflow, matching UI, remembered destination and spoken correction.
3. Add a real continuous voice-session / follow-up initiation path. The initial PC hold / Pixel
   start-stop tap is still required. Do not claim wake-word or barge-in currently works.
4. Implement actual per-app conversation/task navigation. Saved aliases target window titles only;
   they cannot select hidden tasks/tabs in Claude, Antigravity or Codex.
5. Listen to the commander profile with the user; current stock British base voice + mild effect is
   NOT a reproduced Optimus Prime performance. Do not spend days tuning without an A/B listen.

Settings: `%LOCALAPPDATA%\OptimusVoiceOS\preferences.json` (no prompt/audio history). Voice commands
are documented in README and PROJECT_PLAN. Unmute affects subsequent runs, not cancelled narration.
Continue checking the 5h allowance; below 5% remaining, update this handover and stop implementation.

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
