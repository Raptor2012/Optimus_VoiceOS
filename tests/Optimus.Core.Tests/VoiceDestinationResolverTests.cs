using Optimus.Core.Voice;
using Xunit;

namespace Optimus.Core.Tests;

public sealed class VoiceDestinationResolverTests
{
    [Theory]
    [InlineData("To Codex Project Y, fix the login test", "fix the login test")]
    [InlineData("to   CODEX   project y: fix the login test", "fix the login test")]
    [InlineData("To Codex Project Y fix the login test", "fix the login test")]
    [InlineData("To Codex Project Y — fix the login test", "fix the login test")]
    public void Resolve_ExactUniqueAliasReturnsDestinationAndStrippedPrompt(
        string transcript,
        string expectedPrompt)
    {
        var resolver = CreateResolver(new VoiceDestinationAlias("codex-y", "Codex Project Y"));

        VoiceDestinationResolution result = resolver.Resolve(transcript);

        Assert.Equal(VoiceDestinationResolutionStatus.Resolved, result.Status);
        Assert.Equal("codex-y", result.DestinationId);
        Assert.Equal(expectedPrompt, result.PromptText);
    }

    [Theory]
    [InlineData("Fix the login test")]
    [InlineData("today we should fix the login test")]
    [InlineData("")]
    [InlineData(null)]
    public void Resolve_WithoutRoutingPrefixReturnsMissingAndPreservesPrompt(string? transcript)
    {
        var resolver = CreateResolver(new VoiceDestinationAlias("codex-y", "Codex Project Y"));

        VoiceDestinationResolution result = resolver.Resolve(transcript);

        Assert.Equal(VoiceDestinationResolutionStatus.Missing, result.Status);
        Assert.Null(result.DestinationId);
        Assert.Equal(transcript?.Trim() ?? string.Empty, result.PromptText);
    }

    [Theory]
    [InlineData("To Claude Project X, fix the login test")]
    [InlineData("To Codex Project Yonder, fix the login test")]
    [InlineData("To ")]
    public void Resolve_UnconfiguredAliasReturnsUnknownWithoutRemovingText(string transcript)
    {
        var resolver = CreateResolver(new VoiceDestinationAlias("codex-y", "Codex Project Y"));

        VoiceDestinationResolution result = resolver.Resolve(transcript);

        Assert.Equal(VoiceDestinationResolutionStatus.Unknown, result.Status);
        Assert.Null(result.DestinationId);
        Assert.Equal(transcript.Trim(), result.PromptText);
    }

    [Fact]
    public void Resolve_DuplicateAliasReturnsAmbiguous()
    {
        var resolver = CreateResolver(
            new VoiceDestinationAlias("codex-y", "Codex"),
            new VoiceDestinationAlias("codex-other", "codex"));

        VoiceDestinationResolution result = resolver.Resolve("To Codex, fix the login test");

        Assert.Equal(VoiceDestinationResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.DestinationId);
    }

    [Fact]
    public void Resolve_OverlappingAliasesReturnsAmbiguousInsteadOfChoosingLongest()
    {
        var resolver = CreateResolver(
            new VoiceDestinationAlias("codex", "Codex"),
            new VoiceDestinationAlias("codex-y", "Codex Project Y"));

        VoiceDestinationResolution result = resolver.Resolve("To Codex Project Y, fix the login test");

        Assert.Equal(VoiceDestinationResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.DestinationId);
    }

    [Fact]
    public void Resolve_DoesNotTreatAWordPrefixAsAnAliasMatch()
    {
        var resolver = CreateResolver(new VoiceDestinationAlias("code", "Code"));

        VoiceDestinationResolution result = resolver.Resolve("To Codex, fix the login test");

        Assert.Equal(VoiceDestinationResolutionStatus.Unknown, result.Status);
    }

    [Fact]
    public void Resolve_AllowsAnEmptyPromptForTheCallerToValidate()
    {
        var resolver = CreateResolver(new VoiceDestinationAlias("claude-x", "Claude Project X"));

        VoiceDestinationResolution result = resolver.Resolve("To Claude Project X.");

        Assert.Equal(VoiceDestinationResolutionStatus.Resolved, result.Status);
        Assert.Equal("claude-x", result.DestinationId);
        Assert.Empty(result.PromptText);
    }

    [Fact]
    public void ConstructorRejectsInvalidAliases()
    {
        Assert.Throws<ArgumentException>(() => CreateResolver(new VoiceDestinationAlias("", "Codex")));
        Assert.Throws<ArgumentException>(() => CreateResolver(new VoiceDestinationAlias("codex", "")));
    }

    [Fact]
    public void Resolve_CasualMentionOfAppInSentenceDoesNotRoute()
    {
        var resolver = CreateResolver(new VoiceDestinationAlias("antigravity", "anti-gravity"));
        var result = resolver.Resolve("Just testing this uh in anti-gravity, do not reply anything, just send this message.");

        Assert.Equal(VoiceDestinationResolutionStatus.Missing, result.Status);
        Assert.Null(result.DestinationId);
        Assert.Equal("Just testing this uh in anti-gravity, do not reply anything, just send this message.", result.PromptText);
    }

    private static VoiceDestinationResolver CreateResolver(params VoiceDestinationAlias[] aliases) => new(aliases);
}
