namespace Optimus.Core.Narration;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

/// <summary>
/// Thread-safe narration scheduler that ingests observed agent events, applies mode
/// and tool/skill filtering, collapses repetitive logs and code blocks, incrementally
/// segments visible text into complete speakable phrases, and enforces strict ordering
/// and run-generation isolation.
/// </summary>
public sealed class NarrationScheduler : INarrationScheduler
{
    private readonly object _syncLock = new();
    private readonly Channel<SpeakableItem> _channel;
    private readonly IncrementalSentenceSegmenter _segmenter = new();
    private readonly HashSet<string> _retiredRunIds = new(StringComparer.Ordinal);

    private NarrationOptions _options = new();
    private string? _currentRunId;
    private long _sequenceCounter;

    /// <summary>
    /// Initializes a new instance of the <see cref="NarrationScheduler"/> class.
    /// </summary>
    /// <param name="options">Initial narration options.</param>
    public NarrationScheduler(NarrationOptions? options = null)
    {
        _options = options ?? new NarrationOptions();
        _channel = Channel.CreateUnbounded<SpeakableItem>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false
        });
    }

    /// <inheritdoc/>
    public NarrationOptions Options
    {
        get
        {
            lock (_syncLock)
            {
                return _options;
            }
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_syncLock)
            {
                _options = value;
            }
        }
    }

    /// <inheritdoc/>
    public int QueuedCount => _channel.Reader.Count;

    /// <inheritdoc/>
    public string? CurrentRunId
    {
        get
        {
            lock (_syncLock)
            {
                return _currentRunId;
            }
        }
    }

    /// <inheritdoc/>
    public void Enqueue(NarrationEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        lock (_syncLock)
        {
            // If this event belongs to a previously retired run, ignore it
            if (_retiredRunIds.Contains(evt.RunId))
            {
                return;
            }

            // Handle generation transition
            if (_currentRunId == null)
            {
                _currentRunId = evt.RunId;
            }
            else if (!string.Equals(_currentRunId, evt.RunId, StringComparison.Ordinal))
            {
                // A newer run has started: retire previous run and discard stale queued items
                _retiredRunIds.Add(_currentRunId);
                _currentRunId = evt.RunId;
                DrainChannel();
                _segmenter.Reset();
            }

            ProcessEvent(evt);
        }
    }

    /// <inheritdoc/>
    public bool TryDequeue([NotNullWhen(true)] out SpeakableItem? item)
    {
        while (_channel.Reader.TryRead(out var candidate))
        {
            lock (_syncLock)
            {
                // Discard any item from an obsolete or retired run
                if (_retiredRunIds.Contains(candidate.RunId))
                {
                    continue;
                }

                item = candidate;
                return true;
            }
        }

        item = null;
        return false;
    }

    /// <inheritdoc/>
    public async ValueTask<SpeakableItem> DequeueAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var candidate = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            lock (_syncLock)
            {
                if (!_retiredRunIds.Contains(candidate.RunId))
                {
                    return candidate;
                }
            }
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SpeakableItem> GetSpeakableStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SpeakableItem item;
            try
            {
                item = await DequeueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            yield return item;
        }
    }

    /// <inheritdoc/>
    public void Cancel(string? runId = null)
    {
        lock (_syncLock)
        {
            if (runId != null)
            {
                _retiredRunIds.Add(runId);
                if (string.Equals(runId, _currentRunId, StringComparison.Ordinal))
                {
                    DrainChannel();
                    _segmenter.Reset();
                }
            }
            else
            {
                if (_currentRunId != null)
                {
                    _retiredRunIds.Add(_currentRunId);
                }
                DrainChannel();
                _segmenter.Reset();
            }
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        lock (_syncLock)
        {
            _retiredRunIds.Clear();
            _currentRunId = null;
            DrainChannel();
            _segmenter.Reset();
        }
    }

    private void ProcessEvent(NarrationEvent evt)
    {
        switch (evt.Type)
        {
            case NarrationEventType.Progress:
                // Concise mode suppresses ordinary progress noise; Comprehensive emits in order
                if (_options.Mode == NarrationMode.Comprehensive)
                {
                    string collapsed = CodeAndLogCollapser.Collapse(evt.Text);
                    var sentences = _segmenter.Ingest(collapsed, isFinal: !evt.IsStreamingFragment);
                    foreach (string sentence in sentences)
                    {
                        PublishItem(evt.RunId, evt.Type, sentence, evt.Timestamp);
                    }
                }
                break;

            case NarrationEventType.StatusTransition:
                // Emitted in both Concise and Comprehensive modes
                if (!string.IsNullOrWhiteSpace(evt.Text))
                {
                    PublishItem(evt.RunId, evt.Type, evt.Text.Trim(), evt.Timestamp);
                }
                break;

            case NarrationEventType.ToolCall:
            case NarrationEventType.SkillUse:
                // Emitted only if NarrateToolsAndSkills is enabled
                if (_options.NarrateToolsAndSkills)
                {
                    string speechText = FormatToolOrSkillInvocation(evt);
                    if (!string.IsNullOrWhiteSpace(speechText))
                    {
                        PublishItem(evt.RunId, evt.Type, speechText, evt.Timestamp);
                    }
                }
                break;

            case NarrationEventType.ToolResult:
            case NarrationEventType.SkillResult:
                // Emitted only if NarrateToolsAndSkills is enabled
                if (_options.NarrateToolsAndSkills)
                {
                    string collapsedResult = CodeAndLogCollapser.Collapse(evt.Text);
                    string speechText = FormatToolOrSkillResult(evt, collapsedResult);
                    if (!string.IsNullOrWhiteSpace(speechText))
                    {
                        PublishItem(evt.RunId, evt.Type, speechText, evt.Timestamp);
                    }
                }
                break;

            case NarrationEventType.AgentQuestion:
                // Emitted in both modes
                if (!string.IsNullOrWhiteSpace(evt.Text))
                {
                    string collapsedQuestion = CodeAndLogCollapser.Collapse(evt.Text);
                    var sentences = _segmenter.Ingest(collapsedQuestion, isFinal: !evt.IsStreamingFragment);
                    if (sentences.Count > 0)
                    {
                        foreach (string sentence in sentences)
                        {
                            PublishItem(evt.RunId, evt.Type, sentence, evt.Timestamp);
                        }
                    }
                    else if (!evt.IsStreamingFragment)
                    {
                        PublishItem(evt.RunId, evt.Type, collapsedQuestion.Trim(), evt.Timestamp);
                    }
                }
                break;

            case NarrationEventType.FinalResponse:
                // Emitted in both modes; finalize streaming segmentation
                if (!string.IsNullOrWhiteSpace(evt.Text))
                {
                    string collapsedResponse = CodeAndLogCollapser.Collapse(evt.Text);
                    var sentences = _segmenter.Ingest(collapsedResponse, isFinal: true);
                    foreach (string sentence in sentences)
                    {
                        PublishItem(evt.RunId, evt.Type, sentence, evt.Timestamp);
                    }
                }
                break;

            case NarrationEventType.Error:
                // Emitted in both modes
                if (!string.IsNullOrWhiteSpace(evt.Text))
                {
                    PublishItem(evt.RunId, evt.Type, evt.Text.Trim(), evt.Timestamp);
                }
                break;
        }
    }

    private static string FormatToolOrSkillInvocation(NarrationEvent evt)
    {
        string label = evt.Type == NarrationEventType.SkillUse ? "Skill" : "Tool";
        string name = evt.Name?.Trim() ?? string.Empty;
        string text = evt.Text.Trim();

        if (name.Length > 0)
        {
            if (text.Length > 0 && !text.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return $"{label} {name}: {text}";
            }
            if (text.Length > 0)
            {
                return text;
            }
            return $"{label}: {name}";
        }

        return text;
    }

    private static string FormatToolOrSkillResult(NarrationEvent evt, string collapsedResult)
    {
        string trimmed = collapsedResult.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        string prefix = evt.Type == NarrationEventType.SkillResult ? "Skill result: " : "Tool result: ";
        if (trimmed.StartsWith("tool result", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("skill result", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            return trimmed;
        }

        return prefix + trimmed;
    }

    private void PublishItem(string runId, NarrationEventType sourceType, string text, DateTimeOffset timestamp)
    {
        long seq = Interlocked.Increment(ref _sequenceCounter);
        var item = new SpeakableItem(runId, sourceType, text, seq, timestamp);
        _channel.Writer.TryWrite(item);
    }

    private void DrainChannel()
    {
        while (_channel.Reader.TryRead(out _))
        {
            // Drain all items
        }
    }
}
