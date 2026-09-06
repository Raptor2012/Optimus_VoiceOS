namespace Optimus.Providers;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers.Windows;

/// <summary>Why a destination cannot currently receive a prompt.</summary>
public enum DestinationReadiness
{
    /// <summary>Bound to a live window and ready to send.</summary>
    Ready,

    /// <summary>The application is not running, or has no eligible window.</summary>
    NotRunning,

    /// <summary>Several candidate windows exist; the user must pick one.</summary>
    AmbiguousWindow,

    /// <summary>Candidates exist but none is bound yet.</summary>
    NotBound,

    /// <summary>The bound window has closed or been replaced.</summary>
    BoundWindowGone
}

/// <summary>Result of probing a destination, with the candidates that informed it.</summary>
public sealed record DestinationStatus(
    DestinationReadiness Readiness,
    IReadOnlyList<WindowCandidate> Candidates,
    WindowCandidate? Bound,
    string Detail)
{
    public bool CanSend => Readiness == DestinationReadiness.Ready;
}

/// <summary>Why a send did or did not happen. There is no partial success.</summary>
public enum SendStatus
{
    Sent,
    NotReady,
    FocusFailed,
    InputRejected,
    Cancelled,
    Failed
}

public sealed record SendResult(SendStatus Status, string Detail, long ElapsedMilliseconds)
{
    public bool Succeeded => Status == SendStatus.Sent;
}

/// <summary>
/// The exact text the user saw and approved.
/// </summary>
/// <remarks>
/// Captured once at confirmation and never re-read from the editable draft, so an edit landing
/// mid-send cannot change what is transmitted. The product invariant is that what was on screen
/// is what is sent.
/// </remarks>
public sealed record ConfirmedDraft
{
    public ConfirmedDraft(string text, string destinationId)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A confirmed draft cannot be empty.", nameof(text));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);

        Text = text;
        DestinationId = destinationId;
        ConfirmedAtUtc = DateTimeOffset.UtcNow;
    }

    public string Text { get; }

    public string DestinationId { get; }

    public DateTimeOffset ConfirmedAtUtc { get; }
}

/// <summary>
/// One place a confirmed prompt can be sent. Deliberately tiny: probe, bind, send.
/// </summary>
public interface IDestinationAdapter
{
    string DestinationId { get; }

    string DisplayName { get; }

    /// <summary>The process name this destination is configured to target.</summary>
    string ProcessName { get; }

    WindowCandidate? BoundWindow { get; }

    /// <summary>Inspects the live window list without changing any binding.</summary>
    DestinationStatus Probe();

    /// <summary>Binds to one explicitly chosen window.</summary>
    void Bind(WindowCandidate candidate);

    void Unbind();

    /// <summary>
    /// Focuses the bound window and types the draft. Returns <see cref="SendStatus.Sent"/> only
    /// after every step actually succeeded.
    /// </summary>
    Task<SendResult> SendAsync(ConfirmedDraft draft, CancellationToken cancellationToken = default);
}

/// <summary>
/// Observes agent activity and emits visible updates for narration.
/// </summary>
public interface IAgentObserver
{
    int CapturedNodeCount { get; }

    IReadOnlyList<VisibleAgentUpdate> Poll();

    IAsyncEnumerable<VisibleAgentUpdate> ObserveAsync(TimeSpan pollInterval, CancellationToken cancellationToken = default);
}

/// <summary>
/// A destination adapter that can be observed for visible agent narration.
/// </summary>
public interface IObservableDestinationAdapter : IDestinationAdapter
{
    IAgentObserver CreateObserver();
}
