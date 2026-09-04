namespace Optimus.Core.Narration;

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Schedules and filters observed agent events into an ordered stream of speakable items for TTS.
/// </summary>
public interface INarrationScheduler
{
    /// <summary>
    /// Gets or sets the narration configuration options.
    /// </summary>
    NarrationOptions Options { get; set; }

    /// <summary>
    /// Gets the number of speakable items currently waiting in the queue.
    /// </summary>
    int QueuedCount { get; }

    /// <summary>
    /// Gets the active run identifier, or null if no run is currently active.
    /// </summary>
    string? CurrentRunId { get; }

    /// <summary>
    /// Enqueues an observed narration event for processing and scheduling.
    /// </summary>
    /// <param name="evt">The narration event to process.</param>
    void Enqueue(NarrationEvent evt);

    /// <summary>
    /// Attempts to synchronously dequeue the next speakable item.
    /// </summary>
    /// <param name="item">When this method returns true, contains the next speakable item.</param>
    /// <returns>True if an item was dequeued; otherwise, false.</returns>
    bool TryDequeue([NotNullWhen(true)] out SpeakableItem? item);

    /// <summary>
    /// Asynchronously dequeues the next speakable item, waiting if none are currently available.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>The next speakable item.</returns>
    ValueTask<SpeakableItem> DequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an asynchronous stream of speakable items in strict arrival order.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel reading.</param>
    /// <returns>An async enumerable of speakable items.</returns>
    IAsyncEnumerable<SpeakableItem> GetSpeakableStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards all queued items and resets streaming state for the specified run or all runs.
    /// </summary>
    /// <param name="runId">The run identifier to cancel, or null to cancel all current runs.</param>
    void Cancel(string? runId = null);

    /// <summary>
    /// Resets all state, discarding all queues and setting current run to null.
    /// </summary>
    void Reset();
}
