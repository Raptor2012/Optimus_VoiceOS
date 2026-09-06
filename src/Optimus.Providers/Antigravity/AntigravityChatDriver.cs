namespace Optimus.Providers.Antigravity;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

public sealed record HeadlessChatRequest(
    string Prompt,
    string? Project = null,
    string? ConversationId = null,
    string? Model = null,
    string? WorkingDirectory = null);

public enum HeadlessChatEventKind
{
    Text,
    Tool,
    Completed,
    Error
}

public sealed record HeadlessChatEvent(HeadlessChatEventKind Kind, string Text, string? ConversationId = null);

public sealed record HeadlessChatResult(
    bool Succeeded,
    IReadOnlyList<HeadlessChatEvent> Events,
    string? ConversationId,
    string? Error = null)
{
    public string Text
    {
        get
        {
            string incremental = string.Join("", Events.Where(item => item.Kind == HeadlessChatEventKind.Text).Select(item => item.Text));
            return !string.IsNullOrEmpty(incremental)
                ? incremental
                : string.Join("", Events.Where(item => item.Kind == HeadlessChatEventKind.Completed).Select(item => item.Text));
        }
    }
}

public sealed record HeadlessProcessStart(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    string Input);

/// <summary>Process seam used by tests so CLI protocol tests never invoke a real provider.</summary>
public interface IStructuredChatProcess
{
    IAsyncEnumerable<string> RunAsync(HeadlessProcessStart start, CancellationToken cancellationToken = default);
}

public sealed class SystemStructuredChatProcess : IStructuredChatProcess
{
    public async IAsyncEnumerable<string> RunAsync(HeadlessProcessStart start,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo
        {
            FileName = start.Executable,
            WorkingDirectory = string.IsNullOrWhiteSpace(start.WorkingDirectory) ? Environment.CurrentDirectory : start.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string argument in start.Arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {start.Executable}.");
        try
        {
            await process.StandardInput.WriteLineAsync(start.Input).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();

            // Drain both pipes concurrently. Waiting for stdout before reading stderr can
            // deadlock a CLI that emits enough diagnostics to fill its stderr pipe.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            Task waitTask = process.WaitForExitAsync(cancellationToken);
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask, waitTask).ConfigureAwait(false);
            }
            catch
            {
                StopProcess(process);
                await WaitForExitAfterStopAsync(process).ConfigureAwait(false);
                throw;
            }

            foreach (string line in stdoutTask.Result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                yield return line;
            if (process.ExitCode != 0)
            {
                string error = stderrTask.Result.Trim();
                if (string.IsNullOrWhiteSpace(error)) error = $"{start.Executable} exited with code {process.ExitCode}.";
                yield return JsonSerializer.Serialize(new { type = "error", error });
            }
        }
        finally
        {
            StopProcess(process);
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
    }

    private static async Task WaitForExitAfterStopAsync(Process process)
    {
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (InvalidOperationException) { }
    }
}

public sealed record AntigravityChatDriverOptions(string ExecutablePath = "agy", TimeSpan? Timeout = null);

/// <summary>
/// Structured, non-interactive Antigravity driver. Conversation ids are supplied back to the caller
/// so a project leader can remain on one provider conversation across turns.
/// </summary>
public sealed class AntigravityChatDriver
{
    private readonly IStructuredChatProcess _process;
    private readonly AntigravityChatDriverOptions _options;

    public AntigravityChatDriver(IStructuredChatProcess? process = null, AntigravityChatDriverOptions? options = null)
    {
        _process = process ?? new SystemStructuredChatProcess();
        _options = options ?? new AntigravityChatDriverOptions();
    }

    public async Task<HeadlessChatResult> SendAsync(HeadlessChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        TimeSpan? timeoutDuration = _options.Timeout;
        using var timeout = timeoutDuration.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        if (timeout is not null) timeout.CancelAfter(timeoutDuration.GetValueOrDefault());
        CancellationToken token = timeout?.Token ?? cancellationToken;

        List<string> arguments = ["--print", "--input-format", "stream-json", "--output-format", "stream-json"];
        Add(arguments, "--project", request.Project);
        Add(arguments, "--conversation", request.ConversationId);
        Add(arguments, "--model", request.Model);
        string input = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = request.Prompt }
        });

        List<HeadlessChatEvent> events = [];
        string? conversationId = request.ConversationId;
        try
        {
            await foreach (string line in _process.RunAsync(new HeadlessProcessStart(
                _options.ExecutablePath, arguments, request.WorkingDirectory, input), token).ConfigureAwait(false))
            {
                Parse(line, events, ref conversationId);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new(false, events, conversationId, "Antigravity CLI timed out.");
        }
        catch (Exception exception)
        {
            return new(false, events, conversationId, exception.Message);
        }

        bool failed = events.Any(item => item.Kind == HeadlessChatEventKind.Error);
        if (!failed) events.Add(new HeadlessChatEvent(HeadlessChatEventKind.Completed, "", conversationId));
        string? error = events.FirstOrDefault(item => item.Kind == HeadlessChatEventKind.Error)?.Text;
        return new(!failed, events.ToArray(), conversationId, error);
    }

    private static void Add(List<string> args, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) { args.Add(name); args.Add(value); }
    }

    private static void Parse(string line, List<HeadlessChatEvent> events, ref string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            conversationId ??= FindString(root, "conversationId", "conversation_id", "sessionId", "session_id");
            string type = FindString(root, "type") ?? "message";
            string? text = FindString(root, "text", "delta", "content", "result", "message", "error");
            if (root.TryGetProperty("is_error", out JsonElement isError) && isError.ValueKind == JsonValueKind.True)
                type = "error";
            HeadlessChatEventKind kind = type.Contains("error", StringComparison.OrdinalIgnoreCase)
                ? HeadlessChatEventKind.Error
                : type.Contains("tool", StringComparison.OrdinalIgnoreCase)
                    ? HeadlessChatEventKind.Tool
                    : type.Equals("result", StringComparison.OrdinalIgnoreCase) || type.Equals("completed", StringComparison.OrdinalIgnoreCase)
                        ? HeadlessChatEventKind.Completed
                        : HeadlessChatEventKind.Text;
            if (!string.IsNullOrEmpty(text) || kind is HeadlessChatEventKind.Completed or HeadlessChatEventKind.Error)
                events.Add(new(kind, text ?? string.Empty, conversationId));
        }
        catch (JsonException)
        {
            events.Add(new(HeadlessChatEventKind.Text, line, conversationId));
        }
    }

    private static string? FindString(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out JsonElement value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
                return content.GetString();
        }
        return null;
    }
}
