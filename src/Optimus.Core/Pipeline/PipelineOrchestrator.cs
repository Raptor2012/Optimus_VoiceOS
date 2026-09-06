namespace Optimus.Core.Pipeline;

using System.Text.Json;
using Optimus.Core.Conversation;
using Optimus.Core.Memory;
using Optimus.Core.Monitoring;
using Optimus.Inference;
using Optimus.Providers;

public sealed record PipelineRequest(
    byte[]? AudioPcm16Mono16k = null,
    string? Transcript = null,
    string OriginDevice = "pc",
    string? ForegroundApp = null,
    int MaxTokens = 512);

public sealed record PipelineExecutionResult(
    bool Succeeded,
    string Detail,
    string? Response = null,
    bool TargetAvailable = true,
    string? ResponseBaseline = null);

public interface IPipelineActionExecutor
{
    Task<PipelineExecutionResult> ExecuteAsync(ModelDecision decision, CancellationToken cancellationToken = default);
}

public interface IPipelineResponseObserver
{
    Task<string?> ObserveAsync(
        ModelDecision decision,
        PipelineExecutionResult execution,
        CancellationToken cancellationToken = default);
}

public interface IPipelineSpeechSynthesizer
{
    Task NarrateAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Adapter that keeps the synchronous Piper API off the orchestrator's async path.</summary>
public sealed class PiperNarrator : IPipelineSpeechSynthesizer
{
    private readonly PiperSpeechSynthesizer _piper;

    public PiperNarrator(PiperSpeechSynthesizer piper) => _piper = piper ?? throw new ArgumentNullException(nameof(piper));

    public Task NarrateAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return Task.Run(() => _piper.Speak(text, cancellationToken), cancellationToken);
    }
}

/// <summary>Connects the generic pipeline observer to the existing UI response monitor.</summary>
public sealed class ResponseMonitorStage : IPipelineResponseObserver
{
    private readonly ResponseMonitor _monitor;
    private readonly Func<ModelDecision, IntPtr> _windowResolver;
    private readonly Func<ModelDecision, string> _turnResolver;

    public ResponseMonitorStage(
        ResponseMonitor monitor,
        Func<ModelDecision, IntPtr> windowResolver,
        Func<ModelDecision, string> turnResolver)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _windowResolver = windowResolver ?? throw new ArgumentNullException(nameof(windowResolver));
        _turnResolver = turnResolver ?? throw new ArgumentNullException(nameof(turnResolver));
    }

    public Task<string?> ObserveAsync(ModelDecision decision, PipelineExecutionResult execution, CancellationToken cancellationToken = default)
    {
        if (!execution.Succeeded) return Task.FromResult<string?>(execution.Detail);
        return _monitor.MonitorResponseAsync(
            _windowResolver(decision),
            _turnResolver(decision),
            execution.ResponseBaseline,
            cancellationToken);
    }
}

/// <summary>Routes a coordinator-bound objective to exactly one registered provider.</summary>
public sealed class ProviderRouterActionExecutor : IPipelineActionExecutor, IPipelineResponseObserver
{
    private readonly ProviderRouter _router;

    public ProviderRouterActionExecutor(ProviderRouter router) => _router = router ?? throw new ArgumentNullException(nameof(router));

    public async Task<PipelineExecutionResult> ExecuteAsync(ModelDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!decision.IsExecutable || string.IsNullOrWhiteSpace(decision.TargetId))
            return new(false, decision.Message, TargetAvailable: false);

        try
        {
            IProviderAdapter adapter = await _router.ResolveAsync(new ProviderContext(decision.TargetId)).ConfigureAwait(false);
            ProviderState state = await adapter.Observe().ConfigureAwait(false);
            if (GracefulDegradation.IsTargetUnavailable(state))
                return new(false, GracefulDegradation.ForTargetUnavailable(decision.TargetId), TargetAvailable: false);

            cancellationToken.ThrowIfCancellationRequested();
            await adapter.SendMessage(decision.Message).ConfigureAwait(false);
            bool verified = await adapter.VerifySent().ConfigureAwait(false);
            return verified
                ? new(true, "Request sent.", ResponseBaseline: state.LastResponse)
                : new(false, "The destination did not confirm the request was sent.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, GracefulDegradation.ForTargetUnavailable(decision.TargetId), TargetAvailable: false);
        }
    }

    public async Task<string?> ObserveAsync(ModelDecision decision, PipelineExecutionResult execution, CancellationToken cancellationToken = default)
    {
        if (!execution.Succeeded || string.IsNullOrWhiteSpace(decision.TargetId)) return execution.Detail;
        try
        {
            IProviderAdapter adapter = await _router.ResolveAsync(new ProviderContext(decision.TargetId)).ConfigureAwait(false);
            return await adapter.ReadLastResponse().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"The request was sent, but I could not read the response: {ex.Message}";
        }
    }
}

public sealed record PipelineResult(
    bool Succeeded,
    string Transcript,
    string Response,
    ModelDecision Decision,
    PipelineLatencySnapshot Latency,
    PipelineStage? FailedStage = null,
    string? Error = null)
{
    public bool WasQueued => Response.Contains("queued", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Runs one complete local turn: listen, understand, plan, execute, observe, narrate.
/// Every boundary is timed and failures become a spoken, actionable response rather than an
/// unhandled exception on the audio thread.
/// </summary>
public sealed class PipelineOrchestrator
{
    private readonly ISpeechTranscriber _stt;
    private readonly ConversationCoordinator _coordinator;
    private readonly ILocalModelClient _model;
    private readonly IPipelineActionExecutor _executor;
    private readonly IPipelineResponseObserver _observer;
    private readonly IPipelineSpeechSynthesizer _tts;
    private readonly MemoryStore? _memory;
    private readonly BackgroundProcessor _background;
    private readonly LatencyTracker _latency;
    private readonly Action<string>? _log;

    public PipelineOrchestrator(
        ISpeechTranscriber stt,
        ConversationCoordinator coordinator,
        ILocalModelClient model,
        IPipelineActionExecutor executor,
        IPipelineResponseObserver observer,
        IPipelineSpeechSynthesizer tts,
        LatencyTracker? latencyTracker = null,
        MemoryStore? memoryStore = null,
        BackgroundProcessor? backgroundProcessor = null,
        Action<string>? log = null)
    {
        _stt = stt ?? throw new ArgumentNullException(nameof(stt));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _tts = tts ?? throw new ArgumentNullException(nameof(tts));
        _latency = latencyTracker ?? new LatencyTracker(log: log);
        _memory = memoryStore;
        _background = backgroundProcessor ?? new BackgroundProcessor();
        _log = log;
    }

    public LatencyTracker Latency => _latency;

    public async Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _latency.Reset();
        string transcript = string.Empty;
        TranscriptionResult? transcription = null;
        PipelineStage? failedStage = null;

        try
        {
            using (_latency.Measure(PipelineStage.Listen))
            {
                if (!string.IsNullOrWhiteSpace(request.Transcript)) transcript = request.Transcript.Trim();
                else if (request.AudioPcm16Mono16k != null) transcription = _stt.Transcribe(request.AudioPcm16Mono16k, cancellationToken);
                else throw new ArgumentException("Audio or transcript is required.", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(transcript)) transcript = transcription?.Text.Trim() ?? string.Empty;
            if (transcription is { IsEmpty: true })
                return await FinishAsync(false, transcript, ModelDecision.Respond("I did not hear anything. Please try again."), "I did not hear anything. Please try again.", null, cancellationToken).ConfigureAwait(false);
            if (transcription != null && GracefulDegradation.IsLowConfidence(transcription))
            {
                string clarification = GracefulDegradation.ForLowConfidence(transcription.Confidence);
                return await FinishAsync(false, transcript, ModelDecision.Clarify(clarification), clarification, null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            failedStage = PipelineStage.Listen;
            _log?.Invoke($"[Pipeline] listen failed: {ex.Message}");
            return await FinishAsync(false, string.Empty, ModelDecision.Respond(GracefulDegradation.ForException("listening")), GracefulDegradation.ForException("listening"), failedStage, cancellationToken).ConfigureAwait(false);
        }

        ConversationTurnResult understood;
        try
        {
            using (_latency.Measure(PipelineStage.Understand))
                understood = _coordinator.Process(transcript, request.OriginDevice, request.ForegroundApp);
            SaveTurn(understood, null);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Pipeline] understand failed: {ex.Message}");
            return await FinishAsync(false, transcript, ModelDecision.Respond(GracefulDegradation.ForException("understanding")), GracefulDegradation.ForException("understanding"), PipelineStage.Understand, cancellationToken).ConfigureAwait(false);
        }

        ModelDecision decision = understood.Decision;
        string response = decision.Message;
        try
        {
            using (_latency.Measure(PipelineStage.Plan))
            {
                string planPrompt = $"Return a JSON tool call only when needed. Otherwise return a short response.\n" +
                    $"User request: {transcript}\nResolved target: {decision.TargetId ?? "none"}\n" +
                    $"Coordinator decision: {decision.Kind} - {decision.Message}";
                string modelOutput = await _model.CompleteTextAsync(planPrompt, request.MaxTokens, cancellationToken).ConfigureAwait(false);
                if (ToolCallParser.TryParse(modelOutput, out IReadOnlyList<LlamaServerProcess.ToolCall> calls) && calls.Count > 0)
                    decision = ToDecision(calls[0], decision);
                else if (!decision.IsExecutable && !string.IsNullOrWhiteSpace(modelOutput)) response = modelOutput.Trim();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Invoke($"[Pipeline] local model unavailable: {ex.Message}");
            _background.Queue(
                "local model retry",
                retryToken => RetryQueuedRequestAsync(transcript, decision, request.MaxTokens, retryToken),
                cancellationToken: CancellationToken.None);
            response = GracefulDegradation.ForModelUnavailable();
            return await FinishAsync(false, transcript, ModelDecision.Respond(response), response, PipelineStage.Plan, cancellationToken).ConfigureAwait(false);
        }

        if (!decision.IsExecutable)
            return await FinishAsync(decision.Kind == ModelDecisionKind.Complete, transcript, decision, response, null, cancellationToken).ConfigureAwait(false);

        PipelineExecutionResult execution;
        try
        {
            using (_latency.Measure(PipelineStage.Execute))
                execution = await _executor.ExecuteAsync(decision, cancellationToken).ConfigureAwait(false);
            if (!execution.Succeeded)
            {
                response = execution.TargetAvailable ? execution.Detail : GracefulDegradation.ForTargetUnavailable(decision.TargetId ?? "The selected app");
                return await FinishAsync(false, transcript, ModelDecision.Respond(response), response, PipelineStage.Execute, cancellationToken).ConfigureAwait(false);
            }
            // Corrections must know that the exact coordinator-bound request was dispatched.
            _coordinator.AcknowledgeToolInvocation();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Invoke($"[Pipeline] execute failed: {ex.Message}");
            response = GracefulDegradation.ForException("execution");
            return await FinishAsync(false, transcript, ModelDecision.Respond(response), response, PipelineStage.Execute, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using (_latency.Measure(PipelineStage.Observe))
                response = await _observer.ObserveAsync(decision, execution, cancellationToken).ConfigureAwait(false) ?? execution.Detail;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Invoke($"[Pipeline] observe failed: {ex.Message}");
            response = "The request was sent, but I could not observe the result yet.";
        }

        return await FinishAsync(true, transcript, decision, response, failedStage, cancellationToken).ConfigureAwait(false);
    }

    public Task<PipelineResult> ProcessAsync(byte[] pcm16Mono16k, string originDevice = "pc", CancellationToken cancellationToken = default) =>
        ProcessAsync(new PipelineRequest(pcm16Mono16k, OriginDevice: originDevice), cancellationToken);

    private async Task<PipelineResult> FinishAsync(bool succeeded, string transcript, ModelDecision decision, string response, PipelineStage? failedStage, CancellationToken cancellationToken)
    {
        using (_latency.Measure(PipelineStage.Narrate))
        {
            try { await _tts.NarrateAsync(response, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log?.Invoke($"[Pipeline] narrate failed: {ex.Message}"); }
        }
        long elapsed = _latency.Snapshot.Stages
            .Where(pair => pair.Key != PipelineStage.Total)
            .Sum(pair => pair.Value.ElapsedMilliseconds);
        StageLatency total = _latency.Record(PipelineStage.Total, elapsed);
        PipelineLatencySnapshot snapshot = _latency.Snapshot;
        _log?.Invoke($"[Pipeline] turn completed in {total.ElapsedMilliseconds} ms.");
        return new(succeeded, transcript, response, decision, snapshot, failedStage);
    }

    private async Task<string> RetryQueuedRequestAsync(
        string transcript,
        ModelDecision coordinatorDecision,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        string output = await _model.CompleteTextAsync(
            $"Return a JSON tool call only when needed. User request: {transcript}\nResolved target: {coordinatorDecision.TargetId}",
            maxTokens,
            cancellationToken).ConfigureAwait(false);
        if (ToolCallParser.TryParse(output, out IReadOnlyList<LlamaServerProcess.ToolCall> calls) && calls.Count > 0)
        {
            ModelDecision decision = ToDecision(calls[0], coordinatorDecision);
            if (decision.IsExecutable)
            {
                PipelineExecutionResult execution = await _executor.ExecuteAsync(decision, cancellationToken).ConfigureAwait(false);
                if (!execution.Succeeded) return execution.Detail;
                _coordinator.AcknowledgeToolInvocation();
                return await _observer.ObserveAsync(decision, execution, cancellationToken).ConfigureAwait(false) ?? execution.Detail;
            }
        }
        return output.Trim();
    }

    private void SaveTurn(ConversationTurnResult result, string? response)
    {
        _memory?.SaveTurn(result.Turn.TurnId.ToString(), result.Turn.OriginDevice, result.Turn.Transcript,
            result.Contexts.Request.Objective, response,
            result.Decision.ToolName == null ? null : JsonSerializer.Serialize(result.Decision.Arguments), result.Turn.Timestamp);
    }

    private static ModelDecision ToDecision(LlamaServerProcess.ToolCall call, ModelDecision fallback)
    {
        // Coordinator resolution is authoritative. A model may refine formatting, but it may
        // never turn clarification/confirmation into execution or select another destination.
        if (fallback.Kind != ModelDecisionKind.InvokeTool || fallback.RequiresConfirmation ||
            !string.Equals(call.Name, fallback.ToolName, StringComparison.OrdinalIgnoreCase))
            return fallback;

        string? target = ReadString(call.Arguments, "targetId");
        if (!string.Equals(target, fallback.TargetId, StringComparison.OrdinalIgnoreCase)) return fallback;
        return ModelDecision.InvokeTool(fallback.ToolName!, fallback.Message, fallback.TargetId!, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["objective"] = fallback.Message,
                ["targetId"] = fallback.TargetId!
            });
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
