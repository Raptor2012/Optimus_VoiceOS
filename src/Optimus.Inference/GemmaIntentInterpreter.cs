namespace Optimus.Inference;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Interprets spoken commands and approvals using Gemma 4 E2B through <see cref="LlamaServerProcess"/>.
/// </summary>
/// <remarks>
/// Used as a fallback when deterministic regex/word matching does not recognize the utterance.
/// Commands for Optimus are classified into explicit intents; prompts intended for coding agents
/// are classified as "none" and returned intact to the normal draft pipeline.
/// </remarks>
public sealed class GemmaIntentInterpreter : IIntentInterpreter
{
    private const int MaxNewTokens = 48;

    private const string PrimingUtterance = "can you switch to claude";

    private const string SystemPrompt =
        "Classify the spoken utterance for Optimus Voice OS. " +
        "Output a single compact JSON object with 'intent' and any relevant parameters ('target', 'text', 'old', 'new'). " +
        "If the utterance is a prompt or coding task for an AI agent (like Claude, Antigravity, or Codex), output {\"intent\": \"none\"}. " +
        "Allowed command intents: affirmative, cancel, redictate, useOriginal, switch, append, replace, removeLast, repeat, " +
        "cleanupOn, cleanupOff, interruptOn, interruptOff, mute, unmute, concise, comprehensive, toolsOn, toolsOff, status, detailsOn, detailsOff, alias. " +
        "Do NOT think out loud. Output JSON only.";

    private static readonly (string User, string Assistant)[] FewShot =
    {
        ("yeah go ahead and send it", "{\"intent\": \"affirmative\"}"),
        ("looks good to me", "{\"intent\": \"affirmative\"}"),
        ("ship it", "{\"intent\": \"affirmative\"}"),
        ("do not send that", "{\"intent\": \"cancel\"}"),
        ("throw it away", "{\"intent\": \"cancel\"}"),
        ("let me say that again", "{\"intent\": \"redictate\"}"),
        ("start over", "{\"intent\": \"redictate\"}"),
        ("revert to what I originally said", "{\"intent\": \"useOriginal\"}"),
        ("use the raw text", "{\"intent\": \"useOriginal\"}"),
        ("can you switch to anti-gravity", "{\"intent\": \"switch\", \"target\": \"Antigravity\"}"),
        ("change destination to claude", "{\"intent\": \"switch\", \"target\": \"Claude\"}"),
        ("switch to codex", "{\"intent\": \"switch\", \"target\": \"Codex\"}"),
        ("please turn on prompt cleanup", "{\"intent\": \"cleanupOn\"}"),
        ("turn off cleanup", "{\"intent\": \"cleanupOff\"}"),
        ("let me interrupt you", "{\"intent\": \"interruptOn\"}"),
        ("stop interrupting", "{\"intent\": \"interruptOff\"}"),
        ("mute narration", "{\"intent\": \"mute\"}"),
        ("unmute the voice", "{\"intent\": \"unmute\"}"),
        ("what is happening right now", "{\"intent\": \"status\"}"),
        ("show details", "{\"intent\": \"detailsOn\"}"),
        ("hide details", "{\"intent\": \"detailsOff\"}"),
        ("read everything out loud", "{\"intent\": \"fullReview\"}"),
        ("destination only", "{\"intent\": \"shortReview\"}"),
        ("could you repeat that", "{\"intent\": \"repeat\"}"),
        ("remove the last sentence", "{\"intent\": \"removeLast\"}"),
        ("also add and verify with tests", "{\"intent\": \"append\", \"text\": \"and verify with tests\"}"),
        ("replace getUser with fetchUser", "{\"intent\": \"replace\", \"old\": \"getUser\", \"new\": \"fetchUser\"}"),
        ("remember this as backend", "{\"intent\": \"alias\", \"target\": \"backend\"}"),
        ("Add a button to switch between light and dark theme", "{\"intent\": \"none\"}"),
        ("Mute the audio in sound_manager.cpp when window loses focus", "{\"intent\": \"none\"}"),
        ("Please cancel the pending HTTP request in axios interceptor", "{\"intent\": \"none\"}"),
        ("Write a python function to repeat a string n times", "{\"intent\": \"none\"}"),
        ("Can you fix the compilation error in MainWindow.xaml", "{\"intent\": \"none\"}"),
        ("to claude add a unit test for login", "{\"intent\": \"none\"}")
    };

    private readonly LlamaServerProcess _server;
    private bool _disposed;

    public GemmaIntentInterpreter(LlamaServerProcess server) =>
        _server = server ?? throw new ArgumentNullException(nameof(server));

    public bool IsLoaded => _server.IsRunning;

    public void EnsureLoaded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ModelLocator.RequireGemma();
        _server.EnsureStarted();
    }

    public async Task<InterpretedIntent?> InterpretAsync(string utterance, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(utterance))
        {
            return null;
        }

        try
        {
            EnsureLoaded();
        }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException or InvalidOperationException or TimeoutException)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        string raw;
        try
        {
            raw = await _server
                .ChatAsync(BuildMessages(utterance), MaxNewTokens, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
        stopwatch.Stop();

        return SanitizeAndParse(raw, stopwatch.ElapsedMilliseconds);
    }

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

    public long PrimeMilliseconds { get; private set; }

    internal static IReadOnlyList<LlamaServerProcess.ChatMessage> BuildMessages(string utterance)
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

        messages.Add(new LlamaServerProcess.ChatMessage("user", utterance));
        return messages;
    }

    internal static InterpretedIntent? SanitizeAndParse(string modelOutput, long elapsedMilliseconds = 0)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
        {
            return null;
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

        text = text.Trim();

        // Strip markdown fences ```json ... ```
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
            {
                text = text[(firstNewline + 1)..];
            }

            int endFence = text.IndexOf("```", StringComparison.Ordinal);
            if (endFence >= 0)
            {
                text = text[..endFence];
            }

            text = text.Trim();
        }

        // Find outer JSON brackets
        int openBrace = text.IndexOf('{');
        int closeBrace = text.LastIndexOf('}');
        if (openBrace < 0 || closeBrace <= openBrace)
        {
            return null;
        }

        string json = text[openBrace..(closeBrace + 1)];

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("intent", out JsonElement intentElement))
            {
                return null;
            }

            string? intent = intentElement.GetString();
            if (string.IsNullOrWhiteSpace(intent))
            {
                return null;
            }

            string? target = root.TryGetProperty("target", out JsonElement tElem) ? tElem.GetString() : null;
            string? intentText = root.TryGetProperty("text", out JsonElement txtElem) ? txtElem.GetString() : null;
            string? oldText = root.TryGetProperty("old", out JsonElement oldElem) ? oldElem.GetString() : null;
            string? newText = root.TryGetProperty("new", out JsonElement newElem) ? newElem.GetString() : null;

            return new InterpretedIntent(
                Intent: intent,
                Target: target,
                Text: intentText,
                OldText: oldText,
                NewText: newText,
                ElapsedMilliseconds: elapsedMilliseconds);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
