namespace Optimus.Core.Pipeline;

using System.Collections.Concurrent;
using System.Diagnostics;

/// <summary>Names the observable stages of one voice turn.</summary>
public enum PipelineStage
{
    Listen,
    Understand,
    Plan,
    Execute,
    Observe,
    Narrate,
    Total,
    Stt = Listen,
    Inference = Plan,
    ToolExecution = Execute,
    Tts = Narrate
}

public sealed record StageLatency(PipelineStage Stage, long ElapsedMilliseconds, bool ExceededThreshold);

public sealed record PipelineLatencySnapshot(
    IReadOnlyDictionary<PipelineStage, StageLatency> Stages,
    long TotalMilliseconds,
    bool MeetsTarget)
{
    public long GetMilliseconds(PipelineStage stage) =>
        Stages.TryGetValue(stage, out StageLatency? value) ? value.ElapsedMilliseconds : 0;
}

/// <summary>Small, thread-safe latency collector for the voice pipeline.</summary>
public sealed class LatencyTracker
{
    public const long EndToEndTargetMilliseconds = 2_000;

    private readonly ConcurrentDictionary<PipelineStage, StageLatency> _last = new();
    private readonly IReadOnlyDictionary<PipelineStage, long> _thresholds;
    private readonly Action<string>? _log;

    public LatencyTracker(
        IReadOnlyDictionary<PipelineStage, long>? thresholds = null,
        Action<string>? log = null)
    {
        _thresholds = thresholds ?? new Dictionary<PipelineStage, long>
        {
            [PipelineStage.Listen] = 500,
            [PipelineStage.Understand] = 500,
            [PipelineStage.Plan] = 1_000,
            [PipelineStage.Execute] = 1_000,
            [PipelineStage.Observe] = 1_000,
            [PipelineStage.Narrate] = 500,
            [PipelineStage.Total] = EndToEndTargetMilliseconds
        };
        _log = log;
    }

    public event Action<StageLatency>? StageRecorded;

    public IReadOnlyDictionary<PipelineStage, long> Thresholds => _thresholds;

    public PipelineLatencySnapshot Snapshot
    {
        get
        {
            var stages = _last.ToDictionary(pair => pair.Key, pair => pair.Value);
            long total = stages.TryGetValue(PipelineStage.Total, out StageLatency? value)
                ? value.ElapsedMilliseconds
                : stages.Values.Sum(item => item.ElapsedMilliseconds);
            return new(stages, total, total <= EndToEndTargetMilliseconds);
        }
    }

    public IDisposable Measure(PipelineStage stage) => new Measurement(this, stage);

    public StageLatency Record(PipelineStage stage, long elapsedMilliseconds)
    {
        long elapsed = Math.Max(0, elapsedMilliseconds);
        long threshold = _thresholds.TryGetValue(stage, out long configured) ? configured : long.MaxValue;
        var result = new StageLatency(stage, elapsed, elapsed > threshold);
        _last[stage] = result;
        if (result.ExceededThreshold)
            _log?.Invoke($"[Pipeline] {stage} took {elapsed} ms (target {threshold} ms).");
        StageRecorded?.Invoke(result);
        return result;
    }

    /// <summary>Starts a clean measurement window for the next turn.</summary>
    public void Reset() => _last.Clear();

    private sealed class Measurement : IDisposable
    {
        private readonly LatencyTracker _owner;
        private readonly PipelineStage _stage;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _stopped;

        public Measurement(LatencyTracker owner, PipelineStage stage) { _owner = owner; _stage = stage; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
                _owner.Record(_stage, _clock.ElapsedMilliseconds);
        }
    }
}
