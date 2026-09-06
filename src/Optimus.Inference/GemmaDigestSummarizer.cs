namespace Optimus.Inference;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Interface for summarizing agent activity facts into structured digests.
/// </summary>
public interface IDigestSummarizer : IDisposable
{
    bool IsLoaded { get; }

    void EnsureLoaded();

    Task<DigestEvent> SummarizeAsync(
        string? projectId,
        string? projectName,
        string? taskRef,
        IReadOnlyList<DigestFact> facts,
        CancellationToken cancellationToken = default);

    Task PrimeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Local update intelligence and summarization using resident Gemma 4 E2B through <see cref="LlamaServerProcess"/>.
/// Produces concise digests containing headline, spoken summary, source event IDs, project/task refs, and decision flags.
/// </summary>
public sealed class GemmaDigestSummarizer : IDigestSummarizer
{
    private const int MaxNewTokens = 256;

    private const string PrimingPrompt =
        "Project: Optimus Voice OS | Task: Audio interop\n" +
        "- [BUILD] Compiled WasapiInterop\n" +
        "- [TEST] All 35 tests passed";

    private const string SystemPrompt =
        "You are the local intelligence summarizer for Optimus Voice OS. " +
        "Analyze bounded facts and events from coding agents (such as Claude, Antigravity, or Codex) and produce a concise digest. " +
        "Output a single compact JSON object with: " +
        "\"headline\": brief 4-8 word title, " +
        "\"spokenSummary\": concise 1-2 sentence spoken summary for voice readback, " +
        "\"requiresDecision\": boolean (true if an agent needs user approval, decision, or answered question), " +
        "\"decisionOptions\": array of string choices if a decision is required (else []), " +
        "\"detailedSummary\": 2-3 sentence technical overview, " +
        "\"isMeaningful\": boolean (true for test results, build outcomes, decisions, PRs, or milestones; false for routine noise). " +
        "Do NOT think out loud. Output JSON only.";

    private static readonly (string User, string Assistant)[] FewShot =
    {
        (
            "Project: Optimus Voice OS | Task: Audio Interop\n" +
            "- [BUILD] Compiled WasapiInterop and AudioResampler\n" +
            "- [TEST] Running 35 unit tests in Optimus.Inference.Tests\n" +
            "- [TEST] Passed! All 35 tests succeeded in 50ms",
            "{\"headline\": \"Audio interop tests passed\", \"spokenSummary\": \"Optimus Voice OS finished the audio interop slice and all thirty-five unit tests passed.\", \"requiresDecision\": false, \"decisionOptions\": [], \"detailedSummary\": \"Compilation succeeded and all 35 audio interop tests passed cleanly in 50 milliseconds.\", \"isMeaningful\": true}"
        ),
        (
            "Project: Optimus Voice OS | Task: Pixel Navigation\n" +
            "- [PROGRESS] Replaced single screen with 3-tab navigation\n" +
            "- [APPROVAL] Approval requested: Send confirmed refactor draft to Antigravity? Options: [Confirm & Send, Redictate, Decline]",
            "{\"headline\": \"Approval requested for navigation draft\", \"spokenSummary\": \"Antigravity is waiting for approval to submit the three-tab navigation slice.\", \"requiresDecision\": true, \"decisionOptions\": [\"Confirm & Send\", \"Redictate\", \"Decline\"], \"detailedSummary\": \"The 3-tab navigation slice is ready. Waiting for user decision to confirm and send to Antigravity.\", \"isMeaningful\": true}"
        ),
        (
            "Project: Amyloidose XR | Task: Audio Timing\n" +
            "- [PROGRESS] Polling scene hierarchy\n" +
            "- [PROGRESS] Inspecting German audio length 7.053s\n" +
            "- [PROGRESS] Setting visual settle delay to 0.35s",
            "{\"headline\": \"German audio timing aligned\", \"spokenSummary\": \"Amyloidose XR aligned the German audio pacing with a 0.35 second settle delay.\", \"requiresDecision\": false, \"decisionOptions\": [], \"detailedSummary\": \"Verified German audio length at 7.053 seconds and configured the settling delay without procedural additions.\", \"isMeaningful\": true}"
        ),
        (
            "Project: Backend API | Task: Auth Migration\n" +
            "- [STATUS] Session active\n" +
            "- [STATUS] Session active",
            "{\"headline\": \"Auth migration in progress\", \"spokenSummary\": \"Backend auth migration session is active.\", \"requiresDecision\": false, \"decisionOptions\": [], \"detailedSummary\": \"Session is active with ongoing auth migration tasks.\", \"isMeaningful\": false}"
        )
    };

    private readonly LlamaServerProcess _server;
    private bool _disposed;

    public GemmaDigestSummarizer()
        : this(new LlamaServerProcess(ModelLocator.LlamaServerExecutable, ModelLocator.GemmaCleanupModel))
    {
    }

    public GemmaDigestSummarizer(LlamaServerProcess server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }

    public bool IsLoaded => _server.IsRunning;

    public long LoadMilliseconds => _server.StartupMilliseconds;

    public void EnsureLoaded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ModelLocator.RequireGemma();
        _server.EnsureStarted();
    }

    public async Task<DigestEvent> SummarizeAsync(
        string? projectId,
        string? projectName,
        string? taskRef,
        IReadOnlyList<DigestFact> facts,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (facts == null || facts.Count == 0)
        {
            return new DigestEvent(
                Id: $"dig-{Guid.NewGuid():N}",
                ProjectId: projectId,
                ProjectName: projectName,
                TaskRef: taskRef,
                Headline: "No activity",
                SpokenSummary: "No recent updates.",
                RequiresDecision: false,
                SourceEventIds: Array.Empty<string>(),
                CreatedAt: DateTimeOffset.UtcNow,
                IsMeaningful: false
            );
        }

        try
        {
            EnsureLoaded();
            string promptText = FormatFacts(projectName, taskRef, facts);
            var messages = BuildMessages(promptText);

            string raw = await _server.ChatAsync(messages, MaxNewTokens, cancellationToken).ConfigureAwait(false);
            return SanitizeAndParse(raw, projectId, projectName, taskRef, facts);
        }
        catch (Exception ex)
        {
            // Requirement: "If summarization fails retain source events."
            return DigestEvent.CreateFallback(projectId, projectName, taskRef, facts, ex.Message);
        }
    }

    public async Task PrimeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureLoaded();

        var stopwatch = Stopwatch.StartNew();
        await _server
            .ChatAsync(BuildMessages(PrimingPrompt), MaxNewTokens, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        PrimeMilliseconds = stopwatch.ElapsedMilliseconds;
    }

    public long PrimeMilliseconds { get; private set; }

    internal static string FormatFacts(string? projectName, string? taskRef, IReadOnlyList<DigestFact> facts)
    {
        var sb = new StringBuilder();
        sb.Append("Project: ").Append(string.IsNullOrWhiteSpace(projectName) ? "Default" : projectName);
        if (!string.IsNullOrWhiteSpace(taskRef))
        {
            sb.Append(" | Task: ").Append(taskRef);
        }
        sb.AppendLine();

        // Bounded facts: prioritize meaningful facts and take at most 15 facts
        var boundedFacts = facts
            .OrderBy(f => f.Timestamp)
            .TakeLast(15);

        foreach (var fact in boundedFacts)
        {
            string content = fact.Content.Trim();
            if (content.Length > 250)
            {
                content = content[..250] + "...";
            }

            sb.Append("- [").Append(fact.EventType.ToUpperInvariant()).Append("] ");
            if (fact.IsDecision && fact.Options != null && fact.Options.Count > 0)
            {
                sb.Append(content).Append(" Options: [").Append(string.Join(", ", fact.Options)).Append(']');
            }
            else
            {
                sb.Append(content);
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    internal static IReadOnlyList<LlamaServerProcess.ChatMessage> BuildMessages(string promptText)
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

        messages.Add(new LlamaServerProcess.ChatMessage("user", promptText));
        return messages;
    }

    internal static DigestEvent SanitizeAndParse(
        string modelOutput,
        string? projectId,
        string? projectName,
        string? taskRef,
        IReadOnlyList<DigestFact> facts)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
        {
            return DigestEvent.CreateFallback(projectId, projectName, taskRef, facts, "Empty model output");
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

        // Strip markdown code fences
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

        int openBrace = text.IndexOf('{');
        int closeBrace = text.LastIndexOf('}');
        if (openBrace < 0 || closeBrace <= openBrace)
        {
            return DigestEvent.CreateFallback(projectId, projectName, taskRef, facts, "No JSON found in model output");
        }

        string json = text[openBrace..(closeBrace + 1)];

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string headline = root.TryGetProperty("headline", out var hElem) && !string.IsNullOrWhiteSpace(hElem.GetString())
                ? hElem.GetString()!.Trim()
                : (projectName != null ? $"{projectName} update" : "Agent update");

            string spoken = root.TryGetProperty("spokenSummary", out var sElem) && !string.IsNullOrWhiteSpace(sElem.GetString())
                ? sElem.GetString()!.Trim()
                : headline;

            bool requiresDecision = (root.TryGetProperty("requiresDecision", out var dElem) && dElem.GetBoolean()) ||
                                    facts.Any(f => f.IsDecision);

            var optionsList = new List<string>();
            if (root.TryGetProperty("decisionOptions", out var optElem) && optElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in optElem.EnumerateArray())
                {
                    string? str = item.GetString();
                    if (!string.IsNullOrWhiteSpace(str))
                    {
                        optionsList.Add(str.Trim());
                    }
                }
            }

            if (optionsList.Count == 0 && requiresDecision)
            {
                var decisionFact = facts.FirstOrDefault(f => f.IsDecision && f.Options != null && f.Options.Count > 0);
                if (decisionFact?.Options != null)
                {
                    optionsList.AddRange(decisionFact.Options);
                }
            }

            string? detailed = root.TryGetProperty("detailedSummary", out var detElem)
                ? detElem.GetString()?.Trim()
                : null;

            bool isMeaningful = (root.TryGetProperty("isMeaningful", out var mElem) && mElem.GetBoolean()) ||
                                requiresDecision ||
                                facts.Any(f => f.IsMeaningful || f.IsDecision);

            var ids = facts
                .Select(f => f.EventId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            string? original = facts
                .Where(f => !string.IsNullOrWhiteSpace(f.Content))
                .Select(f => f.Content)
                .LastOrDefault();

            return new DigestEvent(
                Id: $"dig-{Guid.NewGuid():N}",
                ProjectId: projectId,
                ProjectName: projectName,
                TaskRef: taskRef,
                Headline: headline,
                SpokenSummary: spoken,
                RequiresDecision: requiresDecision,
                SourceEventIds: ids,
                CreatedAt: DateTimeOffset.UtcNow,
                DetailedSummary: detailed,
                OriginalResponse: original,
                DecisionOptions: optionsList,
                SourceEvents: facts,
                IsMeaningful: isMeaningful
            );
        }
        catch (JsonException ex)
        {
            return DigestEvent.CreateFallback(projectId, projectName, taskRef, facts, $"JSON error: {ex.Message}");
        }
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
