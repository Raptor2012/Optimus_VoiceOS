namespace Optimus.Core.Voice;

public sealed record VoiceDestinationAlias(string DestinationId, string Alias);

public enum VoiceDestinationResolutionStatus
{
    Resolved,
    Missing,
    Unknown,
    Ambiguous,
}

public sealed record VoiceDestinationResolution(
    VoiceDestinationResolutionStatus Status,
    string? DestinationId,
    string PromptText);

/// <summary>
/// Resolves an explicit leading "to &lt;alias&gt;" phrase. It never chooses among multiple matches.
/// </summary>
public sealed class VoiceDestinationResolver
{
    private readonly VoiceDestinationAlias[] _aliases;

    public VoiceDestinationResolver(IEnumerable<VoiceDestinationAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        _aliases = aliases.ToArray();

        foreach (VoiceDestinationAlias alias in _aliases)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(alias.DestinationId);
            ArgumentException.ThrowIfNullOrWhiteSpace(alias.Alias);
        }
    }

    public VoiceDestinationResolution Resolve(string? transcript)
    {
        string source = transcript?.Trim() ?? string.Empty;
        if (!TryConsumeToPrefix(source, out int aliasStart))
        {
            return new(VoiceDestinationResolutionStatus.Missing, null, source);
        }

        var matches = new List<(VoiceDestinationAlias Alias, int End)>();
        foreach (VoiceDestinationAlias alias in _aliases)
        {
            if (TryMatchAlias(source, aliasStart, alias.Alias, out int end))
            {
                matches.Add((alias, end));
            }
        }

        if (matches.Count == 0)
        {
            return new(VoiceDestinationResolutionStatus.Unknown, null, source);
        }

        if (matches.Count != 1)
        {
            return new(VoiceDestinationResolutionStatus.Ambiguous, null, source);
        }

        (VoiceDestinationAlias destination, int aliasEnd) = matches[0];
        int promptStart = SkipPromptSeparator(source, aliasEnd);
        return new(
            VoiceDestinationResolutionStatus.Resolved,
            destination.DestinationId,
            source[promptStart..].Trim());
    }

    private static bool TryConsumeToPrefix(string source, out int aliasStart)
    {
        aliasStart = 0;
        if (source.Length < 2 ||
            !source.AsSpan(0, 2).Equals("to", StringComparison.OrdinalIgnoreCase) ||
            (source.Length > 2 && !char.IsWhiteSpace(source[2])))
        {
            return false;
        }

        aliasStart = 2;
        while (aliasStart < source.Length && char.IsWhiteSpace(source[aliasStart]))
        {
            aliasStart++;
        }

        return true;
    }

    private static bool TryMatchAlias(string source, int sourceIndex, string alias, out int end)
    {
        int aliasIndex = 0;
        while (aliasIndex < alias.Length)
        {
            if (char.IsWhiteSpace(alias[aliasIndex]))
            {
                while (aliasIndex < alias.Length && char.IsWhiteSpace(alias[aliasIndex]))
                {
                    aliasIndex++;
                }

                if (sourceIndex >= source.Length || !char.IsWhiteSpace(source[sourceIndex]))
                {
                    end = sourceIndex;
                    return false;
                }

                while (sourceIndex < source.Length && char.IsWhiteSpace(source[sourceIndex]))
                {
                    sourceIndex++;
                }

                continue;
            }

            if (sourceIndex >= source.Length ||
                char.ToUpperInvariant(source[sourceIndex]) != char.ToUpperInvariant(alias[aliasIndex]))
            {
                end = sourceIndex;
                return false;
            }

            sourceIndex++;
            aliasIndex++;
        }

        end = sourceIndex;
        return sourceIndex == source.Length || IsAliasBoundary(source[sourceIndex]);
    }

    private static bool IsAliasBoundary(char value) =>
        char.IsWhiteSpace(value) || char.IsPunctuation(value);

    private static int SkipPromptSeparator(string source, int index)
    {
        while (index < source.Length && char.IsWhiteSpace(source[index]))
        {
            index++;
        }

        if (index < source.Length && char.IsPunctuation(source[index]))
        {
            index++;
        }

        while (index < source.Length && char.IsWhiteSpace(source[index]))
        {
            index++;
        }

        return index;
    }
}
