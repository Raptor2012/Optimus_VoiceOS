# Optimus Inference Runners Tree (`runners/`)

This directory contains standalone Python/CUDA runner processes for local inference models.

- **Governing ADR**: ADR-001 (Section 1 Process roles) and ADR-004 (Inference lifecycle and GPU scheduling).
- **First Implementation Tasks**:
  - `runners/asr`: Owned by T008 (Streaming transcription with Parakeet/Qwen3-ASR).
  - `runners/cleanup`: Owned by T009 (Prompt cleanup and intent guard).
  - `runners/voicedesign`: Owned by T013 (Qwen3-TTS 1.7B VoiceDesign setup flow).
  - `runners/tts`: Owned by T014 (TTS streaming runtime and pre-render cache).
