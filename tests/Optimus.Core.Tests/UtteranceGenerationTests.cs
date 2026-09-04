namespace Optimus.Core.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Audio;
using Optimus.Core.Hotkeys;
using Optimus.Inference;
using Optimus.Shell.Models;
using Optimus.Shell.ViewModels;
using Xunit;

/// <summary>
/// A slow utterance must never overwrite the draft of a newer one.
/// </summary>
public class UtteranceGenerationTests
{
    /// <summary>One second of 16 kHz mono PCM16.</summary>
    private static byte[] Audio(int bytes = 32000) => new byte[bytes];

    private static readonly string[] TwoUtterances = { "FIRST", "SECOND" };
    private static readonly string[] OneUtterance = { "STALE" };

    /// <summary>
    /// The core race: utterance A is slow, B is fast. B finishes first and is shown. When A
    /// finally returns it must be discarded, not painted over B.
    /// </summary>
    [Fact]
    public async Task SlowFirstUtterance_DoesNotOverwriteFasterSecond()
    {
        var gateA = new SemaphoreSlim(0, 1);
        var transcriber = new ScriptedTranscriber(new Queue<string>(TwoUtterances));
        var cleaner = new ScriptedCleaner
        {
            // Only the first call blocks, simulating a slow cleanup.
            BeforeReturn = async call =>
            {
                if (call == 1)
                {
                    await gateA.WaitAsync();
                }
            }
        };

        using var pipeline = new VoicePipeline(transcriber, cleaner);
        var (viewModel, controller, capture, hotkey) = BuildViewModel();

        try
        {
            viewModel.AttachPipeline(pipeline);

            // Utterance A starts and stalls inside cleanup.
            Utter(hotkey, capture, Audio());
            await WaitUntil(() => cleaner.Calls >= 1);

            // Utterance B starts and completes.
            Utter(hotkey, capture, Audio());
            await WaitUntil(() => viewModel.DraftText == "SECOND/clean");
            Assert.Equal("SECOND", viewModel.RawTranscript);

            // Now let A finish. It is superseded and must be dropped.
            gateA.Release();
            await Task.Delay(300);

            Assert.Equal("SECOND/clean", viewModel.DraftText);
            Assert.Equal("SECOND", viewModel.RawTranscript);
            Assert.Equal(WidgetState.Confirm, viewModel.State);
        }
        finally
        {
            gateA.Dispose();
            viewModel.Dispose();
            controller.Dispose();
            capture.Dispose();
            hotkey.Dispose();
        }
    }

    /// <summary>A result arriving after Cancel must not resurrect the draft.</summary>
    [Fact]
    public async Task ResultArrivingAfterCancel_IsDiscarded()
    {
        var gate = new SemaphoreSlim(0, 1);
        var transcriber = new ScriptedTranscriber(new Queue<string>(OneUtterance));
        var cleaner = new ScriptedCleaner { BeforeReturn = _ => gate.WaitAsync() };

        using var pipeline = new VoicePipeline(transcriber, cleaner);
        var (viewModel, controller, capture, hotkey) = BuildViewModel();

        try
        {
            viewModel.AttachPipeline(pipeline);

            Utter(hotkey, capture, Audio());
            await WaitUntil(() => cleaner.Calls >= 1);

            viewModel.Cancel();
            Assert.Equal(WidgetState.Idle, viewModel.State);

            gate.Release();
            await Task.Delay(300);

            Assert.Equal(string.Empty, viewModel.DraftText);
            Assert.Equal(string.Empty, viewModel.RawTranscript);
            Assert.Equal(WidgetState.Idle, viewModel.State);
        }
        finally
        {
            gate.Dispose();
            viewModel.Dispose();
            controller.Dispose();
            capture.Dispose();
            hotkey.Dispose();
        }
    }

    /// <summary>A failure from a superseded utterance must not raise an error over the live one.</summary>
    [Fact]
    public async Task FailureFromSupersededUtterance_DoesNotShowError()
    {
        var gateA = new SemaphoreSlim(0, 1);
        var transcriber = new ScriptedTranscriber(new Queue<string>(TwoUtterances));
        var cleaner = new ScriptedCleaner
        {
            BeforeReturn = async call =>
            {
                if (call == 1)
                {
                    await gateA.WaitAsync();
                    throw new InvalidOperationException("stale failure");
                }
            }
        };

        using var pipeline = new VoicePipeline(transcriber, cleaner);
        var (viewModel, controller, capture, hotkey) = BuildViewModel();

        try
        {
            viewModel.AttachPipeline(pipeline);

            Utter(hotkey, capture, Audio());
            await WaitUntil(() => cleaner.Calls >= 1);

            Utter(hotkey, capture, Audio());
            await WaitUntil(() => viewModel.DraftText == "SECOND/clean");

            gateA.Release();
            await Task.Delay(300);

            Assert.NotEqual(WidgetState.Error, viewModel.State);
            Assert.Equal(string.Empty, viewModel.ErrorMessage);
            Assert.Equal("SECOND/clean", viewModel.DraftText);
        }
        finally
        {
            gateA.Dispose();
            viewModel.Dispose();
            controller.Dispose();
            capture.Dispose();
            hotkey.Dispose();
        }
    }

    /// <summary>One complete push-to-talk cycle, through the real controller.</summary>
    private static void Utter(MockHotkeyService hotkey, FakeCapture capture, byte[] audio)
    {
        capture.NextBuffer = audio;
        hotkey.SimulatePress();
        hotkey.SimulateRelease();
    }

    private static (WidgetViewModel, PushToTalkController, FakeCapture, MockHotkeyService) BuildViewModel()
    {
        var capture = new FakeCapture();
        var hotkey = new MockHotkeyService();
        var controller = new PushToTalkController(hotkey, capture);
        controller.Start();

        // Run dispatched work inline so the test observes deterministic ordering.
        var viewModel = new WidgetViewModel(action => action());
        viewModel.AttachController(controller);
        return (viewModel, controller, capture, hotkey);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(15);
        }

        throw new TimeoutException("Condition was not met in time.");
    }

    private sealed class FakeCapture : IAudioCaptureService
    {
        public bool IsCapturing { get; private set; }

#pragma warning disable CS0067 // Required by IAudioCaptureService; this fake never raises it.
        public event EventHandler<CaptureErrorEventArgs>? ErrorOccurred;
#pragma warning restore CS0067

        public event EventHandler<CaptureStateChangedEventArgs>? StateChanged;

        public void StartCapture()
        {
            IsCapturing = true;
            StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(true));
        }

        public byte[] StopCapture()
        {
            IsCapturing = false;
            StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(false));
            return NextBuffer;
        }

        /// <summary>The buffer the next StopCapture returns.</summary>
        public byte[] NextBuffer { get; set; } = Array.Empty<byte>();

        public void Dispose() => StateChanged = null;
    }

    private sealed class ScriptedTranscriber : ISpeechTranscriber
    {
        private readonly Queue<string> _results;

        public ScriptedTranscriber(Queue<string> results) => _results = results;

        public bool IsLoaded => true;

        public void EnsureLoaded()
        {
        }

        public TranscriptionResult Transcribe(byte[] pcm16Mono16k, CancellationToken cancellationToken = default)
        {
            lock (_results)
            {
                string text = _results.Count > 0 ? _results.Dequeue() : "EXTRA";
                return new TranscriptionResult(text, 5, 1.0);
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedCleaner : IPromptCleaner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Func<int, Task>? BeforeReturn { get; set; }

        public bool IsLoaded => true;

        public void EnsureLoaded()
        {
        }

        public Task PrimeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<CleanupResult> CleanAsync(string rawTranscript, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);

            if (BeforeReturn != null)
            {
                await BeforeReturn(call);
            }

            return new CleanupResult($"{rawTranscript}/clean", 5, Applied: true);
        }

        public void Dispose()
        {
        }
    }
}
