namespace Optimus.Shell.ViewModels;

using System.Collections.ObjectModel;
using System.Windows.Threading;
using Optimus.Core.ExecutionPolicy;

/// <summary>One agent's remaining five-hour execution window.</summary>
public sealed record AgentCapacity(string Name, double CapacityPercent, bool IsLow);

/// <summary>Supplies the expanded companion view with a lightweight, local quota snapshot.</summary>
public sealed class AgentCapacityViewModel : IDisposable
{
    public const double CapacityWindowHours = 5d;
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _timer;
    private readonly Func<IReadOnlyDictionary<ExecutionProvider, ProviderCapacity>>? _quotaProvider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<ExecutionProvider, DateTimeOffset> _windowStarted = new();
    private bool _disposed;

    public AgentCapacityViewModel(
        Func<IReadOnlyDictionary<ExecutionProvider, ProviderCapacity>>? quotaProvider = null,
        Func<DateTimeOffset>? clock = null,
        bool startPolling = true)
    {
        _quotaProvider = quotaProvider;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        DateTimeOffset now = _clock();
        foreach (ExecutionProvider provider in Providers.Keys)
            _windowStarted[provider] = now;

        Agents = new ObservableCollection<AgentCapacity>();
        Refresh();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = PollInterval
        };
        _timer.Tick += OnTimerTick;
        if (startPolling) _timer.Start();
    }

    public ObservableCollection<AgentCapacity> Agents { get; }

    /// <summary>Recomputes all bars immediately; useful after a quota response or in tests.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        IReadOnlyDictionary<ExecutionProvider, ProviderCapacity>? quotas = null;
        try { quotas = _quotaProvider?.Invoke(); } catch { /* quota is best-effort UI data */ }

        var values = new List<AgentCapacity>(Providers.Count);
        foreach ((ExecutionProvider provider, string name) in Providers)
        {
            double percent = CalculateCapacityPercent(_clock() - _windowStarted[provider]);
            if (quotas != null && quotas.TryGetValue(provider, out ProviderCapacity? quota) && quota.RemainingPercent.HasValue)
                percent = quota.RemainingPercent.Value;

            percent = Math.Clamp(percent, 0, 100);
            values.Add(new AgentCapacity(name, percent, percent < 20));
        }

        Agents.Clear();
        foreach (AgentCapacity value in values) Agents.Add(value);
    }

    public static double CalculateCapacityPercent(TimeSpan elapsed)
    {
        double remaining = (TimeSpan.FromHours(CapacityWindowHours) - elapsed).TotalHours / CapacityWindowHours * 100d;
        return Math.Clamp(remaining, 0d, 100d);
    }

    /// <summary>Sets an elapsed window for one provider and refreshes the matching bar.</summary>
    public void SetElapsed(ExecutionProvider provider, TimeSpan elapsed)
    {
        _windowStarted[provider] = _clock() - elapsed;
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }

    private void OnTimerTick(object? sender, EventArgs e) => Refresh();

    private static readonly IReadOnlyDictionary<ExecutionProvider, string> Providers =
        new Dictionary<ExecutionProvider, string>
        {
            [ExecutionProvider.Codex] = "Codex / Luna",
            [ExecutionProvider.Antigravity] = "Antigravity / Gemini",
            [ExecutionProvider.Claude] = "Opus orchestrator"
        };
}
