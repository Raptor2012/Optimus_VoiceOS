# Optimus Voice OS — Agent Rules

## Authority

The user's latest instruction and `PROJECT_PLAN.md` are authoritative. The original rigid command vocabulary, permanent destination locking, mandatory draft confirmation, and obsolete T003–T029 release backlog are superseded by the natural-language desktop operator specification.

Goal: Deliver a fully local, natural-language desktop operator vertical slice for Windows 11 PC + Pixel 9a.

---

## Roles

### Claude Opus 5
Use for hard architecture, deep debugging, and complex system interactions that block implementation. Do not create speculative frameworks, enterprise abstractions, or generalized protocol suites.

### Gemini 3.8 Flash
Own routine implementation, cleanup, UI/Compose, local model integration, application adapters, phone transport, tests, and builds. Implement the smallest complete slice and leave it runnable.

### GPT-5.6 Sol
Review completed working slices only. Report blocker-level defects and failed done conditions. Do not implement product code, expand scope, or introduce circular hardening reviews.

---

## Workflow

1. The user or orchestrator selects one slice from `docs/BACKLOG.md`.
2. The agent implements, runs tests, and records concise evidence.
3. Sol reviews once for blockers.
4. Fix blockers; otherwise move forward and dogfood.

Do not require an ADR, decision-complete contract, or security review before routine feature work.

---

## Non-Negotiable Behavior

- **Natural Interaction**: The user describes what they want in plain English. Never require fixed command syntax, "tell"/"send" prefixes, or mode switching.
- **Context-Aware Targeting**: Bind each executable request to its resolved destination. Do not enforce a permanent destination lock. Never guess or silently substitute a destination when ambiguous; ask clarifying questions instead.
- **Follow-Through & Authority**: A clear instruction authorizes its ordinary navigation, composition, typing, and intended send. Do not demand an extra "confirm" or mandatory draft review for an already explicit instruction.
- **Destructive Operations Guard**: Require explicit confirmation only for destructive actions, ambiguous targets, or when vital parameters are missing.
- **Privacy & Local Isolation**: Keep all routine audio local and in-memory; discard after transcription. Never store cloud provider credentials. Do not expose the PC endpoint directly to the public internet.
- **No Frontier Leakage**: Desktop orchestration and local conversation must be handled by the resident local model stack, not by burning cloud frontier API tokens.

---

## Deliberate Simplifications (MVP Scope)

Do not add these during the personal-use MVP:
- Release-grade pairing, cryptographic attestation, replay ledgers, or certificate management.
- Public versioned wire protocols or cross-platform conformance vector suites.
- Automatic model switching to paid cloud fallbacks.
- Durable distributed queues, approval-signing systems, or multi-stage retry policies.
- Generalized GPU lease schedulers and enterprise telemetry layers.

Use private LAN/Tailscale, a manually configured PC address, a minimal direct message set, and simple reconnect behavior.

---

## Change Discipline

- Preserve unrelated user work and existing test suites.
- Remove obsolete infrastructure instead of wrapping it in another abstraction layer.
- Keep each slice runnable and verify with `dotnet test` and `./gradlew test`.
- Test real behavior and concrete failure modes, not hypothetical enterprise scenarios.
- Log failures plainly and leave the UI usable.
- Do not commit secrets, proprietary model weights, audio recordings, or machine-local configuration.
