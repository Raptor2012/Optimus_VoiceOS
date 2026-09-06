using System.Runtime.CompilerServices;
using System.Text.Json;
using Optimus.Core.Conversation;
using Optimus.Core.Handoff;
using Optimus.Core.Memory;
using Optimus.Core.Pipeline;
using Optimus.Inference;
using Optimus.Providers;
using Xunit;

namespace Optimus.Core.Tests.Pipeline;

public sealed class PipelineOrchestratorTests
{
    private static readonly string[] CodexAliases = { "codex" };
    private static readonly string[] ExpectedEvents = { "stt", "model", "execute", "observe", "tts" };
    private static readonly ContextResolver CodexResolver = new(new[]
    {
        new ContextTarget("codex", "Codex", CodexAliases)
    });

    [Fact]
    public async Task ProcessAsync_RunsListenThroughNarrateInOrder()
    {
        var events = new List<string>();
        var stt = new FakeStt(events);
        var model = new FakeModel(events, "{\"name\":\"desktop.execute\",\"arguments\":{\"targetId\":\"codex\",\"objective\":\"review the diff\"}}");
        var executor = new FakeExecutor(events);
        var observer = new FakeObserver(events);
        var tts = new FakeTts(events);
        var coordinator = new ConversationCoordinator(CodexResolver);
        var pipeline = new PipelineOrchestrator(stt, coordinator, model, executor, observer, tts);

        PipelineResult result = await pipeline.ProcessAsync(new PipelineRequest(new byte[] { 1, 2 }, OriginDevice: "pc"));

        Assert.True(result.Succeeded, result.Response);
        Assert.Equal("The diff looks good.", result.Response);
        Assert.Equal(ExpectedEvents, events);
        Assert.True(result.Latency.Stages.ContainsKey(PipelineStage.Listen));
        Assert.True(result.Latency.Stages.ContainsKey(PipelineStage.Total));
    }

    [Fact]
    public async Task ProcessAsync_LowConfidenceAsksForClarificationWithoutCallingModel()
    {
        var stt = new FakeStt(new List<string>(), confidence: 0.1);
        var model = new FakeModel(new List<string>(), "{}");
        var tts = new FakeTts(new List<string>());
        var pipeline = new PipelineOrchestrator(
            stt,
            new ConversationCoordinator(),
            model,
            new FakeExecutor(new List<string>()),
            new FakeObserver(new List<string>()),
            tts);

        PipelineResult result = await pipeline.ProcessAsync(new PipelineRequest(new byte[] { 1 }));

        Assert.False(result.Succeeded);
        Assert.Contains("clarify", result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task ProcessAsync_WhenModelIsDownReturnsQueuedFallback()
    {
        var model = new FakeModel(new List<string>(), "", fail: true);
        await using var background = new BackgroundProcessor();
        var pipeline = new PipelineOrchestrator(
            new FakeStt(new List<string>()),
            new ConversationCoordinator(CodexResolver),
            model,
            new FakeExecutor(new List<string>()),
            new FakeObserver(new List<string>()),
            new FakeTts(new List<string>()),
            backgroundProcessor: background);

        PipelineResult result = await pipeline.ProcessAsync(new PipelineRequest(Transcript: "Ask codex to review the diff"));

        Assert.False(result.Succeeded);
        Assert.True(result.WasQueued);
    }

    [Fact]
    public void LatencyTracker_ReportsThresholdWarning()
    {
        var warnings = new List<string>();
        var tracker = new LatencyTracker(new Dictionary<PipelineStage, long> { [PipelineStage.Plan] = 1 }, warnings.Add);

        StageLatency latency = tracker.Record(PipelineStage.Plan, 2);

        Assert.True(latency.ExceededThreshold);
        Assert.Single(warnings);
        Assert.Contains("Plan", warnings[0]);
    }

    [Fact]
    public async Task DeviceHandoff_UsesSharedConversationHistory()
    {
        using var memory = MemoryStore.CreateInMemory();
        memory.SaveTurn("one", "pc", "Review the build", "Review the build", "codex");
        var handoff = new DeviceHandoff(memory);

        Assert.True(DeviceHandoff.IsExplicitRequest("Continue on my Pixel", out string? target));
        Assert.Equal("pixel", target);
        HandoffSnapshot snapshot = await handoff.SyncAsync("pc", "pixel", 5);

        Assert.Equal("Review the build", snapshot.ActiveObjective);
        Assert.Single(snapshot.RecentTurns);
    }

    private sealed class FakeStt : ISpeechTranscriber
    {
        private readonly IList<string> _events;
        private readonly double? _confidence;
        public FakeStt(IList<string> events, double? confidence = 0.95) { _events = events; _confidence = confidence; }
        public bool IsLoaded => true;
        public void EnsureLoaded() { }
        public TranscriptionResult Transcribe(byte[] pcm16Mono16k, CancellationToken cancellationToken = default)
        {
            _events.Add("stt");
            return new("Ask codex to review the diff", 1, 1, _confidence);
        }
        public void Dispose() { }
    }

    private sealed class FakeModel : ILocalModelClient
    {
        private readonly IList<string> _events;
        private readonly string _output;
        private readonly bool _fail;
        public int Calls { get; private set; }
        public FakeModel(IList<string> events, string output, bool fail = false) { _events = events; _output = output; _fail = fail; }
        public Task<string> CompleteTextAsync(string prompt, int maxTokens = 512, CancellationToken cancellationToken = default)
        {
            Calls++;
            _events.Add("model");
            if (_fail) throw new InvalidOperationException("model offline");
            return Task.FromResult(_output);
        }
        public async IAsyncEnumerable<string> StreamTextAsync(string prompt, int maxTokens = 512, [EnumeratorCancellation] CancellationToken cancellationToken = default) { yield return _output; await Task.CompletedTask; }
        public Task<string> CompleteImageAsync(string prompt, LlamaServerProcess.ImageInput image, int maxTokens = 512, CancellationToken cancellationToken = default) => Task.FromResult(_output);
        public Task<ToolExecutionResult> ExecuteToolsAsync(string prompt, IReadOnlyList<LlamaServerProcess.ToolDefinition> tools, IReadOnlyDictionary<string, ToolHandler> handlers, int maxTokens = 512, CancellationToken cancellationToken = default) => Task.FromResult(new ToolExecutionResult(_output, Array.Empty<ToolExecution>()));
        public void Cancel() { }
    }

    private sealed class FakeExecutor : IPipelineActionExecutor
    {
        private readonly IList<string> _events;
        public FakeExecutor(IList<string> events) { _events = events; }
        public Task<PipelineExecutionResult> ExecuteAsync(ModelDecision decision, CancellationToken cancellationToken = default)
        {
            _events.Add("execute");
            return Task.FromResult(new PipelineExecutionResult(true, "sent"));
        }
    }

    private sealed class FakeObserver : IPipelineResponseObserver
    {
        private readonly IList<string> _events;
        public FakeObserver(IList<string> events) { _events = events; }
        public Task<string?> ObserveAsync(ModelDecision decision, PipelineExecutionResult execution, CancellationToken cancellationToken = default)
        {
            _events.Add("observe");
            return Task.FromResult<string?>("The diff looks good.");
        }
    }

    private sealed class FakeTts : IPipelineSpeechSynthesizer
    {
        private readonly IList<string> _events;
        public FakeTts(IList<string> events) { _events = events; }
        public Task NarrateAsync(string text, CancellationToken cancellationToken = default) { _events.Add("tts"); return Task.CompletedTask; }
    }
}
