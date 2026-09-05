namespace Optimus.Core.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Audio;
using Optimus.Core.Hotkeys;
using Optimus.Core.Voice;
using Optimus.Shell.Models;
using Optimus.Shell.ViewModels;
using Xunit;

/// <summary>
/// Covers the listening half of a continuous session: an open session must return speech, must
/// give up quietly when nobody talks, and must never hold the microphone against a hotkey hold.
/// </summary>
public class ContinuousSessionTests
{
    private static OneShotApprovalListener CreateListener(IAudioCaptureService capture) =>
        new(
            capture,
            sessionSilenceDuration: TimeSpan.FromMilliseconds(60),
            sessionWindow: TimeSpan.FromMilliseconds(400));

    [Fact]
    public async Task SessionUtterance_ReturnsSpeechAfterTheTalkerStops()
    {
        using var capture = new InMemoryAudioCapture();
        using var listener = CreateListener(capture);

        Task<byte[]> listening = listener.ListenForSessionUtteranceAsync();

        await WaitUntilAsync(() => capture.IsCapturing);
        capture.AppendSyntheticAudio(1600);
        for (int i = 0; i < 12; i++)
        {
            capture.AppendSilence(160);
            await Task.Delay(20);
        }

        byte[] audio = await listening.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(audio);
        Assert.False(capture.IsCapturing);
    }

    [Fact]
    public async Task QuietWindow_ReturnsNothingSoIdleAudioIsNeverRetained()
    {
        using var capture = new InMemoryAudioCapture();
        using var listener = CreateListener(capture);

        Task<byte[]> listening = listener.ListenForSessionUtteranceAsync();

        await WaitUntilAsync(() => capture.IsCapturing);
        for (int i = 0; i < 40; i++)
        {
            capture.AppendSilence(160);
            await Task.Delay(20);
        }

        byte[] audio = await listening.WaitAsync(TimeSpan.FromSeconds(5));

        // The window closes empty rather than accumulating an unbounded recording of the room.
        Assert.Empty(audio);
        Assert.False(capture.IsCapturing);
    }

    [Fact]
    public async Task CancellingForAHold_LeavesTheMicrophoneToTheHold()
    {
        using var capture = new InMemoryAudioCapture();
        using var listener = CreateListener(capture);

        Task<byte[]> listening = listener.ListenForSessionUtteranceAsync();
        await WaitUntilAsync(() => capture.IsCapturing);

        // This is what a hotkey press does: the session releases the device first.
        listener.Cancel();
        byte[] abandoned = await listening.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(abandoned);

        // The hold now owns the microphone, and the abandoned listen must not close it.
        capture.StartCapture();
        capture.AppendSyntheticAudio(1600);
        await Task.Delay(150);

        Assert.True(capture.IsCapturing);
        Assert.NotEmpty(capture.StopCapture());
    }

    [Fact]
    public void SessionOpeningTheMicrophone_IsNotReportedAsAHeldKey()
    {
        using var hotkey = new MockHotkeyService();
        using var capture = new InMemoryAudioCapture();
        using var controller = new PushToTalkController(hotkey, capture, TimeSpan.Zero);
        using var viewModel = new WidgetViewModel(action => action());
        viewModel.AttachController(controller);
        controller.Start();

        // Session listening shares the device, so starting it raises the same capture event a
        // hold does. Announcing it as a hold parked the widget in a state the session refuses to
        // act from, and nothing released it again.
        capture.StartCapture();

        Assert.NotEqual(WidgetState.Listening, viewModel.State);
        Assert.DoesNotContain("release key", viewModel.StatusLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActualHold_IsStillReportedAsAHeldKey()
    {
        using var hotkey = new MockHotkeyService();
        using var capture = new InMemoryAudioCapture();
        using var controller = new PushToTalkController(hotkey, capture, TimeSpan.Zero);
        using var viewModel = new WidgetViewModel(action => action());
        viewModel.AttachController(controller);
        controller.Start();

        hotkey.SimulatePress();

        Assert.Equal(WidgetState.Listening, viewModel.State);
        Assert.Contains("release key", viewModel.StatusLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BargeIn_IsOffUntilTheUserAsksForIt()
    {
        using var viewModel = new WidgetViewModel(action => action());

        // Interrupting needs the microphone open while the tool talks, which only holds up on
        // headphones. Defaulting it on would make the tool interrupt itself through speakers.
        Assert.False(viewModel.BargeInEnabled);

        viewModel.BargeInEnabled = true;
        Assert.True(viewModel.BargeInEnabled);
    }

    [Fact]
    public void InterruptCommands_ToggleBargeIn()
    {
        Assert.Equal("interruptOn", ConversationCommand.Parse("interrupt on")?.Kind);
        Assert.Equal("interruptOn", ConversationCommand.Parse("let me interrupt")?.Kind);
        Assert.Equal("interruptOff", ConversationCommand.Parse("interrupt off")?.Kind);
        Assert.Equal("interruptOff", ConversationCommand.Parse("stop interrupting")?.Kind);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
