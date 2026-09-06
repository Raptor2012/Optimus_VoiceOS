namespace Optimus.Inference;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Owns one loopback llama.cpp server and its foreground inference request.</summary>
public sealed class LlamaServerProcess : IDisposable
{
    private readonly string _executablePath;
    private readonly string _modelPath;
    private readonly int _contextSize;
    private readonly int _gpuLayers;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly ConcurrentQueue<string> _serverLog = new();
    private readonly ChildProcessJob _job = new();
    private Process? _process;
    private HttpClient? _client;
    private CancellationTokenSource? _activeRequest;
    private Task? _warmupTask;
    private int _port;
    private bool _disposed;

    public LlamaServerProcess(string executablePath, string modelPath, int contextSize = 4096, int gpuLayers = 99)
        : this(executablePath, modelPath, new LlamaServerOptions(contextSize, gpuLayers)) { }

    public LlamaServerProcess(string executablePath, string modelPath, LlamaServerOptions options)
    {
        _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        _modelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
        ArgumentNullException.ThrowIfNull(options);
        if (options.ContextSize <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.GpuLayers < 0) throw new ArgumentOutOfRangeException(nameof(options));
        _contextSize = options.ContextSize;
        _gpuLayers = options.GpuLayers;
    }

    internal LlamaServerProcess(HttpClient client)
    {
        _executablePath = "test";
        _modelPath = "test";
        _contextSize = 4096;
        _gpuLayers = 99;
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool IsRunning
    {
        get { lock (_lock) return _process is { HasExited: false } && _client != null; }
    }

    public long StartupMilliseconds { get; private set; }
    public int ContextSize => _contextSize;
    public int GpuLayers => _gpuLayers;

    /// <summary>Starts the runtime synchronously for legacy callers.</summary>
    public void EnsureStarted(TimeSpan? timeout = null)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is { HasExited: false } && _client != null) return;
            var stopwatch = Stopwatch.StartNew();
            _port = FindFreePort();
            var startInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in BuildArguments(_modelPath, _port, _contextSize, _gpuLayers))
                startInfo.ArgumentList.Add(argument);
            _process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {_executablePath}.");
            _job.Assign(_process.Handle);
            _process.OutputDataReceived += (_, args) => CaptureServerLog(args.Data);
            _process.ErrorDataReceived += (_, args) => CaptureServerLog(args.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/"), Timeout = TimeSpan.FromMinutes(3) };
            try
            {
                WaitForHealthy(timeout ?? TimeSpan.FromMinutes(3));
                stopwatch.Stop();
                StartupMilliseconds = stopwatch.ElapsedMilliseconds;
            }
            catch
            {
                StopProcessLocked();
                throw;
            }
        }
    }

    /// <summary>Performs the blocking load/health check on a worker thread.</summary>
    public Task EnsureStartedAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); EnsureStarted(timeout); }, cancellationToken);

    /// <summary>Primes the model in the background; calling this never blocks the UI thread.</summary>
    public Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _warmupTask ??= Task.Run(async () =>
            {
                try
                {
                    await EnsureStartedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    await ChatAsync([new ChatMessage("user", "Reply with one short word: ready")], 4, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TimeoutException)
                {
                    // Best effort. A foreground request reports the actionable error.
                }
            }, cancellationToken);
        }
    }

    /// <summary>Returns concise runtime readiness and backend diagnostics without throwing.</summary>
    public async Task<RuntimeReadiness> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        HttpClient? client;
        int processId;
        lock (_lock) { client = _client; processId = _process?.Id ?? 0; }
        if (client == null || !IsRunning) return new RuntimeReadiness(false, false, "llama-server is not running.", processId, _port);
        try
        {
            using HttpResponseMessage health = await client.GetAsync("health", cancellationToken).ConfigureAwait(false);
            if (!health.IsSuccessStatusCode) return new RuntimeReadiness(false, false, $"health returned {(int)health.StatusCode}.", processId, _port);
            CudaBackendStatus cuda = await VerifyCudaBackendAsync(client, cancellationToken).ConfigureAwait(false);
            return new RuntimeReadiness(true, cuda.IsCudaAvailable,
                cuda.IsCudaAvailable ? "ready; CUDA backend detected." : "ready; CUDA backend not detected.", processId, _port);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new RuntimeReadiness(false, false, $"runtime check failed: {ex.Message}", processId, _port);
        }
    }

    public RuntimeReadiness CheckReadiness() => CheckReadinessAsync().GetAwaiter().GetResult();

    public static IEnumerable<string> BuildArguments(string modelPath, int port, int contextSize, int gpuLayers = 99)
    {
        yield return "-m"; yield return modelPath;
        yield return "--host"; yield return "127.0.0.1";
        yield return "--port"; yield return port.ToString(CultureInfo.InvariantCulture);
        yield return "-c"; yield return contextSize.ToString(CultureInfo.InvariantCulture);
        yield return "-t"; yield return Math.Max(1, Environment.ProcessorCount / 2).ToString(CultureInfo.InvariantCulture);
        yield return "-ngl"; yield return Math.Max(0, gpuLayers).ToString(CultureInfo.InvariantCulture);
        yield return "--flash-attn";
        yield return "--jinja";
        yield return "--reasoning-budget"; yield return "0";
        yield return "--no-webui";
    }

    public CudaBackendStatus VerifyCudaBackend()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        HttpClient client;
        lock (_lock) client = _client ?? throw new InvalidOperationException("Server is not started.");
        return VerifyCudaBackendAsync(client, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void RequireCudaBackend()
    {
        CudaBackendStatus status = VerifyCudaBackend();
        if (!status.IsCudaAvailable)
            throw new InvalidOperationException("llama-server is ready but did not report a CUDA backend. Check the CUDA build and -ngl configuration.");
    }

    internal static bool PropsReportCuda(string propsJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(propsJson);
            JsonElement root = document.RootElement;
            if (TryGetString(root, "backend") is { } backend && backend.Contains("cuda", StringComparison.OrdinalIgnoreCase)) return true;
            if (root.TryGetProperty("n_gpu_layers", out JsonElement layers) && layers.TryGetInt32(out int count)) return count > 0;
        }
        catch (JsonException) { }
        return false;
    }

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        CompletionResult result = await CompleteAsync(new ChatRequest(messages, 0, maxTokens, false, null), cancellationToken).ConfigureAwait(false);
        return result.Text;
    }

    internal Task<CompletionResult> CompleteWithToolsAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        int maxTokens,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(new ChatRequest(messages, 0, maxTokens, false, tools), cancellationToken);

    /// <summary>Yields text deltas as they arrive. Stop() or the token aborts the HTTP stream.</summary>
    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages, int maxTokens,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        using InferenceLease lease = await AcquireInferenceAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(new ChatRequest(messages, 0, maxTokens, true, null))
        };
        using HttpResponseMessage response = await lease.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lease.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(lease.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(lease.Token).ConfigureAwait(false) is { } line)
        {
            if (TryReadStreamDelta(line, out string? delta) && !string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

    public async Task<string> ChatWithImageAsync(string prompt, ImageInput image, int maxTokens, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(image);
        var message = new MultimodalChatMessage("user", [
            new ChatContentPart("text", prompt, null),
            new ChatContentPart("image_url", null, new ImageUrl(image.ToDataUri()))
        ]);
        CompletionResult result = await CompleteAsync(new MultimodalChatRequest([message], 0, maxTokens, false), cancellationToken).ConfigureAwait(false);
        return result.Text;
    }

    /// <summary>Cancels only the current foreground request; it does not kill the loaded model.</summary>
    public void Stop() => CancelCurrentRequest();
    public void Cancel() => CancelCurrentRequest();

    public void CancelCurrentRequest()
    {
        CancellationTokenSource? active;
        HttpClient? client;
        lock (_lock) { active = _activeRequest; client = _client; }
        active?.Cancel();
        client?.CancelPendingRequests();
    }

    internal static bool TryReadStreamDelta(string line, out string? delta)
    {
        delta = null;
        if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        string payload = line[5..].Trim();
        if (payload is "" or "[DONE]") return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement choice = document.RootElement.GetProperty("choices")[0];
            if (choice.TryGetProperty("delta", out JsonElement change) && change.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
            {
                delta = content.GetString();
                return true;
            }
        }
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
        return false;
    }

    private async Task<CompletionResult> CompleteAsync(object request, CancellationToken cancellationToken)
    {
        using InferenceLease lease = await AcquireInferenceAsync(cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage response = await lease.Client.PostAsJsonAsync("v1/chat/completions", request, lease.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(lease.Token).ConfigureAwait(false);
        return ParseCompletion(json);
    }

    private async Task<InferenceLease> AcquireInferenceAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        HttpClient client;
        lock (_lock) client = _client ?? throw new InvalidOperationException("Server is not started.");
        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_lock)
        {
            if (_disposed)
            {
                linked.Dispose(); _inferenceGate.Release();
                throw new ObjectDisposedException(nameof(LlamaServerProcess));
            }
            _activeRequest = linked;
        }
        return new InferenceLease(this, client, linked);
    }

    private void ReleaseInference(CancellationTokenSource cts)
    {
        lock (_lock) { if (ReferenceEquals(_activeRequest, cts)) _activeRequest = null; }
        cts.Dispose(); _inferenceGate.Release();
    }

    private static CompletionResult ParseCompletion(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement choice = document.RootElement.GetProperty("choices")[0];
        string text = string.Empty;
        var calls = new List<ToolCall>();
        if (choice.TryGetProperty("message", out JsonElement message))
        {
            if (message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String) text = content.GetString() ?? string.Empty;
            if (message.TryGetProperty("tool_calls", out JsonElement toolCalls)) calls.AddRange(ToolCallParser.ParseJson(toolCalls));
        }
        return new CompletionResult(text, calls);
    }

    private async Task<CudaBackendStatus> VerifyCudaBackendAsync(HttpClient client, CancellationToken cancellationToken)
    {
        string props = await client.GetStringAsync("props", cancellationToken).ConfigureAwait(false);
        bool propsReportCuda = PropsReportCuda(props);
        bool logsReportCuda = _serverLog.Any(line => line.Contains("CUDA", StringComparison.OrdinalIgnoreCase) || line.Contains("ggml-cuda", StringComparison.OrdinalIgnoreCase));
        return new CudaBackendStatus(propsReportCuda || logsReportCuda, propsReportCuda, logsReportCuda, string.Join(Environment.NewLine, _serverLog.TakeLast(20)));
    }

    private void WaitForHealthy(TimeSpan timeout)
    {
        HttpClient client = _client!;
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true }) throw new InvalidOperationException($"llama-server exited with code {_process.ExitCode} while loading {_modelPath}.");
            try
            {
                using HttpResponseMessage response = client.GetAsync("health").GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            Thread.Sleep(250);
        }
        throw new TimeoutException($"llama-server did not become healthy within {timeout.TotalSeconds:F0}s.");
    }

    private void CaptureServerLog(string? line) { if (!string.IsNullOrWhiteSpace(line)) _serverLog.Enqueue(line); }
    private static string? TryGetString(JsonElement element, string property) => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int FindFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void StopProcessLocked()
    {
        _client?.Dispose(); _client = null;
        try
        {
            if (_process is { HasExited: false }) { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        _process?.Dispose(); _process = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _activeRequest?.Cancel();
            StopProcessLocked();
            _job.Dispose();
        }
        _inferenceGate.Dispose();
    }

    public sealed record LlamaServerOptions(int ContextSize = 4096, int GpuLayers = 99);
    public sealed record ChatMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);
    public sealed record ImageInput(byte[] Data, string MediaType = "image/png")
    {
        public string ToDataUri()
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(MediaType);
            if (Data is not { Length: > 0 }) throw new ArgumentException("Image data cannot be empty.", nameof(Data));
            return $"data:{MediaType};base64,{Convert.ToBase64String(Data)}";
        }
    }
    public sealed record ToolDefinition([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("function")] ToolFunction Function)
    {
        public ToolDefinition(string name, string description, JsonElement parameters) : this("function", new ToolFunction(name, description, parameters)) { }
    }
    public sealed record ToolFunction([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("description")] string Description, [property: JsonPropertyName("parameters")] JsonElement Parameters);
    public sealed record ToolCall(string Id, string Name, JsonElement Arguments);
    public sealed record CudaBackendStatus(bool IsCudaAvailable, bool PropsReportedCuda, bool LogsReportedCuda, string Evidence);
    public sealed record RuntimeReadiness(bool IsReady, bool IsCudaAvailable, string Diagnostic, int ProcessId, int Port);
    public sealed record CompletionResult(string Text, IReadOnlyList<ToolCall> ToolCalls);

    private sealed class InferenceLease : IDisposable
    {
        private readonly LlamaServerProcess _owner;
        private readonly CancellationTokenSource _cts;
        private bool _released;
        public InferenceLease(LlamaServerProcess owner, HttpClient client, CancellationTokenSource cts) { _owner = owner; Client = client; _cts = cts; }
        public HttpClient Client { get; }
        public CancellationToken Token => _cts.Token;
        public void Dispose() { if (!_released) { _released = true; _owner.ReleaseInference(_cts); } }
    }
    private sealed record ChatRequest([property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages, [property: JsonPropertyName("temperature")] double Temperature, [property: JsonPropertyName("max_tokens")] int MaxTokens, [property: JsonPropertyName("stream")] bool Stream, [property: JsonPropertyName("tools")] IReadOnlyList<ToolDefinition>? Tools);
    private sealed record MultimodalChatMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] IReadOnlyList<ChatContentPart> Content);
    private sealed record ChatContentPart([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("text")] string? Text, [property: JsonPropertyName("image_url")] ImageUrl? ImageUrl);
    private sealed record ImageUrl([property: JsonPropertyName("url")] string Url);
    private sealed record MultimodalChatRequest([property: JsonPropertyName("messages")] IReadOnlyList<MultimodalChatMessage> Messages, [property: JsonPropertyName("temperature")] double Temperature, [property: JsonPropertyName("max_tokens")] int MaxTokens, [property: JsonPropertyName("stream")] bool Stream);
}