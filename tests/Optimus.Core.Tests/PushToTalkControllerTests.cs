namespace Optimus.Core.Tests;

using System;
using System.IO;
using Optimus.Core.Audio;
using Optimus.Core.Hotkeys;
using Xunit;

public class PushToTalkControllerTests
{
    [Fact]
    public void PressStartsCapture_ReleaseStopsCaptureAndEmitsAudio()
    {
        using var hotkey = new MockHotkeyService();
        using var audio = new InMemoryAudioCapture();
        using var controller = new PushToTalkController(hotkey, audio, TimeSpan.Zero);

        controller.Start();

        bool captureStarted = false;
        controller.StateChanged += (_, e) =>
        {
            if (e.IsCapturing)
            {
                captureStarted = true;
            }
        };

        byte[]? capturedData = null;
        controller.AudioCaptured += (_, bytes) =>
        {
            capturedData = bytes;
        };

        // User presses and holds the push-to-talk key
        hotkey.SimulatePress();
        Assert.True(controller.IsCapturing);
        Assert.True(captureStarted);

        // Simulated audio streaming into memory
        audio.AppendSyntheticAudio(8000, 440f);

        // User releases the push-to-talk key
        hotkey.SimulateRelease();
        Assert.False(controller.IsCapturing);
        Assert.NotNull(capturedData);
        Assert.Equal(8000 * sizeof(short), capturedData.Length);
        Assert.Same(capturedData, controller.LastCapturedAudio);
    }

    [Fact]
    public void ShortTap_ReportsGestureAndEmitsNoDictation()
    {
        using var hotkey = new MockHotkeyService();
        using var audio = new InMemoryAudioCapture();
        // A threshold this long makes any press in a test a tap.
        using var controller = new PushToTalkController(hotkey, audio, TimeSpan.FromMinutes(1));

        controller.Start();

        bool tapped = false;
        byte[]? capturedData = null;
        controller.Tapped += (_, _) => tapped = true;
        controller.AudioCaptured += (_, bytes) => capturedData = bytes;

        hotkey.SimulatePress();
        audio.AppendSyntheticAudio(8000, 440f);
        hotkey.SimulateRelease();

        Assert.True(tapped);
        // The fragment recorded before the key came back up must never reach the pipeline.
        Assert.Null(capturedData);
        Assert.False(controller.IsCapturing);
    }

    [Fact]
    public void Hold_EmitsDictationAndReportsNoTap()
    {
        using var hotkey = new MockHotkeyService();
        using var audio = new InMemoryAudioCapture();
        using var controller = new PushToTalkController(hotkey, audio, TimeSpan.Zero);

        controller.Start();

        bool tapped = false;
        byte[]? capturedData = null;
        controller.Tapped += (_, _) => tapped = true;
        controller.AudioCaptured += (_, bytes) => capturedData = bytes;

        hotkey.SimulatePress();
        audio.AppendSyntheticAudio(8000, 440f);
        hotkey.SimulateRelease();

        Assert.False(tapped);
        Assert.NotNull(capturedData);
    }

    [Fact]
    public void CapturePreparing_FiresBeforeTheDeviceIsClaimed()
    {
        using var hotkey = new MockHotkeyService();
        using var audio = new InMemoryAudioCapture();
        using var controller = new PushToTalkController(hotkey, audio, TimeSpan.Zero);

        controller.Start();

        bool capturingWhenAnnounced = true;
        controller.CapturePreparing += (_, _) => capturingWhenAnnounced = audio.IsCapturing;

        hotkey.SimulatePress();

        // Anything listening on the shared microphone gets its chance to release it first.
        Assert.False(capturingWhenAnnounced);
        Assert.True(audio.IsCapturing);
    }

    [Fact]
    public void HotkeyRegistrationFailure_TriggersErrorEventAndLeavesControllerReady()
    {
        using var hotkey = new MockHotkeyService();
        using var audio = new InMemoryAudioCapture();
        using var controller = new PushToTalkController(hotkey, audio, TimeSpan.Zero);

        string? errorReceived = null;
        controller.ErrorOccurred += (_, e) =>
        {
            errorReceived = e.Message;
        };

        controller.Start();
        hotkey.SimulateRegistrationFailure("Conflict with another application");

        Assert.NotNull(errorReceived);
        Assert.Contains("Conflict", errorReceived);
        Assert.False(controller.IsCapturing);
    }

    [Fact]
    public void MicrophoneFailureOnStart_EmitsErrorAndRecoversGracefully()
    {
        using var hotkey = new MockHotkeyService();
        using var audio = new InMemoryAudioCapture { SimulateFailureOnStart = true };
        using var controller = new PushToTalkController(hotkey, audio, TimeSpan.Zero);

        string? errorReceived = null;
        controller.ErrorOccurred += (_, e) =>
        {
            errorReceived = e.Message;
        };

        controller.Start();
        hotkey.SimulatePress();

        Assert.NotNull(errorReceived);
        Assert.Contains("Simulated microphone unavailable", errorReceived);
        Assert.False(controller.IsCapturing);

        // Recover: reset failure flag and test normal capture works
        audio.SimulateFailureOnStart = false;
        hotkey.SimulatePress();
        Assert.True(controller.IsCapturing);

        hotkey.SimulateRelease();
        Assert.False(controller.IsCapturing);
    }

    [Fact]
    public void PushToTalk_WritesNoFilesToDisk()
    {
        string tempDir = Path.GetTempPath();
        var filesBefore = Directory.GetFiles(tempDir);

        using var hotkey = new MockHotkeyService();
        using var audio = new InMemoryAudioCapture();
        using var controller = new PushToTalkController(hotkey, audio, TimeSpan.Zero);

        controller.Start();
        hotkey.SimulatePress();
        audio.AppendSyntheticAudio(16000, 440f);
        hotkey.SimulateRelease();

        var filesAfter = Directory.GetFiles(tempDir);

        // Check that no new files matching audio formats or optimus were created
        var newFiles = Array.FindAll(filesAfter, f =>
            !Array.Exists(filesBefore, b => string.Equals(b, f, StringComparison.OrdinalIgnoreCase)) &&
            (f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
             f.EndsWith(".pcm", StringComparison.OrdinalIgnoreCase) ||
             f.EndsWith(".raw", StringComparison.OrdinalIgnoreCase) ||
             f.Contains("optimus", StringComparison.OrdinalIgnoreCase)));

        Assert.Empty(newFiles);
    }
}
