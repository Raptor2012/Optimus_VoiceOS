# Optimus Voice OS — Agent rules

## Authority

The user's latest instruction and `PROJECT_PLAN.md` are authoritative. The original ADRs, protocol specifications, threat model, and T003–T029 release backlog are obsolete for the personal-use MVP.

Goal: ship a working Windows + Pixel 9a vertical slice quickly.

## Roles

### Claude Opus 5

Use for hard architecture or debugging that blocks current implementation. Do not create long contracts, generalized protocols, speculative security systems, or future-proof frameworks.

### Gemini 3.8 Flash

Own routine implementation, cleanup, UI, model integration, sender adapters, phone transport, tests, and builds. Implement the smallest complete slice and leave it runnable.

### GPT-5.6 Sol

Review completed working slices only. Report blocker-level defects and failed done conditions. Do not implement product code, expand scope, or start repeated rounds over optional hardening.

## Workflow

1. The user starts Claude or Gemini.
2. Give it one slice from `docs/BACKLOG.md`.
3. It implements, runs, and records concise evidence.
4. Sol reviews once for blockers.
5. Fix blockers; otherwise move forward and dogfood.

Do not require an ADR, decision-complete contract, protocol review, or security review before routine feature work.

## Non-negotiable behavior

- Always show the exact destination before sending.
- Never guess or silently substitute a destination.
- Always require explicit confirmation.
- Cleanup must not change intent.
- Keep routine audio local and ephemeral.
- Do not store provider credentials.
- Do not expose the PC endpoint directly to the public internet.

## Deliberate simplifications

Do not add these during the MVP:

- release-grade pairing, cryptography, attestation, replay protection, certificate management, or key storage;
- public/versioned wire protocols or cross-platform conformance vectors;
- compatibility fallback engines or automatic provider/model switching;
- durable queues, distributed sessions, approval systems, or complex recovery;
- exhaustive error taxonomies and catch/retry layers;
- generalized GPU scheduling and process supervision;
- release audits, telemetry, hosted services, or enterprise abstractions.

Use private LAN/Tailscale, a manually configured PC address, a tiny direct message set, and simple reconnect behavior for the one phone.

## Change discipline

- Preserve unrelated user work.
- Remove obsolete infrastructure instead of wrapping it in another abstraction.
- Keep each slice runnable.
- Test changed behavior and normal failures, not hypothetical product-scale scenarios.
- Log failures plainly and leave the UI usable.
- Do not commit secrets, models, recordings, or machine-local configuration.
- Optimize measured latency in the real user path.
