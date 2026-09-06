namespace Optimus.Inference;

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Owns one local <c>llama-server</c> child process bound to loopback and talks to it over
/// its OpenAI-compatible endpoint.
/// </summary>
/// <remarks>
/// llama.cpp is used out of process rather than through in-process bindings because the
/// bindings' bundled llama.cpp did not recognise current model architectures (<c>gemma4</c>,
/// <c>qwen35</c>). The server binary tracks upstream directly, so the model, not the binding
/// release cadence, decides what runs.
/// </remarks>
public sealed class LlamaServerProcess : IDisposable
{
    private readonly string _executablePath;
    private readonly string _modelPath;
    private readonly int _contextSize;
    private readonly int _gpuLayers;
    private readonly object _lock = new();
    private readonly ConcurrentQueue<string> _serverLog = new();

    private readonly ChildProcessJob _job = new();
    private Process? _process;
    private HttpClient? _client;
    private int _port;
    private bool _disposed;

    public LlamaServerProcess(string executablePath, string modelPath, int contextSize = 4096, int gpuLayers = 99)
    {
        _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        _modelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
        _contextSize = contextSize;
        _gpuLayers = gpuLayers;
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _process is { HasExited: false } && _client != null;
            }
        }
    }

    public long StartupMilliseconds { get; private set; }

    /// <summary>Starts the server and blocks until it reports healthy.</summary>
    public void EnsureStarted(TimeSpan? timeout = null)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_process is { HasExited: false } && _client != null)
            {
                return;
            }

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
            {
                startInfo.ArgumentList.Add(argument);
            }

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start {_executablePath}.");

            // Tie the server's lifetime to ours at the kernel level, so a force-kill or crash
            // of this process cannot leave a multi-gigabyte model resident.
            _job.Assign(_process.Handle);

            // Drain the pipes so a chatty server cannot fill its buffer and block.
            _process.OutputDataReceived += (_, eventArgs) => CaptureServerLog(eventArgs.Data);
            _process.ErrorDataReceived += (_, eventArgs) => CaptureServerLog(eventArgs.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{_port}/"),
                Timeout = TimeSpan.FromMinutes(3)
            };

            WaitForHealthy(timeout ?? TimeSpan.FromMinutes(3));

            stopwatch.Stop();
            StartupMilliseconds = stopwatch.ElapsedMilliseconds;
        }
    }

    internal static IEnumerable<string> BuildArguments(string modelPath, int port, int contextSize, int gpuLayers = 99)
    {
        yield return "-m";
        yield return modelPath;
        yield return "--host";
        yield return "127.0.0.1"; // loopback only; never exposed off the machine
        yield return "--port";
        yield return port.ToString(CultureInfo.InvariantCulture);
        yield return "-c";
        yield return contextSize.ToString(CultureInfo.InvariantCulture);
        yield return "-t";
        yield return Math.Max(1, Environment.ProcessorCount / 2).ToString(CultureInfo.InvariantCulture);
        yield return "-ngl";              // offload all layers that fit on the configured GPU
        yield return Math.Max(0, gpuLayers).ToString(CultureInfo.InvariantCulture);
        yield return "--flash-attn";      // reduces KV-cache pressure on the 8 GB RTX 4070
        yield return "--jinja";              // use the model's own chat template
        yield return "--reasoning-budget";   // cleanup must not think out loud
        yield return "0";
        yield return "--no-webui";
    }

    /// <summary>
    /// Verifies that llama-server exposed a CUDA-backed model rather than silently falling back
    /// to CPU. This is intentionally an explicit check because llama.cpp can still answer health
    /// checks after a GPU backend failed to load.
    /// </summary>
    public CudaBackendStatus VerifyCudaBackend()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        HttpClient client;
        lock (_lock)
        {
            client = _client ?? throw new InvalidOperationException("Server is not started.");
        }

        string props = client.GetStringAsync("props").GetAwaiter().GetResult();
        bool propsReportCuda = PropsReportCuda(props);
        bool logsReportCuda = _serverLog.Any(line =>
            line.Contains("CUDA", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ggml-cuda", StringComparison.OrdinalIgnoreCase));
        bool passed = propsReportCuda || logsReportCuda;
        string evidence = string.Join(Environment.NewLine, _serverLog.TakeLast(20));

        return new CudaBackendStatus(passed, propsReportCuda, logsReportCuda, evidence);
    }

    internal static bool PropsReportCuda(string propsJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(propsJson);
            JsonElement root = document.RootElement;
            if (TryGetString(root, "backend") is { } backend && backend.Contains("cuda", StringComparison.OrdinalIgnoreCase))
                return true;
            if (root.TryGetProperty("n_gpu_layers", out JsonElement layers) && layers.TryGetInt32(out int count))
                return count > 0;
        }
        catch (JsonException)
        {
            // A non-JSON diagnostic is not proof of CUDA; fall through to false.
        }

        return false;
    }

    private static string? TryGetString(JsonElement objectElement, string propertyName) =>
        objectElement.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void WaitForHealthy(TimeSpan timeout)
    {
        HttpClient client = _client!;
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"llama-server exited with code {_process.ExitCode} while loading {_modelPath}.");
            }

            try
            {
                using HttpResponseMessage response = client.GetAsync("health").GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException)
            {
                // Still loading.
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException($"llama-server did not become healthy within {timeout.TotalSeconds:F0}s.");
    }

    /// <summary>Runs one chat completion and returns the assistant message text.</summary>
    public async Task<string> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        int maxTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        HttpClient client;
        lock (_lock)
        {
            client = _client ?? throw new InvalidOperationException("Server is not started.");
        }

        return await CompleteAsync(client, new ChatRequest(messages, Temperature: 0, MaxTokens: maxTokens, Stream: false), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Scaffolding for vision-capable GGUF models; callers provide an in-memory image only.</summary>
    public Task<string> ChatWithImageAsync(
        string prompt,
        ImageInput image,
        int maxTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);

        HttpClient client;
        lock (_lock)
        {
            client = _client ?? throw new InvalidOperationException("Server is not started.");
        }

        var message = new MultimodalChatMessage(
            "user",
            [
                new ChatContentPart("text", prompt, null),
                new ChatContentPart("image_url", null, new ImageUrl(image.ToDataUri()))
            ]);
        return CompleteAsync(client, new MultimodalChatRequest([message], 0, maxTokens, false), cancellationToken);
    }

    private static async Task<string> CompleteAsync(HttpClient client, object request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client
            .PostAsJsonAsync("v1/chat/completions", request, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        ChatResponse? payload = await response.Content
            .ReadFromJsonAsync<ChatResponse>(cancellationToken)
            .ConfigureAwait(false);

        return payload?.Choices is { Count: > 0 } choices
            ? choices[0].Message?.Content ?? string.Empty
            : string.Empty;
    }

    private void CaptureServerLog(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
            _serverLog.Enqueue(line);
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _client?.Dispose();
            _client = null;

            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Could not signal; process is exiting anyway.
            }

            _process?.Dispose();
            _process = null;

            _job.Dispose();
        }
    }

    public sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    public sealed record ImageInput(byte[] Data, string MediaType = "image/png")
    {
        public string ToDataUri()
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(MediaType);
            if (Data is not { Length: > 0 }) throw new ArgumentException("Image data cannot be empty.", nameof(Data));
            return $"data:{MediaType};base64,{Convert.ToBase64String(Data)}";
        }
    }

    public sealed record CudaBackendStatus(
        bool IsCudaAvailable,
        bool PropsReportedCuda,
        bool LogsReportedCuda,
        string Evidence);

    private sealed record MultimodalChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] IReadOnlyList<ChatContentPart> Content);

    private sealed record ChatContentPart(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("image_url")] ImageUrl? ImageUrl);

    private sealed record ImageUrl(
        [property: JsonPropertyName("url")] string Url);

    private sealed record MultimodalChatRequest(
        [property: JsonPropertyName("messages")] IReadOnlyList<MultimodalChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record ChatRequest(
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice>? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessageOut? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record ChatMessageOut(
        [property: JsonPropertyName("content")] string? Content);
}
