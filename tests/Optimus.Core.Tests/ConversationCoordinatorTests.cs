using Optimus.Core.Conversation;
using Xunit;

namespace Optimus.Core.Tests;

public sealed class ConversationCoordinatorTests
{
    private static readonly string[] ClaudeAliases = { "Claude Desktop", "the writing assistant" };
    private static readonly string[] CodexAliases = { "ChatGPT Codex" };
    private static readonly string[] AoAliases = { "AO", "orchestrator" };
    private static readonly string[] FirstAmbiguousAliases = { "assistant" };
    private static readonly string[] SecondAmbiguousAliases = { "assistant" };

    private static ContextResolver Resolver() => new(new[]
    {
        new ContextTarget("claude", "Claude", ClaudeAliases),
        new ContextTarget("codex", "Codex", CodexAliases),
        new ContextTarget("ao", "Agent Orchestrator", AoAliases)
    });

    [Fact]
    public void ParaphrasedRequest_InvokesWithoutCommandPrefixOrConfirmation()
    {
        var coordinator = new ConversationCoordinator(Resolver());

        ConversationTurnResult result = coordinator.Process(
            "Could you have Claude investigate why tapping stops recording?", "pixel");

        Assert.Equal(ModelDecisionKind.InvokeTool, result.Decision.Kind);
        Assert.Equal("claude", result.Decision.TargetId);
        Assert.False(result.Decision.RequiresConfirmation);
        Assert.Equal("pixel", result.Turn.OriginDevice);
    }

    [Fact]
    public void PronounResolution_UsesTheActiveRequestButBrowsingDoesNotRetargetIt()
    {
        var coordinator = new ConversationCoordinator(Resolver());
        ConversationTurnResult first = coordinator.Process("Ask Claude to inspect the microphone bug.");
        coordinator.UpdateBrowsingContext("Codex", "a different conversation");

        ConversationTurnResult followUp = coordinator.Process("Mention that holding sometimes fails too.");

        Assert.Equal("claude", first.Decision.TargetId);
        Assert.Equal("claude", followUp.Decision.TargetId);
        Assert.Contains("holding sometimes fails", coordinator.PendingWork!.Objective, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Codex", coordinator.BrowsingContext.ForegroundApp);
        Assert.Equal("claude", coordinator.RequestContext.TargetId);
    }

    [Fact]
    public void AmbiguousReference_AsksInsteadOfGuessing()
    {
        var resolver = new ContextResolver(new[]
        {
            new ContextTarget("one", "One", FirstAmbiguousAliases),
            new ContextTarget("two", "Two", SecondAmbiguousAliases)
        });
        var coordinator = new ConversationCoordinator(resolver);

        ConversationTurnResult result = coordinator.Process("Ask the assistant to check this.");

        Assert.Equal(ModelDecisionKind.Clarify, result.Decision.Kind);
        Assert.Contains("One", result.Decision.Message, StringComparison.Ordinal);
        Assert.Contains("Two", result.Decision.Message, StringComparison.Ordinal);
        Assert.Null(result.Decision.TargetId);
    }

    [Fact]
    public void MissingRecipient_WaitsForRecipientRatherThanUsingForegroundSilently()
    {
        var coordinator = new ConversationCoordinator(Resolver());

        ConversationTurnResult result = coordinator.Process("Ask them to investigate the bug.");

        Assert.Equal(ModelDecisionKind.WaitForRecipient, result.Decision.Kind);
        Assert.Null(result.Decision.TargetId);
    }

    [Fact]
    public void Correction_ChangesPendingObjective_AndNewObjectiveSupersedesIt()
    {
        var coordinator = new ConversationCoordinator(Resolver());
        coordinator.Process("Ask Claude to investigate tapping.");

        ConversationTurnResult correction = coordinator.Process("Actually, investigate holding too.");
        Assert.Equal(ModelDecisionKind.InvokeTool, correction.Decision.Kind);
        Assert.Contains("tapping", correction.Decision.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("holding too", correction.Decision.Message, StringComparison.OrdinalIgnoreCase);

        ConversationTurnResult newObjective = coordinator.Process("Have Codex review the crash log.");
        Assert.Equal("codex", newObjective.Decision.TargetId);
        Assert.Equal("codex", coordinator.RequestContext.TargetId);
        Assert.DoesNotContain("tapping", coordinator.CurrentObjective!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorrectionAfterDispatch_DoesNotPretendEarlierRequestWasRedirected()
    {
        var coordinator = new ConversationCoordinator(Resolver());
        coordinator.Process("Ask Claude to investigate tapping.");
        coordinator.AcknowledgeToolInvocation();

        ConversationTurnResult result = coordinator.Process("Actually, use AO instead.");

        Assert.Equal(ModelDecisionKind.Respond, result.Decision.Kind);
        Assert.Contains("already sent", result.Decision.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Claude", result.Decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DestructiveAction_RequiresExplicitConfirmationOnlyWhenNeeded()
    {
        var coordinator = new ConversationCoordinator(Resolver());

        ConversationTurnResult result = coordinator.Process("Delete the old Claude draft.");

        Assert.Equal(ModelDecisionKind.Clarify, result.Decision.Kind);
        Assert.True(result.Decision.RequiresConfirmation);
    }

    [Fact]
    public void AgentResponse_IsSummarizedLocallyAndWaitsWithoutAutoAnswering()
    {
        var coordinator = new ConversationCoordinator(Resolver());
        AgentResponseResult result = coordinator.CaptureAgentResponse(
            "claude",
            "The fix is to keep the gesture key stable. Please choose whether to add a regression test.");

        Assert.Equal(ModelDecisionKind.WaitForRecipient, result.Decision.Kind);
        Assert.Contains("regression test", result.Decisions.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("claude", coordinator.MonitoringContext.TargetId);
        Assert.True(coordinator.MonitoringContext.AwaitingUserInstruction);
        Assert.Null(coordinator.RequestContext.TargetId);
    }

    [Fact]
    public void StoredPreferenceCanResolveAnUnqualifiedRecipient()
    {
        var coordinator = new ConversationCoordinator(
            Resolver(),
            new[] { new KeyValuePair<string, string>("preferredDestination", "codex") });

        ConversationTurnResult result = coordinator.Process("Ask it to review the current change.");

        Assert.Equal(ModelDecisionKind.InvokeTool, result.Decision.Kind);
        Assert.Equal("codex", result.Decision.TargetId);
    }
}
