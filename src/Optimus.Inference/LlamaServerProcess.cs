namespace Optimus.Inference;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
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
    private readonly object _lock = new();

    private readonly ChildProcessJob _job = new();
    private Process? _process;
    private HttpClient? _client;
    private int _port;
    private bool _disposed;

    public LlamaServerProcess(string executablePath, string modelPath, int contextSize = 4096)
    {
        _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        _modelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
        _contextSize = contextSize;
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

            foreach (string argument in BuildArguments(_modelPath, _port, _contextSize))
            {
                startInfo.ArgumentList.Add(argument);
            }

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start {_executablePath}.");

            // Tie the server's lifetime to ours at the kernel level, so a force-kill or crash
            // of this process cannot leave a multi-gigabyte model resident.
            _job.Assign(_process.Handle);

            // Drain the pipes so a chatty server cannot fill its buffer and block.
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
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

    internal static IEnumerable<string> BuildArguments(string modelPath, int port, int contextSize)
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
        yield return "--jinja";              // use the model's own chat template
        yield return "--reasoning-budget";   // cleanup must not think out loud
        yield return "0";
        yield return "--no-webui";
    }

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

        var request = new ChatRequest(messages, Temperature: 0, MaxTokens: maxTokens, Stream: false);

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
