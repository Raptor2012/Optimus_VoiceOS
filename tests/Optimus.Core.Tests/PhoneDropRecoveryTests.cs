namespace Optimus.Core.Tests;

using System;
using Optimus.Core.Audio;
using Optimus.Core.Hotkeys;
using Optimus.Shell.Models;
using Optimus.Shell.ViewModels;
using Xunit;

/// <summary>
/// A phone that announces a capture and then disappears must not leave the widget waiting on it.
/// Found by dogfooding: the widget sat in "Listening on Pixel..." with no route back.
/// </summary>
public class PhoneDropRecoveryTests
{
    [Fact]
    public void PhoneDropDuringCapture_ReturnsTheWidgetToIdle()
    {
        using var viewModel = new WidgetViewModel(action => action());

        viewModel.PhoneCaptureBeginning();
        Assert.Equal(WidgetState.Listening, viewModel.State);

        viewModel.PhoneCaptureAbandoned();

        Assert.Equal(WidgetState.Idle, viewModel.State);
        Assert.Contains("Phone disconnected", viewModel.StatusLine, StringComparison.Ordinal);
    }

    [Fact]
    public void PhoneDropWithADraftShowing_KeepsTheDraftForConfirmation()
    {
        using var viewModel = new WidgetViewModel(action => action())
        {
            DraftText = "Add a retry to fetchUser."
        };

        viewModel.PhoneCaptureBeginning();
        viewModel.PhoneCaptureAbandoned();

        // The draft survives: losing the phone is not a reason to discard captured work.
        Assert.Equal(WidgetState.Confirm, viewModel.State);
        Assert.Equal("Add a retry to fetchUser.", viewModel.DraftText);
    }

    [Fact]
    public void PhoneDropWhileHoldingOnThisPc_LeavesTheHoldAlone()
    {
        using var hotkey = new MockHotkeyService();
        using var capture = new InMemoryAudioCapture();
        using var controller = new PushToTalkController(hotkey, capture, TimeSpan.Zero);
        using var viewModel = new WidgetViewModel(action => action());
        viewModel.AttachController(controller);
        controller.Start();

        viewModel.PhoneCaptureBeginning();

        // The user is mid-sentence on the PC when the phone drops.
        hotkey.SimulatePress();
        viewModel.PhoneCaptureAbandoned();

        Assert.Equal(WidgetState.Listening, viewModel.State);
        Assert.True(capture.IsCapturing);
    }
}
