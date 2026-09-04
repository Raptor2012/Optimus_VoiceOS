# ADR-001: Process boundaries, technology ownership, and dependency direction

- Status: Accepted
- Date: 2026-09-04
- Owner: Claude Opus 5
- Related tasks: T001, T002, T003, T004, T005, T007

## Context

`PROJECT_PLAN.md` fixes the technology set: .NET 8 WPF for the desktop surface, ASP.NET Core/Kestrel for the local service, isolated local inference runners, a Kotlin/Jetpack Compose Android client, one pinned-TLS WebSocket between phone and PC, and replaceable sender adapters for Claude Code and Codex.

Four forces decide the process layout:

1. **Latency.** The warm path must reach a cleaned visible draft in under 500 ms p95 (`docs/performance/LATENCY_BUDGET.md`). Every process hop costs measurable time, so hops must be few and each one must be justified.
2. **Crash isolation.** Local inference runners are native CUDA workloads that will crash. A runner crash must not take down capture, the UI, or an active agent session.
3. **Privacy.** Routine audio must never be persisted. The fewer components that touch PCM, the smaller the surface that has to guarantee that.
4. **Uniformity.** The phone and the desktop widget show the same states and take the same actions. Two independent orchestration paths would produce two divergent confirmation implementations, which is exactly where confirmation-integrity bugs live.

Windows session constraints also apply: GPU access, per-user DPAPI, WASAPI capture, and provider CLIs all require an interactive user session. A Windows service in session 0 cannot serve this product.

## Decision

### 1. Four process roles on the PC, all inside the interactive user session

| Process | Executable | Role | Lifetime |
| --- | --- | --- | --- |
| Shell | `Optimus.Shell.exe` | .NET 8 WPF tray process, floating widget, global hotkey, WASAPI capture and playback, settings UI. **UI and device I/O only.** | Starts at logon. Restartable at any time. |
| Core | `Optimus.Service.exe` | ASP.NET Core/Kestrel host: device endpoint, orchestration state machine, confirmation authority, session store, runner supervisor, provider adapters, GPU scheduler. | Started by Shell if not already running. Survives Shell restarts. Stops on tray Exit or logoff. |
| Runner | `optimus-runner-asr`, `optimus-runner-cleanup`, `optimus-runner-tts` | One process per warm inference model. Owns model weights, GPU context, and decode loops. | Child of Core. Supervised and restartable. |
| Setup runner | `optimus-runner-voicedesign` | Qwen3-TTS 1.7B VoiceDesign. Setup-time only. | Transient child of Core; never resident. |

Provider CLIs (Claude Code, Codex) run as child processes of Core, spawned and supervised by their sender adapter.

Optimus is **not** a Windows service. Rationale: session-0 isolation blocks GPU scheduling for interactive workloads, WASAPI device enumeration, per-user DPAPI, and provider CLI credential stores. Core therefore runs as a user-session background process with no window.

### 2. The desktop UI is a protocol client, not a co-orchestrator

The Shell connects to Core over the **same** WebSocket endpoint and the **same** protocol version the phone uses (`docs/specs/WIRE_PROTOCOL.md`), over `127.0.0.1`, with the same TLS certificate and a local capability token (ADR-003 section 6).

Consequences that make this the decisive choice:

- One implementation of the state machine, confirmation, session, queue, steer, and approval semantics. There is no desktop shortcut path that can bypass a confirmation gate.
- The Shell holds no authority. It cannot construct a `SendAction` that Core did not first authorize with a `ConfirmationRequest`.
- Headless end-to-end tests drive Core with a synthetic device client and require no UI automation.
- Desktop and phone reach feature parity by construction.

The measured cost is one loopback WebSocket hop in each direction. The latency budget allocates 20 ms for control delivery and 10 ms for the audio handoff, both of which are met by loopback TLS 1.3 over an established connection.

### 3. Ownership matrix

| Concern | Owner | Explicitly not owned by |
| --- | --- | --- |
| Global hotkey registration and hook | Shell | Core |
| WASAPI capture, device selection, VAD, frame packetization | Shell | Core, runners |
| Playback of TTS chunks | The device that originated the prompt (Shell or Android) | Core |
| Widget state rendering | Shell | Core |
| Orchestration state machine (idle to completed/error) | Core | Shell, Android |
| Transcript, cleaned draft, glossary application | Core | Shell |
| Confirmation issuance, validation, single-use consumption | Core | every other component |
| Destination discovery and the active destination record | Core | adapters (they only enumerate), Shell |
| Agent session store, queue, steer arbitration | Core | adapters |
| Approval issuance, expiry, replay protection | Core | adapters, devices |
| Runner process supervision, restart, health, GPU lease | Core | Shell |
| Model residency and warmup | Core | runners (a runner loads one model and never chooses) |
| Provider CLI process supervision | The owning sender adapter, inside Core | Core orchestrator |
| Device records, pairing, revocation | Core | Shell (renders only) |
| Secret storage through DPAPI | Core | Shell |
| Text event history and retention enforcement | Core | Shell |

**Audio residency rule.** PCM exists in exactly three places: the Shell or Android capture buffer, the Core relay buffer, and the ASR runner decode buffer. All three are pooled, zeroed on release, and never written to disk. No other component may accept PCM. This is the enforcement point for the "routine audio is not retained" invariant (ADR-004 section 7).

### 4. Restart and recovery responsibilities

- **Shell crash.** Core keeps running. Active agent sessions, queued prompts, and phone connections are unaffected. On restart the Shell reconnects, sends `Resume`, and re-renders state from `ServiceStatus` and `SessionList`. Any in-flight capture is abandoned; a partially captured utterance is discarded, never sent.
- **Core crash.** The Shell detects socket closure, shows the `error` state, and relaunches Core after 1 s (maximum 5 attempts in 5 minutes, then manual). Agent session *metadata* is persisted, so `ResumeSession` can re-attach to provider sessions. In-flight confirmations are lost by design: the confirmation key `K_conf` is memory-only and per-Core-start, so every outstanding confirmation becomes invalid rather than replayable.
- **Runner crash.** The Core supervisor restarts it (ADR-004 section 6). Core degrades the affected feature and reports it; it never silently retries a user-visible action.
- **Provider CLI crash.** The owning adapter marks the session `Failed`, emits an `AgentEvent` of kind `Failed`, and offers `ResumeSession`. Core never auto-resends a confirmed prompt.

### 5. Assemblies and dependency direction (.NET)

```
                    Optimus.Contracts        (no project references)
                      ^      ^      ^
                      |      |      |
        Optimus.Inference   Optimus.Providers   Optimus.Client
                      ^      ^                        ^
                      |      |                        |
                     Optimus.Core               Optimus.Shell
                          ^
                          |
                    Optimus.Service      (composition root)
```

| Assembly | Contains | May reference |
| --- | --- | --- |
| `Optimus.Contracts` | Protocol DTOs, enums, error codes, version constants, canonical serialization rules | nothing |
| `Optimus.Inference` | `IInferenceRunner` contracts, runner supervisor, named-pipe transport, GPU lease | `Optimus.Contracts` |
| `Optimus.Providers` | `ISenderAdapter`, Claude Code adapter, Codex adapter | `Optimus.Contracts` |
| `Optimus.Core` | Orchestration state machine, confirmation authority, session store, device registry, history, policy | `Optimus.Contracts`, `Optimus.Inference`, `Optimus.Providers` |
| `Optimus.Client` | Device-side WebSocket client, reconnect, resume | `Optimus.Contracts` |
| `Optimus.Shell` | WPF tray, widget, hotkey, WASAPI capture and playback | `Optimus.Contracts`, `Optimus.Client` |
| `Optimus.Service` | Kestrel host, DI composition, endpoint mapping | `Optimus.Contracts`, `Optimus.Core`, `Optimus.Inference`, `Optimus.Providers` |

Hard rules, enforced by an automated architecture test delivered in T002:

1. `Optimus.Contracts` has zero project references.
2. No assembly references `Optimus.Shell` or `Optimus.Service`.
3. `Optimus.Inference` and `Optimus.Providers` never reference each other and never reference `Optimus.Core`.
4. `Optimus.Shell` never references `Optimus.Core`, `Optimus.Inference`, or `Optimus.Providers`.
5. `Optimus.Client` references nothing but `Optimus.Contracts`.

### 6. Android module direction

```
                     :core:protocol      (Kotlin only, no Android framework types)
                    ^       ^        ^
                    |       |        |
        :core:transport  :core:audio  :core:security
                    ^       ^        ^
                    |       |        |
        :feature:pairing  :feature:talk  :feature:sessions
                             ^
                             |
                            :app
```

`:core:protocol` declares no project dependency. No `:feature:*` module depends on another `:feature:*` module. `:app` is the only module that wires features together.

### 7. Inter-process transports

| Link | Transport | Rationale |
| --- | --- | --- |
| Android to Core | Pinned-TLS WebSocket, `wss://<host>:<port>/v1/device` | Fixed by `PROJECT_PLAN.md`; one connection carries control JSON and binary PCM |
| Shell to Core | Same endpoint on `127.0.0.1` | One protocol, one state machine, no privileged local path |
| Core to Runner | Duplex named pipe, per-runner random name, DACL restricted to the current user SID | No local TCP port to attack or collide with; lowest-overhead Windows local IPC with byte-stream framing |
| Adapter to provider CLI | Child-process stdio using the provider protocol | Isolated inside the adapter and replaceable |

Named pipes are chosen over local TCP because a TCP loopback listener is reachable by every process on the machine, whereas a pipe DACL restricts access to the owning user account. Shared memory was rejected: it buys under 3 ms on 20 ms audio frames and adds a lifetime-management failure mode.

## Alternatives considered

- **Single process hosting UI, Kestrel, and inference.** Lowest latency, but a CUDA fault kills the UI and the phone connection, and WPF dispatcher stalls would block the device endpoint. Rejected on crash isolation and responsiveness.
- **Core as a Windows service.** Rejected: session-0 isolation breaks GPU scheduling, audio device access, per-user DPAPI, and provider CLI credentials.
- **Shell as a privileged in-process co-orchestrator with a private API.** Rejected: it duplicates confirmation and session logic and creates a bypass path around the confirmation gate, directly threatening a product invariant.
- **One runner process hosting all models.** Rejected: one crash loses ASR, cleanup, and TTS at once, and the setup-time 1.7B voice model could not be isolated from the warm path.
- **gRPC over loopback for Core to Runner.** Rejected: HTTP/2 framing and a local TCP listener add both latency and an exposed local port for no benefit over a DACL-restricted named pipe.

## Consequences

- Benefits: one orchestration path, one confirmation authority, a testable headless Core, and blast-radius containment for CUDA faults.
- Costs: two extra hops on the warm path (budgeted at 30 ms total), a supervisor that must be correct, and a token-based local authentication step for the Shell.
- Risk: Core becomes a large component. Mitigated by the internal module boundaries above and by an architecture test that fails the build on a violated dependency rule.
- Operational: three or four Optimus processes appear in Task Manager. Tray diagnostics must present them so a user can distinguish a runner crash from an application crash.

## Verification

- T002 adds `Optimus.Architecture.Tests`, which parses every `.csproj` `ProjectReference` and fails on any violation of section 5 rules 1 to 5.
- T002 adds a Gradle check that fails if a `:feature:*` module declares a dependency on another `:feature:*` module, or if `:core:protocol` declares any project dependency.
- T004 acceptance requires the Shell to complete an end-to-end connect and resume against Core over loopback, with no code path that constructs protocol messages outside `Optimus.Contracts`.
- T027 recovery tests kill each process class and assert the recovery behavior in section 4.

## Related documents

- `docs/ARCHITECTURE.md`
- `docs/adr/ADR-002-device-protocol-and-compatibility.md`
- `docs/adr/ADR-003-pairing-authentication-and-secrets.md`
- `docs/adr/ADR-004-inference-lifecycle-and-gpu-scheduling.md`
- `docs/adr/ADR-005-confirmation-sessions-and-approvals.md`
