# Provider-neutral sender-adapter specification

- Status: Accepted
- Owner: Claude Opus 5
- Governing decisions: `docs/adr/ADR-001-process-and-subsystem-boundaries.md`, `docs/adr/ADR-005-confirmation-sessions-and-approvals.md`
- Implemented by: T016 (contract and Claude Code adapter), T017 (Codex adapter)

The adapter layer is the only place that knows what a coding agent is. Core knows sessions, confirmations, destinations, and events; it does not know about CLIs, transcripts of provider protocols, or vendor session identifiers. `PROJECT_PLAN.md` requires that agent cloud behavior stays unchanged and isolated behind these adapters.

## 1. Assembly boundary

`Optimus.Providers` references only `Optimus.Contracts`. It must not reference `Optimus.Core`, `Optimus.Inference`, `Optimus.Service`, or `Optimus.Shell`. Adapters therefore cannot reach the confirmation authority, the GPU scheduler, the device registry, or any UI.

Adapters never receive: PCM audio, the confirmation key `K_conf`, device signing keys, unconfirmed prompt text, or the raw device connection.

## 2. Contract

```csharp
namespace Optimus.Providers;

public interface ISenderAdapter : IAsyncDisposable
{
    string ProviderId { get; }                 // "claude-code" | "codex"
    ProviderCapabilities Capabilities { get; }

    Task<IReadOnlyList<Destination>> DiscoverDestinationsAsync(
        DiscoveryRequest request, CancellationToken ct);

    Task<AgentSessionHandle> CreateSessionAsync(
        CreateSessionRequest request, CancellationToken ct);

    Task<AgentSessionHandle> ResumeSessionAsync(
        ResumeSessionRequest request, CancellationToken ct);

    Task<SendOutcome> SendConfirmedPromptAsync(
        ConfirmedPrompt prompt, CancellationToken ct);

    Task<SendOutcome> QueuePromptAsync(
        ConfirmedPrompt prompt, CancellationToken ct);

    Task<SteerOutcome> SteerActiveSessionAsync(
        ConfirmedPrompt steer, CancellationToken ct);

    Task<ApprovalOutcome> RespondToApprovalAsync(
        ApprovalDecision decision, CancellationToken ct);

    Task CancelAsync(CancelTarget target, CancellationToken ct);

    IAsyncEnumerable<ProviderEvent> SubscribeToEventsAsync(
        string providerSessionRef, CancellationToken ct);
}

public sealed record ProviderCapabilities(
    bool SupportsQueue,          // false means Core emulates the queue
    bool SupportsSteer,          // false means Steer is not offered for this provider
    bool SupportsResume,
    bool SupportsCancel,
    bool EmitsApprovals,
    bool EmitsQuestions,
    int MaxPromptCharacters);
```

`SendConfirmedPromptAsync`, `QueuePromptAsync`, and `SteerActiveSessionAsync` all take a `ConfirmedPrompt`. There is no overload accepting a bare string. A `ConfirmedPrompt` cannot be constructed outside `Optimus.Contracts`, and its constructor requires a non-null `ConfirmationReceipt`; adapters **must** throw `ArgumentException` if `Receipt` is null or its `PromptHash` does not match `SHA-256(NFC(Text))`. This makes the confirmation invariant a compile-time and construction-time property rather than a review convention.

### Capability fallbacks

- `SupportsQueue == false`: Core holds the queue and dispatches the next item on the `TurnCompleted` event. The user-visible behavior is identical.
- `SupportsSteer == false`: the busy-session dialog offers only Queue and Cancel. Core never converts a steer into a send.
- `SupportsResume == false`: sessions from a previous Core run are listed as `Closed` with reason `ProviderCannotResume`.
- `MaxPromptCharacters` exceeded: `SendOutcome.Status = PromptTooLong`. Core surfaces it before confirmation where possible, by validating at `RequestConfirmation` time.

## 3. Types

```csharp
public sealed record ConfirmedPrompt(
    string OperationId,          // ULID, adapter idempotency key
    string ProviderSessionRef,
    string DestinationId,
    string Text,
    PromptMode Mode,             // Send | Queue | Steer
    string OriginDeviceId,
    ConfirmationReceipt Receipt);

public sealed record ConfirmationReceipt(
    string ConfirmationId, string PromptHash, DateTimeOffset ValidatedAtUtc);

public sealed record DiscoveryRequest(bool ForceRefresh, TimeSpan Budget);

public sealed record CreateSessionRequest(
    string OperationId, string DestinationId, string? Title);

public sealed record ResumeSessionRequest(
    string OperationId, string DestinationId, string ProviderSessionRef);

public sealed record AgentSessionHandle(
    string ProviderSessionRef, ProviderSessionState State, DateTimeOffset StartedAtUtc);

public sealed record SendOutcome(
    SendStatus Status, string? ProviderTurnRef, int? QueuePosition, ProviderError? Error);

public sealed record SteerOutcome(SteerStatus Status, ProviderError? Error);

public sealed record ApprovalDecision(
    string OperationId, string ProviderSessionRef, string ProviderApprovalRef,
    bool Approve, string OperationHash);

public sealed record ApprovalOutcome(ApprovalStatus Status, ProviderError? Error);

public sealed record CancelTarget(
    string OperationId, string ProviderSessionRef, CancelScope Scope); // Turn | Steer | All

public sealed record ProviderEvent(
    string ProviderSessionRef,
    ProviderEventKind Kind,
    string RawText,                  // unsanitized; Core sanitizes
    string? ProviderQuestionRef,
    IReadOnlyList<string>? Options,
    ProviderApprovalPayload? Approval,
    DateTimeOffset OccurredAtUtc);

public sealed record ProviderApprovalPayload(
    string ProviderApprovalRef,
    ApprovalOperationClass OperationClass,
    string Title,
    string Detail,
    string CanonicalOperation);      // the exact bytes hashed into operationHash
```

Enumerations:

- `ProviderSessionState`: `Starting`, `Idle`, `Busy`, `AwaitingAnswer`, `AwaitingApproval`, `Failed`, `Closed`.
- `SendStatus`: `Accepted`, `Queued`, `Rejected`, `SessionBusy`, `SessionNotFound`, `PromptTooLong`, `ProviderUnavailable`, `Timeout`, `Cancelled`.
- `SteerStatus`: `Delivered`, `Missed`, `NotSupported`, `SessionNotFound`, `Timeout`, `Cancelled`.
- `ApprovalStatus`: `Applied`, `AlreadyResolved`, `OperationChanged`, `NotFound`, `Timeout`.
- `ProviderEventKind`: `SessionStarted`, `PlanningStarted`, `ToolStarted`, `FileEdited`, `TestsPassed`, `TestsFailed`, `Question`, `ApprovalRequested`, `Output`, `TurnCompleted`, `Failed`, `SessionClosed`.
- `ApprovalOperationClass`: section 7.

`ProviderError` is `(ProviderErrorCode Code, string SafeMessage, bool Retryable, TimeSpan? RetryAfter)` with `ProviderErrorCode` in `{ Unavailable, AuthRequired, RateLimited, ProtocolError, SessionNotFound, SessionBusy, Timeout, Cancelled, Internal }`. `SafeMessage` is adapter-authored and must not embed provider output verbatim.

## 4. Lifecycle, timeouts, retries, idempotency

| Method | Default timeout | Core retries | Idempotency |
| --- | --- | --- | --- |
| `DiscoverDestinationsAsync` | 3 s | 3 attempts, 250 ms / 1 s / 3 s backoff | Pure read |
| `CreateSessionAsync` | 15 s | none | `OperationId` |
| `ResumeSessionAsync` | 10 s | 2 attempts | `OperationId` |
| `SendConfirmedPromptAsync` | 10 s to acceptance | **never** | `OperationId` |
| `QueuePromptAsync` | 10 s | **never** | `OperationId` |
| `SteerActiveSessionAsync` | 5 s | **never** | `OperationId` |
| `RespondToApprovalAsync` | 10 s | **never** | `OperationId` |
| `CancelAsync` | 5 s | 2 attempts | `OperationId` |
| `SubscribeToEventsAsync` | none (stream) | reconnects internally | n/a |

Rules:

1. **Send is never retried automatically.** A timeout on a send is reported to the user as an uncertain outcome with an explicit "check the session" affordance. A duplicate prompt is worse than a failed one.
2. Every adapter maintains an `OperationId` dedupe cache of at least 256 entries for 15 minutes and returns the original outcome for a repeat.
3. Every method honours its `CancellationToken` and must return or throw `OperationCanceledException` within 500 ms of cancellation.
4. Timeouts are enforced by Core with a linked token; adapters must not implement longer internal waits.
5. `SubscribeToEventsAsync` yields until the token is cancelled or the session closes. It must not throw for transient provider issues; it emits a `Failed` event and continues or completes.
6. Adapters expose no method that mutates a session without an `OperationId`.

## 5. Process supervision inside an adapter

An adapter that spawns a provider CLI:

- Creates it in a Windows job object owned by Core so it cannot outlive the service.
- Redirects stdin, stdout, and stderr; never allocates a console window.
- Applies a startup timeout of 20 s to first readiness, then `ProviderUnavailable`.
- Detects exit and emits `Failed`, then `SessionClosed`. It never auto-restarts a turn.
- Restarts the CLI for a *new* session on demand only, with backoff 1 s, 2 s, 4 s, capped at 3 attempts per minute.
- Writes no prompt or output text to any file it creates.
- Passes credentials through the provider's own mechanism, read from the DPAPI store at spawn time and never placed on the command line, where other processes could read it from the process list.

## 6. Destination discovery

- Destinations are enumerated from provider-specific evidence (installed CLI, configured workspaces, recently used repositories). Discovery is a read-only operation and must not start a session.
- `destinationId` must be stable across restarts for the same provider and workspace, computed as `SHA-256(providerId || canonicalWorkspacePath)` truncated to 128 bits, base64url. Stability matters because a confirmation binds it.
- `label` is human-readable and is what the user sees in the confirmation card. It must be unambiguous when two workspaces share a folder name; include the parent segment.
- A discovery failure yields an empty list plus a `ProviderError`; it never yields a stale cached list marked `Available`.

## 7. Approval risk classification

`riskLevel` in `ApprovalRequest` is derived by the adapter from the operation class, never from provider free text (ADR-005 section 8).

| `ApprovalOperationClass` | Examples | `riskLevel` |
| --- | --- | --- |
| `ReadOutsideWorkspace` | reading a file above the workspace root | `Elevated` |
| `WriteInsideWorkspace` | editing a tracked file | `Low` |
| `WriteOutsideWorkspace` | writing anywhere outside the workspace root | `Consequential` |
| `ExecuteCommand` | running a shell command | `Consequential` |
| `InstallDependency` | package manager install | `Consequential` |
| `NetworkWrite` | POST, PUT, upload, publish | `Consequential` |
| `CredentialAccess` | reading tokens, keys, credential stores | `Consequential` |
| `VersionControlWrite` | commit, push, branch delete, force operations | `Consequential` |
| `Destructive` | delete, overwrite, reset | `Consequential` |
| `Other` | anything an adapter cannot classify | `Consequential` |

`Other` maps to `Consequential` deliberately: an unclassifiable operation is treated as the most dangerous, not the least.

`CanonicalOperation` is the exact string the adapter believes describes the operation, in canonical form per `docs/specs/WIRE_PROTOCOL.md` section 3. Core hashes it into `operationHash`. Before applying an approval, Core calls the adapter to confirm the pending operation still hashes to the same value (`APPROVAL_OPERATION_CHANGED` otherwise), which closes the window where a provider swaps the operation between display and approval.

## 8. Output handling

- Adapters return `RawText` unmodified. Sanitization, control-character stripping, bidirectional-override removal, and length capping happen once, in Core, so the rules cannot drift per provider.
- Adapters must not interpret provider output as commands to Optimus. Specifically, an adapter must never parse provider output to set a destination, create a confirmation, approve an operation, or send a prompt.
- Adapters must not log `RawText`. Diagnostics log event kind, session ref, and byte length.

## 9. Event mapping

Each adapter documents its provider-to-`ProviderEventKind` mapping in a table inside its own source folder as `MAPPING.md`, and a unit test asserts every provider event type is either mapped or explicitly ignored. An unmapped, unignored event type fails the test rather than being silently dropped.

Core maps `ProviderEventKind` to spoken output:

| Kind | Spoken | Notes |
| --- | --- | --- |
| `PlanningStarted` | first occurrence per turn only | Repeats are visual only |
| `ToolStarted` | no | Visual only |
| `FileEdited` | no | Visual only |
| `TestsPassed` / `TestsFailed` | yes | |
| `Question` | yes | Interrupting |
| `ApprovalRequested` | yes | Interrupting |
| `Failed` | yes | Interrupting |
| `TurnCompleted` | yes, as a final summary | Coalesced |
| `Output` | no | Visual only |
| `SessionStarted` / `SessionClosed` | no | Visual only |

## 10. Adding a provider

A new provider requires:

1. A class implementing `ISenderAdapter` in `Optimus.Providers/<Provider>/`.
2. A `MAPPING.md` and its completeness test.
3. Registration in the Core composition root, which is the only file outside the provider folder that changes.
4. Conformance-suite compliance (section 11).

No change to Core orchestration, the wire protocol, or the UI is permitted as part of adding a provider. If one appears necessary, the work is an architecture change and belongs to Claude Opus 5.

## 11. Conformance suite

T016 delivers `Optimus.Providers.Conformance`, a shared xUnit theory suite executed against every adapter and against an in-memory `FakeAdapter`. It asserts:

- `SendConfirmedPromptAsync` throws when `Receipt` is null or its hash does not match `Text`.
- A repeated `OperationId` returns the original outcome and performs no second submission.
- Every method observes cancellation within 500 ms.
- Every method returns a typed `ProviderError` rather than throwing for provider-side failures.
- `DiscoverDestinationsAsync` produces stable `destinationId` values across two calls and across a process restart.
- Capability fallbacks behave as section 2 specifies.
- `Other` operation class maps to `Consequential`.
- Provider output containing control characters, bidirectional overrides, ANSI escapes, and instruction-like text causes no state transition and is never logged.

An adapter that does not pass the conformance suite cannot be registered; the composition root test enumerates registered adapters and fails if any is missing from the suite run.
