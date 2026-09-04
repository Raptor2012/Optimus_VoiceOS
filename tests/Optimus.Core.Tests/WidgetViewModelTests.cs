namespace Optimus.Core.Tests;

using System;
using Optimus.Core.Audio;
using Optimus.Core.Hotkeys;
using Optimus.Shell.Models;
using Optimus.Shell.ViewModels;
using Xunit;

public class WidgetViewModelTests
{
    [Fact]
    public void InitialState_IsIdleAndShowsDefaultHotkey()
    {
        using var vm = new WidgetViewModel(action => action());

        Assert.Equal(WidgetState.Idle, vm.State);
        Assert.True(vm.IsIdle);
        Assert.False(vm.IsListening);
        Assert.False(vm.IsError);
        Assert.Contains("F8", vm.StatusLine);
    }

    [Fact]
    public void HotkeyPress_TransitionsStateToListening()
    {
        using var hotkey = new MockHotkeyService();
        using var audio = new InMemoryAudioCapture();
        using var controller = new PushToTalkController(hotkey, audio);
        using var vm = new WidgetViewModel(action => action());

        vm.AttachController(controller);
        controller.Start();

        hotkey.SimulatePress();

        Assert.Equal(WidgetState.Listening, vm.State);
        Assert.True(vm.IsListening);
        Assert.Contains("Listening", vm.StatusLine);
    }

    [Fact]
    public void HotkeyRelease_ReturnsToIdleAndDisplaysStats()
    {
        using var hotkey = new MockHotkeyService();
        using var audio = new InMemoryAudioCapture();
        using var controller = new PushToTalkController(hotkey, audio);
        using var vm = new WidgetViewModel(action => action());

        vm.AttachController(controller);
        controller.Start();

        hotkey.SimulatePress();
        audio.AppendSyntheticAudio(16000, 440f); // 1 second of 16kHz audio = 32000 bytes
        hotkey.SimulateRelease();

        Assert.Equal(WidgetState.Idle, vm.State);
        Assert.True(vm.IsIdle);
        Assert.Contains("1.0s", vm.StatusLine);
        Assert.Contains("KB in memory", vm.StatusLine);
    }

    [Fact]
    public void CaptureError_TransitionsToErrorStateAndCanBeDismissed()
    {
        using var hotkey = new MockHotkeyService();
        using var audio = new InMemoryAudioCapture { SimulateFailureOnStart = true };
        using var controller = new PushToTalkController(hotkey, audio);
        using var vm = new WidgetViewModel(action => action());

        vm.AttachController(controller);
        controller.Start();

        hotkey.SimulatePress();

        Assert.Equal(WidgetState.Error, vm.State);
        Assert.True(vm.IsError);
        Assert.Contains("unavailable", vm.ErrorMessage);

        // Dismiss the error
        vm.DismissErrorCommand.Execute(null);

        Assert.Equal(WidgetState.Idle, vm.State);
        Assert.True(vm.IsIdle);
        Assert.Empty(vm.ErrorMessage);
    }

    [Fact]
    public void CancelCommand_ResetsDraftAndReturnsToIdle()
    {
        using var vm = new WidgetViewModel(action => action());

        vm.State = WidgetState.Confirm;
        vm.DraftText = "Some draft text";

        vm.CancelCommand.Execute(null);

        Assert.Equal(WidgetState.Idle, vm.State);
        Assert.Empty(vm.DraftText);
        Assert.Contains("Cancelled", vm.StatusLine);
    }

    [Fact]
    public void LoadManualDraft_UsesNormalConfirmationFlowWithoutChoosingDestination()
    {
        using var vm = new WidgetViewModel(action => action());

        vm.LoadManualDraft("Run the focused tests.");

        Assert.Equal(WidgetState.Confirm, vm.State);
        Assert.Equal("Run the focused tests.", vm.RawTranscript);
        Assert.Equal("Run the focused tests.", vm.DraftText);
        Assert.True(vm.IsDraftVisible);
        Assert.True(vm.IsConfirmPanelVisible);
        Assert.Null(vm.SelectedDestination);
        Assert.Contains("choose a destination", vm.StatusLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadManualDraft_RejectsEmptyText()
    {
        using var vm = new WidgetViewModel(action => action());

        Assert.Throws<ArgumentException>(() => vm.LoadManualDraft("   "));
    }

    [Theory]
    [InlineData(WidgetState.Idle, "#30D158")]
    [InlineData(WidgetState.Listening, "#FF3B30")]
    [InlineData(WidgetState.Processing, "#FF9500")]
    [InlineData(WidgetState.Confirm, "#0A84FF")]
    [InlineData(WidgetState.Error, "#FF453A")]
    public void StatusBadgeColor_MatchesState(WidgetState state, string expectedHex)
    {
        using var vm = new WidgetViewModel(action => action()) { State = state };
        Assert.Equal(expectedHex, vm.StatusBadgeColor);
    }
}
