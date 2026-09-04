# Optimus .NET Source Tree (`src/`)

This directory contains the .NET 8 desktop assemblies, service host, and core libraries.

- **Governing ADR**: ADR-001 (Process boundaries, technology ownership, and dependency direction).
- **First Implementation Tasks**:
  - `Optimus.Contracts`: Owned by T003.
  - `Optimus.Client` and `Optimus.Service`: Owned by T004.
  - `Optimus.Shell`: Owned by T005 and T006.
  - `Optimus.Inference`: Owned by T007.
  - `Optimus.Providers`: Owned by T016 and T017.
  - `Optimus.Core`: Owned by T018, T019, T020, and T024.
