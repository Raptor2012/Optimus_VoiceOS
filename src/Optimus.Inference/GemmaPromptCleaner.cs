namespace Optimus.Inference;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Local prompt cleanup using Gemma 4 E2B (QAT Q4 GGUF) through <see cref="LlamaServerProcess"/>.
/// </summary>
/// <remarks>
/// Cleanup repairs dictation artefacts only. It must never add requirements, answer the
/// request, choose a destination, or reinterpret intent (AGENTS.md product invariants).
/// <para>
/// The few-shot examples below are load-bearing, not decoration. Gemma 4 is a reasoning model:
/// with only a system instruction it narrates its reasoning into the reply and drifts toward
/// performing the request instead of rewriting it. The worked examples pin the output shape to
/// a single cleaned sentence, which both fixes the drift and cuts the reply to ~15 tokens.
/// </para>
/// </remarks>
public sealed class GemmaPromptCleaner : IPromptCleaner
{
    private const int MaxNewTokens = 160;

    /// <summary>Short, representative, and deliberately throwaway.</summary>
    private const string PrimingUtterance = "um so like add a comment here";

    private const string SystemPrompt =
        "Rewrite dictated speech as clean written text. Remove filler words and false starts, " +
        "fix punctuation and capitalization, and write code identifiers, file paths and flags " +
        "the way a developer types them. Do NOT answer or perform the request. Do NOT add or " +
        "remove requirements. Do NOT explain or think out loud. " +
        "Reply with the cleaned sentence only.";

    private static readonly (string User, string Assistant)[] FewShot =
    {
        ("um so like add a retry to the fetch user function in api dot ts",
         "Add a retry to the fetchUser function in api.ts."),
        ("okay can you uh bump the timeout flag to thirty seconds in config dot yaml",
         "Bump the --timeout flag to 30 seconds in config.yaml."),
        ("i wanna uh i wanna rename get user data to fetch user profile everywhere",
         "Rename getUserData to fetchUserProfile everywhere.")
    };

    private readonly LlamaServerProcess _server;
    private bool _disposed;

    public GemmaPromptCleaner()
        : this(new LlamaServerProcess(ModelLocator.LlamaServerExecutable, ModelLocator.GemmaCleanupModel))
    {
    }

    public GemmaPromptCleaner(LlamaServerProcess server) =>
        _server = server ?? throw new ArgumentNullException(nameof(server));

    public bool IsLoaded => _server.IsRunning;

    public long LoadMilliseconds => _server.StartupMilliseconds;

    public void EnsureLoaded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ModelLocator.RequireGemma();
        _server.EnsureStarted();
    }

    public async Task<CleanupResult> CleanAsync(string rawTranscript, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(rawTranscript))
        {
            return new CleanupResult(string.Empty, 0, Applied: false, "Empty transcript");
        }

        try
        {
            EnsureLoaded();
        }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException or InvalidOperationException or TimeoutException)
        {
            // Degrade to the raw transcript. The confirmation gate is unchanged, and showing
            // the unedited words can never change what the user asked for.
            return new CleanupResult(rawTranscript, 0, Applied: false, ex.Message);
        }

        var stopwatch = Stopwatch.StartNew();
        string raw = await _server
            .ChatAsync(BuildMessages(rawTranscript), MaxNewTokens, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        string cleaned = Sanitize(raw, rawTranscript);
        bool applied = !string.Equals(cleaned, rawTranscript, StringComparison.Ordinal);

        return new CleanupResult(
            cleaned,
            stopwatch.ElapsedMilliseconds,
            applied,
            applied ? null : "Cleanup returned no usable change");
    }

    /// <summary>
    /// Sends one short throwaway utterance through the exact same message shape as a real
    /// cleanup, so the server caches the shared system + few-shot prefix.
    /// </summary>
    public async Task PrimeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureLoaded();

        var stopwatch = Stopwatch.StartNew();
        await _server
            .ChatAsync(BuildMessages(PrimingUtterance), MaxNewTokens, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        PrimeMilliseconds = stopwatch.ElapsedMilliseconds;
    }

    /// <summary>Milliseconds the priming call took, for diagnostics.</summary>
    public long PrimeMilliseconds { get; private set; }

    internal static IReadOnlyList<LlamaServerProcess.ChatMessage> BuildMessages(string rawTranscript)
    {
        var messages = new List<LlamaServerProcess.ChatMessage>(2 + (FewShot.Length * 2))
        {
            new("system", SystemPrompt)
        };

        foreach ((string user, string assistant) in FewShot)
        {
            messages.Add(new LlamaServerProcess.ChatMessage("user", user));
            messages.Add(new LlamaServerProcess.ChatMessage("assistant", assistant));
        }

        messages.Add(new LlamaServerProcess.ChatMessage("user", rawTranscript));
        return messages;
    }

    /// <summary>
    /// Strips template scaffolding and any leaked reasoning, and falls back to the raw
    /// transcript when nothing usable is left. Falling back can lose the cleanup but can never
    /// change what the user asked for.
    /// </summary>
    internal static string Sanitize(string modelOutput, string rawTranscript)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
        {
            return rawTranscript;
        }

        string text = modelOutput;

        int thinkEnd = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0)
        {
            text = text[(thinkEnd + "</think>".Length)..];
        }

        foreach (string marker in new[] { "<end_of_turn>", "<start_of_turn>", "<|im_end|>", "<|endoftext|>" })
        {
            int index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                text = text[..index];
            }
        }

        text = text.Trim().Trim('"').Trim();

        // A reply that ran to several lines is narration, not a cleaned sentence.
        if (text.Contains('\n', StringComparison.Ordinal))
        {
            string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            text = lines.Length > 0 ? lines[^1] : string.Empty;
        }

        return string.IsNullOrWhiteSpace(text) ? rawTranscript : text;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _server.Dispose();
    }
}
