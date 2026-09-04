namespace Optimus.Core.Tests;

using Optimus.Core.Narration;
using Optimus.Providers.Windows;
using Optimus.Shell;
using Xunit;

public sealed class AgentNarrationIntegrationTests
{
    [Theory]
    [InlineData(VisibleAgentActivity.Progress, "I am inspecting the files.", NarrationEventType.Progress)]
    [InlineData(VisibleAgentActivity.Progress, "Running tests", NarrationEventType.StatusTransition)]
    [InlineData(VisibleAgentActivity.ToolOrSkill, "Using skill search", NarrationEventType.SkillUse)]
    [InlineData(VisibleAgentActivity.ToolOrSkill, "dotnet test", NarrationEventType.ToolCall)]
    [InlineData(VisibleAgentActivity.FinalResponseCandidate, "Done.", NarrationEventType.FinalResponse)]
    [InlineData(VisibleAgentActivity.VisibleText, "Should I continue?", NarrationEventType.AgentQuestion)]
    public void VisibleAgentUpdatesMapToNarrationEvents(
        VisibleAgentActivity activity,
        string text,
        NarrationEventType expected)
    {
        var update = new VisibleAgentUpdate(text, activity, DateTimeOffset.UtcNow);

        NarrationEvent result = AgentNarrationCoordinator.ToNarrationEvent("run", update);

        Assert.Equal(expected, result.Type);
    }
}
