# Optimus Voice OS

A personal Windows + Pixel 9a voice layer for coding agents.

Current flow:

`talk -> local STT -> remembered/voice-selected destination -> spoken review -> say send`

Say `switch to Claude`, `remember this as voice project`, `replace blue with green`,
`add unit tests`, `use original`, `read that again`, or `cancel` during review.
Cleanup is off by default (`cleanup on` enables it). Exact full readback is the default;
`short readback` announces the destination only. PC and Pixel share the same voice workflow.
An initial PC hotkey hold or Pixel start/stop tap is still needed; this is not always-listening yet.

The immediate target is one user, one PC, and one phone over private LAN/Tailscale. Release-grade pairing, crypto, compatibility layers, and generalized infrastructure are deliberately deferred.

Read:

- `PROJECT_PLAN.md` — current scope and deliberate non-goals.
- `docs/BACKLOG.md` — executable vertical slices.
- `AGENTS.md` — lean Claude/Gemini/Sol responsibilities.
- `docs/WORKFLOW.md` — fast implementation and review flow.

## Local models

Weights are large and are **not** committed. They live outside the repository, by default under
`D:\SamHaydenVoiceTool\models` (override with `OPTIMUS_MODELS_DIR`):

| Path | Source | Size |
| --- | --- | --- |
| `parakeet-tdt-0.6b-v2-int8/` (`encoder.int8.onnx`, `decoder.int8.onnx`, `joiner.int8.onnx`, `tokens.txt`) | `csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8` | ~631 MB |
| `gemma-4-e2b/gemma-4-E2B_q4_0-it.gguf` | `google/gemma-4-E2B-it-qat-q4_0-gguf` | ~3.2 GB |

Cleanup runs through `llama-server` from a llama.cpp release build, by default at
`D:\SamHaydenVoiceTool\runtimes\llama.cpp\llama-server.exe` (override with `OPTIMUS_LLAMA_SERVER`).
It binds loopback only and is killed with the app via a Windows job object.

Run the widget:

```powershell
dotnet run --project .\src\Optimus.Shell -c Release
```

Add `--no-models` for capture-only (skips the ~3.8 GB load), or `--mock` for no hardware at all.

## Destinations

Three fixed Windows targets, matched by process name:

| Destination | Process | Note |
| --- | --- | --- |
| Claude | `claude` | Claude desktop app |
| Antigravity | `Antigravity` | Antigravity IDE |
| Codex (ChatGPT app) | `ChatGPT` | Codex is a workspace *inside* the ChatGPT desktop app |

Choose an agent by voice once. A lone first-use window binds without another click; several
windows get a spoken numbered choice. Your choice is remembered and announced before each send.
After restart, saved titles must match exactly and uniquely; otherwise voice selection resumes.
Say `remember this as [name]` to create a personal window alias. Aliases do not navigate hidden tabs.

Because Codex and ChatGPT share one window and are switched by an in-app selector, the adapter
cannot tell which workspace is active. Make sure the bound window has Codex selected.

Model-backed and UI tests are opt-in so the normal suite stays fast:

```powershell
$env:OPTIMUS_MODEL_SMOKE=1   # Parakeet + Gemma end-to-end
$env:OPTIMUS_MIC_SMOKE=1     # real microphone capture
$env:OPTIMUS_UI_SMOKE=1      # live focus/type/submit (steals focus while it runs)
dotnet test .\Optimus.sln -c Release
```

Build Pixel: `.\android\gradlew.bat -p android testDebugUnitTest assembleDebug`.
APK: `android/app/build/outputs/apk/debug/app-debug.apk`.
Next: dogfood voice switching, spoken edits and readback from both devices; then implement
continuous session initiation and actual in-app conversation navigation.
