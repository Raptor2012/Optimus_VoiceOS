# Optimus Voice OS

Optimus is a local-first natural-language desktop operator for a Windows 11 PC and a Pixel 9a.
It keeps routine audio, transcription, reasoning, and memory on the local devices. The PC
companion can drive supported desktop agent windows and can connect to the phone over a private
LAN or Tailscale network.

## Quick start: PC companion

Prerequisites:

- Windows 11 and the .NET 8 SDK.
- The local model assets listed in [Local models](#local-models).
- Optional: a running AO daemon on `http://127.0.0.1:3001`.

From the repository root:

```powershell
dotnet restore .\Optimus.sln
dotnet build .\Optimus.sln -c Release
dotnet run --project .\src\Optimus.Shell -c Release
```

Useful development switches:

- `--no-models` starts the widget without loading STT or LLM models.
- `--no-voice` disables local TTS.
- `--no-phone` disables the PC-to-Pixel TCP endpoint.
- `--mock` uses in-memory capture and mock hotkeys for UI-only work.
- `--draft "text"` opens a draft without using the microphone.

Hold **F8**, speak a plain-English request, and release. The coordinator resolves the request's
destination from the current turn and available windows. If the target is ambiguous or a
destructive action needs approval, Optimus asks a clarifying question instead of guessing.

## Quick start: Pixel 9a over wireless debugging

1. On the Pixel, enable **Developer options** and **Wireless debugging**.
2. On the PC, install the Android SDK platform tools and pair with the pairing address shown on
   the phone:

   ```powershell
   adb pair <phone-ip>:<pairing-port>
   adb connect <phone-ip>:<debug-port>
   ```

3. Build and install the debug APK:

   ```powershell
   .\android\gradlew.bat -p android testDebugUnitTest
   .\android\gradlew.bat -p android :app:assembleDebug
   adb install -r .\android\app\build\outputs\apk\debug\app-debug.apk
   ```

4. Open Optimus on the phone, enter the PC's private-network address and port `8770`, then tap
   **Connect**. Wireless debugging is only for deploying the APK; voice traffic uses Optimus's
   private PC endpoint, not `adb`.

## Architecture

The end-to-end pipeline is deliberately small:

1. **Capture** — a Windows hotkey or Pixel gesture captures 16 kHz mono PCM in memory.
2. **Transcribe** — local Parakeet produces text; captured audio is discarded after use.
3. **Coordinate** — `ConversationCoordinator` combines the turn, browsing context, request
   context, monitoring context, and stored preferences. It binds executable work to one resolved
   destination and asks when required information is missing.
4. **Execute** — a concrete Windows/AO adapter performs the ordinary navigation, typing, or send.
5. **Observe** — agent responses are monitored locally and reduced to a short spoken summary,
   with decisions exposed for the next natural-language turn.
6. **Speak and transport** — Piper renders local TTS; the PC and Pixel exchange small framed
   messages and audio segments over the private endpoint.
7. **Remember** — SQLite stores aliases, preferences, turns, and summaries under local app data.

The resident local model stack handles conversation and cleanup. There is no cloud fallback and
no public internet endpoint in the MVP.

## Local models

Weights are large and are **not** committed. By default they live under
`D:\SamHaydenVoiceTool\models`; override that root with `OPTIMUS_MODELS_DIR`.

| Path | Source | Approx. size |
| --- | --- | ---: |
| `parakeet-tdt-0.6b-v2-int8/` (`encoder.int8.onnx`, `decoder.int8.onnx`, `joiner.int8.onnx`, `tokens.txt`) | `csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8` | 631 MB |
| `gemma-4-e2b/gemma-4-E2B_q4_0-it.gguf` | `google/gemma-4-E2B-it-qat-q4_0-gguf` | 3.2 GB |
| `piper-voices/en_GB-northern_english_male-medium.onnx` (+ `.json`) | Piper voice asset | varies |

Cleanup uses `llama-server`; its executable defaults to
`D:\SamHaydenVoiceTool\runtimes\llama.cpp\llama-server.exe` and can be overridden with
`OPTIMUS_LLAMA_SERVER`. Piper can be overridden with `OPTIMUS_PIPER`, and the voice model with
`OPTIMUS_TTS_VOICE`.

## Configuration reference

The checked-in default configuration is `src/Optimus.Shell/appsettings.json` and is copied beside
the shell executable on build. It contains no credentials:

| Section | Setting | Default |
| --- | --- | --- |
| `Llm` | `Endpoint` | `http://127.0.0.1:8080` |
| `Llm` | `Provider` | `llama-server` |
| `Ao` | `Endpoint` | `http://127.0.0.1:3001` |
| `Tts` | `Voice` | `en_GB-northern_english_male-medium` |
| `Tts` | `LengthScale`, `NoiseScale`, `NoiseW`, `SentenceSilenceSeconds` | `1.0`, `0.55`, `0.60`, `0.15` |
| `Hotkey` | `Key` / `VirtualKeyCode` | `F8` / `119` |
| `Memory` | `DatabasePath` | `%LOCALAPPDATA%\Optimus\memory.db` |

Runtime-specific model paths can be set with the environment variables above. Personal window
aliases and preferences are stored separately at `%LOCALAPPDATA%\OptimusVoiceOS\preferences.json`;
audio recordings and cloud credentials are never stored.

## Verification

The normal .NET suite is hardware-free:

```powershell
dotnet test .\Optimus.sln -c Release
```

Optional smoke tests:

```powershell
$env:OPTIMUS_MODEL_SMOKE = 1   # Parakeet + Gemma end-to-end
$env:OPTIMUS_MIC_SMOKE = 1     # real microphone capture
$env:OPTIMUS_UI_SMOKE = 1      # live focus/type/submit; steals focus
dotnet test .\Optimus.sln -c Release
```

Read `PROJECT_PLAN.md` for current scope, `docs/BACKLOG.md` for slices, and `docs/WORKFLOW.md`
for the implementation and review flow.
