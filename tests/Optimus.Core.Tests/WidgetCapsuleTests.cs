namespace Optimus.Core.Tests;

using System;
using Optimus.Shell.Models;
using Optimus.Shell.ViewModels;
using Xunit;

public class WidgetCapsuleTests
{
    [Fact]
    public void ToggleExpandedCommandTogglesState()
    {
        var vm = new WidgetViewModel();
        Assert.False(vm.IsExpanded);

        vm.ToggleExpandedCommand.Execute(null);
        Assert.True(vm.IsExpanded);

        vm.ToggleExpandedCommand.Execute(null);
        Assert.False(vm.IsExpanded);
    }

    [Fact]
    public void TogglePauseCommandTogglesPause()
    {
        var vm = new WidgetViewModel();
        Assert.False(vm.IsPaused);

        vm.TogglePauseCommand.Execute(null);
        Assert.True(vm.IsPaused);

        vm.TogglePauseCommand.Execute(null);
        Assert.False(vm.IsPaused);
    }

    [Fact]
    public void ToggleMuteCommandTogglesMuteAndFiresEvent()
    {
        var vm = new WidgetViewModel();
        bool fired = false;
        bool lastMuted = false;
        vm.NarrationMuteChanged += muted =>
        {
            fired = true;
            lastMuted = muted;
        };

        Assert.False(vm.IsSpeechMuted);

        vm.ToggleMuteCommand.Execute(null);
        Assert.True(vm.IsSpeechMuted);
        Assert.True(fired);
        Assert.True(lastMuted);

        vm.ToggleMuteCommand.Execute(null);
        Assert.False(vm.IsSpeechMuted);
        Assert.False(lastMuted);
    }

    [Fact]
    public void PendingDecisionWorkflowApprove()
    {
        var vm = new WidgetViewModel();
        Assert.False(vm.HasPendingDecision);

        var decision = new DecisionModel
        {
            Title = "Approve Plan",
            Subtitle = "Project Alpha · Step 2",
            Description = "Ready to execute implementation slice.",
            PrimaryActionLabel = "Approve & Start",
            SecondaryActionLabel = "Request Changes"
        };

        bool approved = false;
        DecisionModel? approvedDecision = null;
        vm.DecisionApproved += (s, d) =>
        {
            approved = true;
            approvedDecision = d;
        };

        vm.SetPendingDecision(decision);

        Assert.True(vm.HasPendingDecision);
        Assert.True(vm.IsExpanded);
        Assert.Equal("Approve Plan", vm.DecisionTitle);
        Assert.Equal("Project Alpha · Step 2", vm.DecisionSubtitle);
        Assert.Equal("Ready to execute implementation slice.", vm.DecisionDescription);

        vm.ApproveDecisionCommand.Execute(null);

        Assert.False(vm.HasPendingDecision);
        Assert.True(approved);
        Assert.Same(decision, approvedDecision);
        Assert.Contains("Approved: Approve Plan", vm.StatusLine);
    }

    [Fact]
    public void PendingDecisionWorkflowReject()
    {
        var vm = new WidgetViewModel();
        var decision = new DecisionModel
        {
            Title = "Agent Permission",
            Description = "Agent wants to delete temporary branch."
        };

        bool rejected = false;
        DecisionModel? rejectedDecision = null;
        vm.DecisionRejected += (s, d) =>
        {
            rejected = true;
            rejectedDecision = d;
        };

        vm.SetPendingDecision(decision);
        Assert.True(vm.HasPendingDecision);

        vm.RejectDecisionCommand.Execute(null);

        Assert.False(vm.HasPendingDecision);
        Assert.True(rejected);
        Assert.Same(decision, rejectedDecision);
        Assert.Contains("Rejected: Agent Permission", vm.StatusLine);
    }

    [Fact]
    public void ClearPendingDecisionClearsState()
    {
        var vm = new WidgetViewModel();
        vm.SetPendingDecision(new DecisionModel { Title = "Test" });
        Assert.True(vm.HasPendingDecision);

        vm.ClearPendingDecision();
        Assert.False(vm.HasPendingDecision);
    }

    [Fact]
    public void AssistantResponseCardProperties()
    {
        var vm = new WidgetViewModel();
        Assert.False(vm.HasAssistantResponse);

        vm.SetAssistantResponse("CLAUDE", "I have implemented the feature.");
        Assert.True(vm.HasAssistantResponse);
        Assert.Equal("CLAUDE", vm.AssistantResponseHeadline);
        Assert.Equal("I have implemented the feature.", vm.AssistantResponseText);

        vm.ClearAssistantResponseCommand.Execute(null);
        Assert.False(vm.HasAssistantResponse);
        Assert.Equal(string.Empty, vm.AssistantResponseText);
    }

    [Fact]
    public void NarrationStatusUpdatesAssistantResponse()
    {
        var vm = new WidgetViewModel();
        vm.NarrationStatus = "Claude: Running all unit tests";

        Assert.True(vm.HasAssistantResponse);
        Assert.Equal("CLAUDE", vm.AssistantResponseHeadline);
        Assert.Equal("Running all unit tests", vm.AssistantResponseText);
    }

    [Fact]
    public void DestinationLockingFormatsLabelWithLock()
    {
        var vm = new WidgetViewModel();
        Assert.Equal("No target", vm.ShortDestinationLabel);

        vm.LockedDestinationLabel = "Claude Code";
        vm.IsDestinationLocked = true;
        // Without selected destination, custom label is shown
        Assert.Equal("Claude Code", vm.ShortDestinationLabel);

        vm.ToggleDestinationLockCommand.Execute(null);
        Assert.False(vm.IsDestinationLocked);
    }

    [Fact]
    public void EndConversationResetsInteractionAndCollapses()
    {
        var vm = new WidgetViewModel();
        vm.IsExpanded = true;
        vm.IsPaused = true;
        vm.IsSpeechMuted = true;

        vm.EndConversationCommand.Execute(null);

        Assert.False(vm.IsExpanded);
        Assert.False(vm.IsPaused);
        Assert.False(vm.IsSpeechMuted);
    }

    [Fact]
    public void OpenInAoCommandCanExecute()
    {
        var vm = new WidgetViewModel();
        Assert.NotNull(vm.OpenInAoCommand);
        Assert.True(vm.OpenInAoCommand.CanExecute(null));
    }

    [Fact]
    public void RedictateCommandCanExecute()
    {
        var vm = new WidgetViewModel();
        Assert.NotNull(vm.RedictateCommand);
        Assert.True(vm.RedictateCommand.CanExecute(null));
    }
}
