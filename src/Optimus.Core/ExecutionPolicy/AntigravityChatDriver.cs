namespace Optimus.Core.ExecutionPolicy;

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public sealed record AntigravityChatOptions(
    string Prompt,
    string? ConversationId = null,
    string? Model = null,
    string? Effort = null,
    string? Agent = null,
    bool DangerouslySkipPermissions = true,
    string OutputFormat = "json",
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null
);

public sealed record AntigravityChatResponse(
    bool Success,
    string? OutputText,
    string? RawOutput,
    string? ConversationId,
    int ExitCode,
    TimeSpan Elapsed,
    string? ErrorMessage = null
);

public interface IAntigravityChatDriver : IDisposable
{
    bool IsCliAvailable { get; }
    string CliPath { get; }
    Task<AntigravityChatResponse> ExecuteAsync(AntigravityChatOptions options, CancellationToken cancellationToken = default);
    void StopOutgoing();
}

public sealed class AntigravityHeadlessChatDriver : IAntigravityChatDriver
{
    private readonly string _cliPath;
    private readonly object _processLock = new();
    private Process? _activeProcess;
    private bool _disposed;

    public AntigravityHeadlessChatDriver(string? customCliPath = null)
    {
        _cliPath = !string.IsNullOrWhiteSpace(customCliPath)
            ? customCliPath
            : ResolveAgyCliPath();
    }

    public bool IsCliAvailable => File.Exists(_cliPath);

    public string CliPath => _cliPath;

    public static string ResolveAgyCliPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string defaultPath = Path.Combine(localAppData, "agy", "bin", "agy.exe");
        if (File.Exists(defaultPath))
        {
            return defaultPath;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                string candidate = Path.Combine(dir.Trim(), "agy.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return defaultPath;
    }

    public async Task<AntigravityChatResponse> ExecuteAsync(
        AntigravityChatOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Prompt);

        if (!IsCliAvailable)
        {
            return new AntigravityChatResponse(
                Success: false,
                OutputText: null,
                RawOutput: null,
                ConversationId: options.ConversationId,
                ExitCode: -1,
                Elapsed: TimeSpan.Zero,
                ErrorMessage: $"Antigravity CLI (agy) was not found at '{_cliPath}'.");
        }

        var sb = new StringBuilder();
        sb.Append("-p ").Append(EscapeArgument(options.Prompt));
        sb.Append(" --output-format ").Append(options.OutputFormat);

        if (options.DangerouslySkipPermissions)
        {
            sb.Append(" --dangerously-skip-permissions");
        }

        if (!string.IsNullOrWhiteSpace(options.ConversationId))
        {
            sb.Append(" --conversation ").Append(EscapeArgument(options.ConversationId));
        }

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            sb.Append(" --model ").Append(EscapeArgument(options.Model));
        }

        if (!string.IsNullOrWhiteSpace(options.Effort))
        {
            sb.Append(" --effort ").Append(EscapeArgument(options.Effort));
        }

        if (!string.IsNullOrWhiteSpace(options.Agent))
        {
            sb.Append(" --agent ").Append(EscapeArgument(options.Agent));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _cliPath,
            Arguments = sb.ToString(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory) && Directory.Exists(options.WorkingDirectory))
        {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        var stopwatch = Stopwatch.StartNew();
        Process process;

        lock (_processLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(AntigravityHeadlessChatDriver));

            process = new Process { StartInfo = startInfo };
            _activeProcess = process;
        }

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.Timeout.HasValue)
        {
            cts.CancelAfter(options.Timeout.Value);
        }

        try
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    lock (stdoutBuilder) stdoutBuilder.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    lock (stderrBuilder) stderrBuilder.AppendLine(e.Data);
                }
            };

            if (!process.Start())
            {
                return new AntigravityChatResponse(
                    Success: false,
                    OutputText: null,
                    RawOutput: null,
                    ConversationId: options.ConversationId,
                    ExitCode: -1,
                    Elapsed: stopwatch.Elapsed,
                    ErrorMessage: "Failed to start agy process.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            string stdout = stdoutBuilder.ToString();
            string stderr = stderrBuilder.ToString();
            int exitCode = process.ExitCode;

            string? parsedText = null;
            string? conversationId = options.ConversationId;

            if (exitCode == 0)
            {
                parsedText = ParseOutputText(stdout, ref conversationId);
            }

            return new AntigravityChatResponse(
                Success: exitCode == 0,
                OutputText: parsedText ?? stdout.Trim(),
                RawOutput: stdout,
                ConversationId: conversationId,
                ExitCode: exitCode,
                Elapsed: stopwatch.Elapsed,
                ErrorMessage: exitCode == 0 ? null : (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr));
        }
        catch (OperationCanceledException)
        {
            StopOutgoing();
            return new AntigravityChatResponse(
                Success: false,
                OutputText: null,
                RawOutput: stdoutBuilder.ToString(),
                ConversationId: options.ConversationId,
                ExitCode: -1,
                Elapsed: stopwatch.Elapsed,
                ErrorMessage: "Antigravity CLI turn was cancelled or timed out.");
        }
        catch (Exception ex)
        {
            StopOutgoing();
            return new AntigravityChatResponse(
                Success: false,
                OutputText: null,
                RawOutput: stdoutBuilder.ToString(),
                ConversationId: options.ConversationId,
                ExitCode: -1,
                Elapsed: stopwatch.Elapsed,
                ErrorMessage: ex.Message);
        }
        finally
        {
            lock (_processLock)
            {
                if (ReferenceEquals(_activeProcess, process))
                {
                    _activeProcess = null;
                }
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Forcefully stops the outgoing process tree before replacement writes.
    /// </summary>
    public void StopOutgoing()
    {
        lock (_processLock)
        {
            if (_activeProcess != null && !_activeProcess.HasExited)
            {
                try
                {
                    _activeProcess.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort kill
                }
            }
        }
    }

    private static string? ParseOutputText(string stdout, ref string? conversationId)
    {
        string trimmed = stdout.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("conversation_id", out var convElem) ||
                    doc.RootElement.TryGetProperty("conversationId", out convElem))
                {
                    conversationId = convElem.GetString() ?? conversationId;
                }

                if (doc.RootElement.TryGetProperty("response", out var respElem))
                {
                    return respElem.GetString();
                }

                if (doc.RootElement.TryGetProperty("text", out var textElem))
                {
                    return textElem.GetString();
                }

                if (doc.RootElement.TryGetProperty("content", out var contentElem))
                {
                    return contentElem.GetString();
                }
            }
        }
        catch
        {
            // Fall back to returning raw trimmed text
        }

        return trimmed;
    }

    private static string EscapeArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopOutgoing();
    }
}
