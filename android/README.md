# Optimus Android Client Tree (`android/`)

This directory contains the Kotlin / Jetpack Compose Android client for Pixel 9a (API 35).

- **Governing ADRs**: ADR-001 (Section 6 Android module direction) and ADR-003 (Section 3 TLS stack and ECDSA P-256).
- **First Implementation Tasks**:
  - `:core:protocol`: Owned by T003 (Protocol contracts and vectors).
  - `:app`, `:core:transport`, `:core:security`, `:feature:pairing`: Owned by T021 (Transport, pinning, and pairing).
  - `:core:audio`, `:feature:talk`: Owned by T022 (Talk screen, audio capture, and confirmation card).
  - `:feature:sessions`: Owned by T023 (Sessions screen, approvals, and biometric attestation).
