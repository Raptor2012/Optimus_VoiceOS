# Optimus Voice OS

A personal Windows + Pixel 9a voice layer for coding agents.

Current flow:

`push-to-talk -> local STT -> local cleanup -> inspect/edit -> explicit destination -> confirm -> send`

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

Nothing is selected or bound automatically. Pick a destination, then bind it to one exact
window; when an app has several windows (the ChatGPT app normally has two) the widget lists them
and refuses to send until you choose. A closed or ambiguous target fails the send and leaves the
draft untouched — it never falls back to another window.

Because Codex and ChatGPT share one window and are switched by an in-app selector, the adapter
cannot tell which workspace is active. Make sure the bound window has Codex selected.

Model-backed and UI tests are opt-in so the normal suite stays fast:

```powershell
$env:OPTIMUS_MODEL_SMOKE=1   # Parakeet + Gemma end-to-end
$env:OPTIMUS_MIC_SMOKE=1     # real microphone capture
$env:OPTIMUS_UI_SMOKE=1      # live focus/type/submit (steals focus while it runs)
dotnet test .\Optimus.sln -c Release
```

Next: S000 removes obsolete infrastructure; then S001 begins the runnable Windows path.
