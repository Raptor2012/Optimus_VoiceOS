namespace Optimus.Inference;

/// <summary>Unified local text, image, and structured-tool interface for the desktop operator.</summary>
public interface ILocalModelClient
{
    Task<string> CompleteTextAsync(string prompt, int maxTokens = 512, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> StreamTextAsync(string prompt, int maxTokens = 512, CancellationToken cancellationToken = default);
    Task<string> CompleteImageAsync(string prompt, LlamaServerProcess.ImageInput image, int maxTokens = 512, CancellationToken cancellationToken = default);
    Task<ToolExecutionResult> ExecuteToolsAsync(
        string prompt,
        IReadOnlyList<LlamaServerProcess.ToolDefinition> tools,
        IReadOnlyDictionary<string, ToolHandler> handlers,
        int maxTokens = 512,
        CancellationToken cancellationToken = default);
    void Cancel();
}

/// <summary>Delegates one fully parsed tool call to a desktop adapter.</summary>
public delegate ValueTask<string> ToolHandler(LlamaServerProcess.ToolCall call, CancellationToken cancellationToken);

public sealed class LocalModelClient : ILocalModelClient, IDisposable
{
    private readonly LlamaServerProcess _server;
    private bool _disposed;

    public LocalModelClient(LlamaServerProcess server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }

    public Task<string> CompleteTextAsync(string prompt, int maxTokens = 512, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return _server.ChatAsync([new LlamaServerProcess.ChatMessage("user", prompt)], maxTokens, cancellationToken);
    }

    public async IAsyncEnumerable<string> StreamTextAsync(
        string prompt,
        int maxTokens = 512,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        await foreach (string chunk in _server.StreamChatAsync(
            [new LlamaServerProcess.ChatMessage("user", prompt)], maxTokens, cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    public Task<string> CompleteImageAsync(string prompt, LlamaServerProcess.ImageInput image, int maxTokens = 512, CancellationToken cancellationToken = default) =>
        CompleteImageCoreAsync(prompt, image, maxTokens, cancellationToken);

    private async Task<string> CompleteImageCoreAsync(string prompt, LlamaServerProcess.ImageInput image, int maxTokens, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _server.ChatWithImageAsync(prompt, image, maxTokens, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests tools and executes handlers only after llama-server returned a complete response
    /// and every tool argument parsed as a complete JSON value. A malformed/partial response has
    /// zero executions.
    /// </summary>
    public async Task<ToolExecutionResult> ExecuteToolsAsync(
        string prompt,
        IReadOnlyList<LlamaServerProcess.ToolDefinition> tools,
        IReadOnlyDictionary<string, ToolHandler> handlers,
        int maxTokens = 512,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(handlers);
        LlamaServerProcess.CompletionResult response = await _server.CompleteWithToolsAsync(
            [new LlamaServerProcess.ChatMessage("user", prompt)], tools, maxTokens, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<LlamaServerProcess.ToolCall> calls = response.ToolCalls;
        if (calls.Count == 0 && ToolCallParser.TryParse(response.Text, out IReadOnlyList<LlamaServerProcess.ToolCall> parsed)) calls = parsed;
        if (calls.Count == 0) return new ToolExecutionResult(response.Text, Array.Empty<ToolExecution>());

        var executions = new List<ToolExecution>(calls.Count);
        foreach (LlamaServerProcess.ToolCall call in calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!handlers.TryGetValue(call.Name, out ToolHandler? handler))
            {
                executions.Add(new ToolExecution(call, null, $"No handler registered for tool '{call.Name}'."));
                continue;
            }
            string result = await handler(call, cancellationToken).ConfigureAwait(false);
            executions.Add(new ToolExecution(call, result, null));
        }
        return new ToolExecutionResult(response.Text, executions);
    }

    public void Cancel() => _server.Stop();
    public void Stop() => _server.Stop();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _server.Dispose();
    }
}

public sealed record ToolExecution(LlamaServerProcess.ToolCall Call, string? Result, string? Error);
public sealed record ToolExecutionResult(string Text, IReadOnlyList<ToolExecution> Executions);