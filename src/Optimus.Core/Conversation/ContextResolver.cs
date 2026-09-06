namespace Optimus.Core.Conversation;

public enum ContextResolutionStatus
{
    Resolved,
    Missing,
    Unknown,
    Ambiguous
}

/// <summary>A destination or conversation that can be bound for one executable request.</summary>
public sealed record ContextTarget
{
    public ContextTarget(string id, string name, IEnumerable<string>? aliases = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id.Trim();
        Name = name.Trim();
        Aliases = (aliases ?? Array.Empty<string>())
            .Append(Id)
            .Append(Name)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<string> Aliases { get; }
}

public sealed record ContextResolutionRequest(
    string Transcript,
    string? ActiveObjective = null,
    string? ForegroundApp = null,
    IReadOnlyDictionary<string, string>? StoredPreferences = null,
    string? ActiveTargetId = null);

public sealed record ContextResolution(
    ContextResolutionStatus Status,
    string? TargetId,
    string? TargetName,
    IReadOnlyList<ContextTarget> Candidates,
    string? Reference,
    string Reason)
{
    public bool IsResolved => Status == ContextResolutionStatus.Resolved;
}

/// <summary>
/// Resolves a reference for one request. It is deliberately stateless with respect to targets:
/// browsing one application never changes the target of a later request.
/// </summary>
public sealed class ContextResolver
{
    private static readonly string[] ReferenceTokens = { " it", " that", " there", " them", " this", " here" };
    private static readonly string[] ReferenceWords = { "it", "that", "there", "them", "this", "here" };
    private readonly IReadOnlyList<ContextTarget> _targets;

    public ContextResolver(IEnumerable<ContextTarget>? targets = null)
    {
        _targets = (targets ?? Array.Empty<ContextTarget>()).ToArray();
    }

    public IReadOnlyList<ContextTarget> Targets => _targets;

    public ContextResolution Resolve(
        string transcript,
        string? activeObjective = null,
        string? foregroundApp = null,
        IReadOnlyDictionary<string, string>? storedPreferences = null,
        string? activeTargetId = null) =>
        Resolve(new ContextResolutionRequest(
            transcript,
            activeObjective,
            foregroundApp,
            storedPreferences,
            activeTargetId));

    public ContextResolution Resolve(ContextResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Transcript);

        string text = request.Transcript.Trim();
        List<(ContextTarget Target, int Score, string Reference)> explicitMatches = FindExplicitMatches(text);
        if (explicitMatches.Count > 0)
        {
            int bestScore = explicitMatches.Max(match => match.Score);
            var best = explicitMatches.Where(match => match.Score == bestScore)
                .GroupBy(match => match.Target.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            if (best.Length == 1)
            {
                return Resolved(best[0].Target, best[0].Reference, "explicit wording");
            }

            return new(
                ContextResolutionStatus.Ambiguous,
                null,
                null,
                best.Select(match => match.Target).ToArray(),
                best[0].Reference,
                "The wording matches more than one destination.");
        }

        bool hasReference = ContainsReferenceWord(text);
        if (!string.IsNullOrWhiteSpace(request.ActiveTargetId))
        {
            ContextTarget? activeTarget = FindTarget(request.ActiveTargetId);
            if (activeTarget != null && (hasReference || LooksLikeContinuation(text)))
            {
                return Resolved(activeTarget, ReferenceWord(text), "active request");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ActiveObjective))
        {
            List<ContextTarget> objectiveTargets = FindTargetsInText(request.ActiveObjective);
            if (objectiveTargets.Count == 1 && (hasReference || LooksLikeContinuation(text)))
            {
                return Resolved(objectiveTargets[0], ReferenceWord(text), "active objective");
            }
            if (objectiveTargets.Count > 1 && hasReference)
            {
                return Ambiguous(objectiveTargets, ReferenceWord(text), "The active objective has multiple possible destinations.");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ForegroundApp) && (hasReference || LooksLikeContinuation(text)))
        {
            ContextTarget? foreground = FindTarget(request.ForegroundApp);
            if (foreground != null)
            {
                return Resolved(foreground, ReferenceWord(text), "foreground application");
            }
        }

        if (TryReadPreferredTarget(request.StoredPreferences, out string? preferred))
        {
            ContextTarget? preferredTarget = FindTarget(preferred!);
            if (preferredTarget != null && !HasCompetingTargetMention(text))
            {
                return Resolved(preferredTarget, "preferred destination", "stored preference");
            }
        }

        return new(
            hasReference ? ContextResolutionStatus.Missing : ContextResolutionStatus.Unknown,
            null,
            null,
            Array.Empty<ContextTarget>(),
            hasReference ? ReferenceWord(text) : null,
            hasReference ? "The referenced destination is not available." : "No destination was named.");
    }

    private List<(ContextTarget Target, int Score, string Reference)> FindExplicitMatches(string text)
    {
        var matches = new List<(ContextTarget Target, int Score, string Reference)>();
        foreach (ContextTarget target in _targets)
        {
            foreach (string alias in target.Aliases)
            {
                if (ContainsPhrase(text, alias))
                {
                    // An exact target id/name wins over a friendly alias. Longest phrase wins
                    // so "Claude VoiceOS" is not reduced to "Claude".
                    int score = alias.Length * 10 +
                        (string.Equals(alias, target.Id, StringComparison.OrdinalIgnoreCase) ? 2 : 0) +
                        (string.Equals(alias, target.Name, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
                    matches.Add((target, score, alias));
                }
            }
        }
        return matches;
    }

    private List<ContextTarget> FindTargetsInText(string text) =>
        _targets.Where(target => target.Aliases.Any(alias => ContainsPhrase(text, alias))).ToList();

    private ContextTarget? FindTarget(string value) =>
        _targets.FirstOrDefault(target =>
            string.Equals(target.Id, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, value, StringComparison.OrdinalIgnoreCase) ||
            target.Aliases.Any(alias => string.Equals(alias, value, StringComparison.OrdinalIgnoreCase)));

    private static ContextResolution Resolved(ContextTarget target, string? reference, string reason) =>
        new(ContextResolutionStatus.Resolved, target.Id, target.Name, new[] { target }, reference, reason);

    private static ContextResolution Ambiguous(IEnumerable<ContextTarget> targets, string? reference, string reason)
    {
        ContextTarget[] candidates = targets.ToArray();
        return new(ContextResolutionStatus.Ambiguous, null, null, candidates, reference, reason);
    }

    private static bool TryReadPreferredTarget(
        IReadOnlyDictionary<string, string>? preferences,
        out string? value)
    {
        value = null;
        if (preferences == null) return false;
        foreach (string key in new[] { "preferredDestination", "defaultDestination", "destination", "preferredApp" })
        {
            if (preferences.TryGetValue(key, out string? candidate) && !string.IsNullOrWhiteSpace(candidate))
            {
                value = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool ContainsReferenceWord(string text) =>
        ReferenceTokens
            .Any(token => $" {text.ToLowerInvariant()} ".Contains(token + " ", StringComparison.Ordinal));

    private static string? ReferenceWord(string text)
    {
        string lower = text.ToLowerInvariant();
        foreach (string word in ReferenceWords)
        {
            if ($" {lower} ".Contains($" {word} ", StringComparison.Ordinal)) return word;
        }
        return null;
    }

    private static bool LooksLikeContinuation(string text) =>
        text.Contains("mention", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("add", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("also", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("continue", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("investigate", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("review", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("check", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("send", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("ask", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("use", StringComparison.OrdinalIgnoreCase);

    private static bool HasCompetingTargetMention(string text) =>
        text.Contains("another", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("different", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("instead", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsPhrase(string text, string phrase)
    {
        int start = 0;
        while ((start = text.IndexOf(phrase, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool left = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
            int end = start + phrase.Length;
            bool right = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (left && right) return true;
            start = end;
        }
        return false;
    }
}
