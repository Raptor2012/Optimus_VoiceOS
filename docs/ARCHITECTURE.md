# Optimus Voice OS architecture overview

- Status: Accepted
- Owner: Claude Opus 5

This is the entry point to the architecture. It states the shape of the system and points at the decision documents. Where this overview and an ADR disagree, the ADR wins.

## Document map

| Document | Decides |
| --- | --- |
| `docs/adr/ADR-001-process-and-subsystem-boundaries.md` | Processes, ownership, dependency direction, recovery responsibilities |
| `docs/adr/ADR-002-device-protocol-and-compatibility.md` | Transport, versioning, framing, ordering, reconnect, error model |
| `docs/adr/ADR-003-pairing-authentication-and-secrets.md` | PC identity, QR pairing, pinning, per-connection auth, secret storage |
| `docs/adr/ADR-004-inference-lifecycle-and-gpu-scheduling.md` | Runner lifecycle, model residency, GPU scheduling, cancellation, crash recovery |
| `docs/adr/ADR-005-confirmation-sessions-and-approvals.md` | Confirmation integrity, destination binding, sessions, Queue, Steer, questions, approvals |
| `docs/specs/WIRE_PROTOCOL.md` | Every device message, error code, and conformance vector |
| `docs/specs/SENDER_ADAPTER.md` | The provider-neutral adapter contract |
| `docs/security/THREAT_MODEL.md` | Assets, boundaries, attackers, mitigations, residual risks |
| `docs/performance/LATENCY_BUDGET.md` | The 500 ms warm path allocation and how each segment is measured |
| `docs/performance/BENCHMARK_PLAN.md` | Corpora, run protocol, statistics, correctness gates, reproducibility |
| `docs/BACKLOG.md` | Dependency-ordered task list with owners and completion gates |

## Runtime shape

```
  Pixel 9a                                  Windows 11 PC
+-----------+                     +-------------------------------------+
| Android   |                     |  Optimus.Shell.exe (WPF, tray)      |
| app       |                     |  hotkey, widget, WASAPI, playback   |
|           |                     +------------------+------------------+
| capture   |                                        | wss 127.0.0.1
| status    |   wss, pinned TLS 1.3                  | (same protocol)
| approvals |======================================> |
+-----------+                     +------------------v------------------+
                                  |  Optimus.Service.exe (Core)         |
                                  |  device endpoint, state machine,    |
                                  |  confirmation authority, sessions,  |
                                  |  GPU scheduler, runner supervisor   |
                                  +---+-------------+-------------+-----+
                                      | named pipes |             | stdio
                              +-------v---+  +------v----+  +-----v-------+
                              | asr runner|  |cleanup    |  | provider    |
                              |           |  |runner     |  | CLIs        |
                              +-----------+  +-----------+  | (Claude Code|
                              | tts runner|                 |  , Codex)   |
                              +-----------+                 +-------------+
```

Three facts define the system:

1. **The PC is the only inference and agent server.** The phone captures, displays, and approves. No model runs on the phone and no adapter code exists there.
2. **The desktop UI is a protocol client.** It speaks the same WebSocket protocol as the phone, so there is exactly one orchestration path and exactly one confirmation implementation.
3. **Nothing sends without a consumed confirmation.** An adapter cannot be handed prompt text without a `ConfirmationReceipt`, and a receipt exists only after Core validated ten ordered checks against a single-use, MAC-bound confirmation. A confirmation always names an explicit session the user created or resumed; Core never creates a provider session as a side effect.

Two boundaries follow from those facts and are stated here because they constrain every later task:

- **Optimus holds no provider credentials.** Adapters drive a CLI the user already authenticated in the provider's own tool. Optimus reports `PROVIDER_AUTH_REQUIRED` and never collects, stores, or injects a token.
- **Nothing about a prompt is persisted except the opt-in text history and a session title.** Queues are in-memory and are reported as dropped after a restart; routine audio is never written at all.

## The warm path in one paragraph

The user holds the hotkey. The Shell starts an already-initialized WASAPI client and streams 20 ms PCM frames to Core, which streams them to the ASR runner; accepting the capture also clears the GPU slot of speech and background work so the interactive path never waits for an eviction. On release, the ASR runner finalizes, Core normalizes and applies the glossary, then the cleanup runner produces a conservative draft that may remove fillers and repair punctuation but may not change intent. Core sends a `PromptDraft`. The user picks an explicit destination and an explicit session, then asks for confirmation; Core issues a `ConfirmationRequest` bound to the exact text, destination, session, device, expiry, and a one-time nonce. The user confirms, the device echoes the exact bytes it rendered from one immutable view model, Core validates and consumes the confirmation, and only then does a sender adapter receive a `ConfirmedPrompt`. The whole path to a visible draft is budgeted at 500 ms p95.

## Non-negotiables

These come from `PROJECT_PLAN.md` and are restated here because every design choice above serves them:

- Never send a prompt without a visible explicit destination and user confirmation.
- Never infer or silently change the destination.
- Cleanup may repair presentation but may not add, remove, or reinterpret intent.
- Ordinary voice processing and audio remain local, and routine audio is not retained.
- Agent cloud behavior remains unchanged and isolated behind sender adapters.
- Approval requests are authenticated, expiring, replay-protected, and bound to the displayed operation.
- The phone is a capture, status, and approval client; the PC is the only inference and agent server.
- Speed is optimized only after correctness, confirmation, security, and privacy gates pass.
