# Fast multi-model workflow

The user controls the Claude, Antigravity/Gemini, and Codex sessions. Sol reviews only.

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
