namespace Optimus.Core.Tests;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Audio;
using Optimus.Core.Hotkeys;
using Optimus.Core.Speech;
using Optimus.Core.Voice;
using Optimus.Inference;
using Optimus.Providers;
using Optimus.Providers.Windows;
using Optimus.Shell.Models;
using Optimus.Shell.ViewModels;
using Xunit;

public class SpokenApprovalIntegrationTests
{
    private static byte[] MakeAudio(string text) => Encoding.UTF8.GetBytes(text);

    private static (
        WidgetViewModel Widget,
        RecordingAdapter Claude,
        RecordingAdapter Codex,
        MockHotkeyService Hotkey,
        FakeTranscriber Transcriber,
        FakeCleaner Cleaner,
        FakeReviewPlayer Speech,
        FakeApprovalListener ApprovalListener) CreateContext()
    {
        var claude = new RecordingAdapter("claude", "Claude", ready: true);
        var codex = new RecordingAdapter("codex", "Codex", ready: true);
        var registry = new DestinationRegistry(new IDestinationAdapter[] { claude, codex });

        var hotkey = new MockHotkeyService();
        var capture = new InMemoryAudioCapture();
        var controller = new PushToTalkController(hotkey, capture);

        var transcriber = new FakeTranscriber();
        var cleaner = new FakeCleaner();
        var pipeline = new VoicePipeline(transcriber, cleaner);

        var speech = new FakeReviewPlayer();
        var approvalListener = new FakeApprovalListener();

        var widget = new WidgetViewModel(action => action());
        widget.AttachController(controller);
        widget.AttachDestinations(registry);
        widget.AttachPipeline(pipeline);
        widget.AttachSpeech(speech);
        widget.AttachApprovalListener(approvalListener);

        controller.Start();

        return (widget, claude, codex, hotkey, transcriber, cleaner, speech, approvalListener);
    }

    /// <summary>
    /// Requirement 1: TTS completion alone sends nothing and transitions to AwaitingApproval.
    /// </summary>
    [Fact]
    public async Task TtsCompletionAloneSendsNothing()
    {
        var (widget, claude, _, _, _, _, speech, approvalListener) = CreateContext();
        using (widget)
        {
            // Prepare a pending approval gate that has not returned yet
            var approvalGate = approvalListener.PrepareApprovalGate();

            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("Add a retry to fetchUser in api.ts.");

            await speech.WaitUntilSpoken();
            await Task.Delay(80);

            Assert.Empty(claude.Sent);
            Assert.Equal(WidgetState.AwaitingApproval, widget.State);
            Assert.True(approvalListener.ListenApprovalCalls >= 1);
            Assert.Contains("Send this to Claude, or redictate?", widget.StatusLine);

            // Clean up gate
            approvalGate.TrySetCanceled();
        }
    }

    /// <summary>
    /// Requirement 2: Affirmative command sends exactly once (idempotent / guarded against double dispatch).
    /// </summary>
    [Fact]
    public async Task AffirmativeSendsOnce()
    {
        var (widget, claude, _, _, _, _, speech, approvalListener) = CreateContext();
        using (widget)
        {
            approvalListener.EnqueueApproval(MakeAudio("send"));

            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("Add a retry to fetchUser in api.ts.");

            await speech.WaitUntilSpoken();
            await Task.Delay(100);

            Assert.Single(claude.Sent);
            Assert.Equal("Add a retry to fetchUser in api.ts.", claude.Sent[0]);
            Assert.Equal(WidgetState.Sent, widget.State);

            // Verify a concurrent / duplicate send attempt does not send again
            await widget.ConfirmAsync();
            Assert.Single(claude.Sent);
        }
    }

    /// <summary>
    /// Requirement 3: Every affirmative variant works ("yes", "yeah", "yep", "confirm", "send", "send it", "go ahead", "do it").
    /// </summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("yeah")]
    [InlineData("yep")]
    [InlineData("confirm")]
    [InlineData("send")]
    [InlineData("send it")]
    [InlineData("go ahead")]
    [InlineData("do it")]
    public async Task EveryAffirmativeVariantWorks(string variant)
    {
        var (widget, claude, _, _, _, _, speech, approvalListener) = CreateContext();
        using (widget)
        {
            approvalListener.EnqueueApproval(MakeAudio(variant));

            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft($"Draft for {variant}.");

            await speech.WaitUntilSpoken();
            await Task.Delay(100);

            Assert.Single(claude.Sent);
            Assert.Equal($"Draft for {variant}.", claude.Sent[0]);
            Assert.Equal(WidgetState.Sent, widget.State);
            Assert.Contains("Claude", widget.StatusLine);
        }
    }

    /// <summary>
    /// Requirement 4: Unknown speech sends nothing; reprompts and falls back to Confirm after retries exhausted.
    /// </summary>
    [Fact]
    public async Task UnknownSpeechSendsNothingAndFallsBackToConfirm()
    {
        var (widget, claude, _, hotkey, _, _, speech, approvalListener) = CreateContext();
        using (widget)
        {
            // 3 attempts total (initial + 2 retries)
            approvalListener.EnqueueApproval(MakeAudio("what is the weather"));
            approvalListener.EnqueueApproval(MakeAudio("play some music"));
            approvalListener.EnqueueApproval(MakeAudio("banana"));

            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("Prompt that will not be approved.");

            await speech.WaitUntilSpoken();
            await Task.Delay(150);

            Assert.Empty(claude.Sent);
            Assert.Equal(WidgetState.Confirm, widget.State);
            Assert.Equal("Review the draft, then confirm", widget.StatusLine);
            Assert.True(hotkey.IsHooked);
        }
    }

    /// <summary>
    /// Requirement 5: Redictate fully replaces the old draft with a new utterance.
    /// </summary>
    [Fact]
    public async Task RedictateFullyReplacesTheOldDraft()
    {
        var (widget, claude, _, _, _, _, speech, approvalListener) = CreateContext();
        using (widget)
        {
            approvalListener.EnqueueApproval(MakeAudio("redictate"));
            approvalListener.EnqueueDictation(MakeAudio("Completely new replacement draft."));
            // Also approve the new draft
            approvalListener.EnqueueApproval(MakeAudio("send"));

            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("Old obsolete draft.");

            // First spoken review for old draft
            await speech.WaitUntilSpoken();
            await Task.Delay(150);

            // Second spoken review for replacement draft
            await speech.WaitUntilSpoken();
            await Task.Delay(100);

            Assert.Single(claude.Sent);
            Assert.Equal("Completely new replacement draft.", claude.Sent[0]);
            Assert.Equal("Completely new replacement draft.", widget.DraftText);
        }
    }

    /// <summary>
    /// Requirement 6: Cancel sends nothing and discards the draft.
    /// </summary>
    [Theory]
    [InlineData("cancel")]
    [InlineData("no")]
    [InlineData("stop")]
    [InlineData("abort")]
    [InlineData("scratch that")]
    [InlineData("never mind")]
    public async Task CancelSendsNothing(string cancelVariant)
    {
        var (widget, claude, _, hotkey, _, _, speech, approvalListener) = CreateContext();
        using (widget)
        {
            approvalListener.EnqueueApproval(MakeAudio(cancelVariant));

            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("Draft to be cancelled.");

            await speech.WaitUntilSpoken();
            await Task.Delay(100);

            Assert.Empty(claude.Sent);
            Assert.Equal(WidgetState.Idle, widget.State);
            Assert.Empty(widget.DraftText);
            Assert.Contains("Cancelled", widget.StatusLine);
            Assert.True(hotkey.IsHooked);
        }
    }

    /// <summary>
    /// Requirement 7: Stale approval results from previous generation are ignored.
    /// </summary>
    [Fact]
    public async Task StaleApprovalResultsAreIgnored()
    {
        var (widget, claude, _, _, _, _, speech, approvalListener) = CreateContext();
        using (widget)
        {
            var delayedApproval = approvalListener.PrepareApprovalGate();

            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("First draft.");

            await speech.WaitUntilSpoken();
            await Task.Delay(50);

            // User cancels before the delayed approval resolves
            widget.Cancel();
            Assert.Equal(WidgetState.Idle, widget.State);

            // Now the stale approval resolves as "send"
            delayedApproval.TrySetResult(MakeAudio("send"));
            await Task.Delay(80);

            Assert.Empty(claude.Sent);
            Assert.Equal(WidgetState.Idle, widget.State);
        }
    }

    /// <summary>
    /// Requirement 8: Edited text is reread before sending.
    /// </summary>
    [Fact]
    public async Task EditedTextIsRereadBeforeSending()
    {
        var (widget, claude, _, _, _, _, speech, approvalListener) = CreateContext();
        using (widget)
        {
            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("Original draft.");

            await speech.WaitUntilSpoken();
            await Task.Delay(50);

            // User edits the text manually
            widget.DraftText = "Edited draft text.";

            // Attempting to send before reread completes must be blocked
            await widget.ConfirmAsync();
            Assert.Empty(claude.Sent);

            // The reread occurs
            await speech.WaitUntilSpoken();
            Assert.Equal("Edited draft text.", speech.LastDraft);

            // Now approval can confirm and send the edited draft
            approvalListener.EnqueueApproval(MakeAudio("send"));
            await widget.ConfirmAsync();

            Assert.Single(claude.Sent);
            Assert.Equal("Edited draft text.", claude.Sent[0]);
        }
    }

    /// <summary>
    /// Requirement 9: Destination prefixes resolve only uniquely and strip prefix before cleanup.
    /// </summary>
    [Fact]
    public async Task DestinationPrefixesResolveOnlyUniquely()
    {
        var (widget, claude, codex, _, _, _, speech, approvalListener) = CreateContext();
        using (widget)
        {
            // 1. Spoken "Claude, write a sorting function"
            approvalListener.EnqueueApproval(MakeAudio("send"));
            widget.SelectedDestination = null; // Clear destination

            var captureService = (InMemoryAudioCapture)widget.GetType()
                .GetField("_controller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(widget)!.GetType().GetProperty("AudioCaptureService")!.GetValue(
                    widget.GetType().GetField("_controller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(widget))!;

            var hotkey = (MockHotkeyService)widget.GetType()
                .GetField("_controller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(widget)!.GetType().GetField("_hotkeyService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(
                    widget.GetType().GetField("_controller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(widget))!;

            hotkey.SimulatePress();
            captureService.AppendAudio(MakeAudio("Claude, write a sorting function"));
            hotkey.SimulateRelease();

            await speech.WaitUntilSpoken();
            await Task.Delay(100);

            Assert.NotNull(widget.SelectedDestination);
            Assert.Equal("claude", widget.SelectedDestination.DestinationId);
            Assert.Equal("write a sorting function", widget.DraftText);
            Assert.Single(claude.Sent);
            Assert.Equal("write a sorting function", claude.Sent[0]);

            // 2. Spoken "To Codex, run all unit tests"
            approvalListener.EnqueueApproval(MakeAudio("send"));
            hotkey.SimulatePress();
            captureService.AppendAudio(MakeAudio("To Codex, run all unit tests"));
            hotkey.SimulateRelease();

            await speech.WaitUntilSpoken();
            await Task.Delay(100);

            Assert.Equal("codex", widget.SelectedDestination.DestinationId);
            Assert.Equal("run all unit tests", widget.DraftText);
            Assert.Single(codex.Sent);
            Assert.Equal("run all unit tests", codex.Sent[0]);
        }
    }

    /// <summary>
    /// Requirement 10: Gemma is never called for commands (verify with mock cleaner).
    /// </summary>
    [Fact]
    public async Task GemmaCleanerIsNeverCalledForApprovalCommands()
    {
        var (widget, claude, _, hotkey, _, cleaner, speech, approvalListener) = CreateContext();
        using (widget)
        {
            approvalListener.EnqueueApproval(MakeAudio("send"));

            var captureService = (InMemoryAudioCapture)widget.GetType()
                .GetField("_controller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(widget)!.GetType().GetProperty("AudioCaptureService")!.GetValue(
                    widget.GetType().GetField("_controller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(widget))!;

            // First: User dictates prompt
            hotkey.SimulatePress();
            captureService.AppendAudio(MakeAudio("Claude, format the codebase"));
            hotkey.SimulateRelease();

            await speech.WaitUntilSpoken();
            await Task.Delay(100);

            // The cleaner must have been called exactly ONCE for the initial prompt
            Assert.Equal(1, cleaner.CallCount);
            Assert.Equal("format the codebase", cleaner.LastInput);

            // And the send command was executed via Parakeet STT only, never cleaner
            Assert.Single(claude.Sent);
            Assert.Equal("format the codebase", claude.Sent[0]);

            // Cleaner call count is STILL 1
            Assert.Equal(1, cleaner.CallCount);
        }
    }

    /// <summary>
    /// Requirement 11: Microphone capture and TTS never overlap.
    /// </summary>
    [Fact]
    public async Task MicrophoneCaptureAndTtsNeverOverlap()
    {
        var (widget, _, _, hotkey, _, _, speech, approvalListener) = CreateContext();
        speech.HoldUntilReleased = true;

        using (widget)
        {
            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("Testing overlap safety.");

            await speech.WaitUntilStarted();

            // While TTS is speaking:
            Assert.True(widget.IsSpeakingReview);
            Assert.Equal(WidgetState.ReadingDraft, widget.State);
            // Global hotkey is unhooked during TTS
            Assert.False(hotkey.IsHooked);
            // Approval listener has not started yet
            Assert.False(approvalListener.IsListening);

            // Prepare approval audio and release TTS
            approvalListener.EnqueueApproval(MakeAudio("cancel"));
            speech.Release();

            await speech.WaitUntilSpoken();
            await Task.Delay(80);

            // Now TTS is finished, approval listener captured "cancel", and state is Idle
            Assert.False(widget.IsSpeakingReview);
            Assert.Equal(WidgetState.Idle, widget.State);
            Assert.True(hotkey.IsHooked);
        }
    }

    /// <summary>
    /// Silence timeout: when no audio is detected during approval listening, falls back to Confirm.
    /// </summary>
    [Fact]
    public async Task SilenceTimeoutFallsBackToConfirmState()
    {
        var (widget, claude, _, hotkey, _, _, speech, approvalListener) = CreateContext();
        using (widget)
        {
            // Empty audio bytes simulates silence timeout from listener
            approvalListener.EnqueueApproval(Array.Empty<byte>());

            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("Draft waiting for response.");

            await speech.WaitUntilSpoken();
            await Task.Delay(80);

            Assert.Empty(claude.Sent);
            Assert.Equal(WidgetState.Confirm, widget.State);
            Assert.Equal("Review the draft, then confirm", widget.StatusLine);
            Assert.True(hotkey.IsHooked);
        }
    }

    private static (
        WidgetViewModel Widget,
        RecordingAdapter Claude,
        RecordingAdapter Codex,
        FakeReviewPlayer PcSpeech,
        FakeReviewPlayer PhoneSpeech,
        FakeApprovalListener PcApproval,
        FakeApprovalListener PhoneApproval) CreatePhoneContext()
    {
        var claude = new RecordingAdapter("claude", "Claude", ready: true);
        var codex = new RecordingAdapter("codex", "Codex", ready: true);
        var registry = new DestinationRegistry(new IDestinationAdapter[] { claude, codex });

        var hotkey = new MockHotkeyService();
        var capture = new InMemoryAudioCapture();
        var controller = new PushToTalkController(hotkey, capture);

        var transcriber = new FakeTranscriber();
        var cleaner = new FakeCleaner();
        var pipeline = new VoicePipeline(transcriber, cleaner);

        var pcSpeech = new FakeReviewPlayer();
        var phoneSpeech = new FakeReviewPlayer();
        var pcApproval = new FakeApprovalListener();
        var phoneApproval = new FakeApprovalListener();

        var widget = new WidgetViewModel(action => action());
        widget.AttachController(controller);
        widget.AttachDestinations(registry);
        widget.AttachPipeline(pipeline);
        widget.AttachSpeech(pcSpeech);
        widget.AttachPhoneSpeech(phoneSpeech);
        widget.AttachApprovalListener(pcApproval);
        widget.AttachPhoneApprovalListener(phoneApproval);

        controller.Start();

        return (widget, claude, codex, pcSpeech, phoneSpeech, pcApproval, phoneApproval);
    }

    [Fact]
    public async Task PhoneDraft_SpeaksOnPhoneOnly_AndAffirmativeApprovalSends()
    {
        var (widget, claude, _, pcSpeech, phoneSpeech, pcApproval, phoneApproval) = CreatePhoneContext();
        using (widget)
        {
            phoneApproval.EnqueueApproval(MakeAudio("send"));

            widget.LoadPhoneDraft("Claude write a test", "write a test", "timings 10ms", "claude");

            Assert.NotNull(widget.SelectedDestination);
            Assert.Equal("claude", widget.SelectedDestination.DestinationId);

            await phoneSpeech.WaitUntilSpoken();
            await Task.Delay(100);

            Assert.Equal(0, pcSpeech.Calls);
            Assert.True(phoneSpeech.Calls >= 1);

            Assert.Equal(0, pcApproval.ListenApprovalCalls);
            Assert.True(phoneApproval.ListenApprovalCalls >= 1);

            Assert.Single(claude.Sent);
            Assert.Equal("write a test", claude.Sent[0]);
            Assert.Equal(WidgetState.Sent, widget.State);
        }
    }

    [Fact]
    public async Task PhoneDraft_DisconnectDuringApproval_SendsNothingAndReturnsToConfirm()
    {
        var (widget, claude, _, pcSpeech, phoneSpeech, _, phoneApproval) = CreatePhoneContext();
        using (widget)
        {
            phoneApproval.EnqueueApproval(Array.Empty<byte>());

            widget.LoadPhoneDraft("Claude test", "test", "timings", "claude");

            await phoneSpeech.WaitUntilSpoken();
            await Task.Delay(100);

            Assert.Empty(claude.Sent);
            Assert.Equal(WidgetState.Confirm, widget.State);
            Assert.Equal("Review the draft, then confirm", widget.StatusLine);
        }
    }

    private sealed class FakeTranscriber : ISpeechTranscriber
    {
        public bool IsLoaded => true;
        public int TranscribeCalls { get; private set; }
        public void EnsureLoaded() { }
        public TranscriptionResult Transcribe(byte[] pcm16Mono16k, CancellationToken cancellationToken = default)
        {
            TranscribeCalls++;
            if (pcm16Mono16k.Length == 0)
            {
                return new TranscriptionResult(string.Empty, 0, 0);
            }
            string text = Encoding.UTF8.GetString(pcm16Mono16k);
            return new TranscriptionResult(text, 10, 1.0);
        }
        public void Dispose() { }
    }

    private sealed class FakeCleaner : IPromptCleaner
    {
        public bool IsLoaded => true;
        public int CallCount { get; private set; }
        public string? LastInput { get; private set; }
        public void EnsureLoaded() { }
        public Task PrimeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CleanupResult> CleanAsync(string rawTranscript, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastInput = rawTranscript;
            return Task.FromResult(new CleanupResult(rawTranscript.Trim(), 5, true));
        }
        public void Dispose() { }
    }

    private sealed class FakeApprovalListener : IApprovalListener, IDisposable
    {
        private readonly Queue<byte[]> _approvalQueue = new();
        private readonly Queue<byte[]> _dictationQueue = new();
        private TaskCompletionSource<byte[]>? _activeApprovalTcs;
        private TaskCompletionSource<byte[]>? _activeDictationTcs;
        private readonly object _lock = new();

        public int ListenApprovalCalls { get; private set; }
        public int ListenDictationCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public bool IsListening { get; private set; }

        public void EnqueueApproval(byte[] audio)
        {
            lock (_lock)
            {
                if (_activeApprovalTcs != null && !_activeApprovalTcs.Task.IsCompleted)
                {
                    _activeApprovalTcs.TrySetResult(audio);
                    _activeApprovalTcs = null;
                }
                else
                {
                    _approvalQueue.Enqueue(audio);
                }
            }
        }

        public TaskCompletionSource<byte[]> PrepareApprovalGate()
        {
            lock (_lock)
            {
                var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                _activeApprovalTcs = tcs;
                return tcs;
            }
        }

        public void EnqueueDictation(byte[] audio)
        {
            lock (_lock)
            {
                if (_activeDictationTcs != null && !_activeDictationTcs.Task.IsCompleted)
                {
                    _activeDictationTcs.TrySetResult(audio);
                    _activeDictationTcs = null;
                }
                else
                {
                    _dictationQueue.Enqueue(audio);
                }
            }
        }

        public Task<byte[]> ListenForApprovalAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                ListenApprovalCalls++;
                IsListening = true;
                if (_approvalQueue.Count > 0)
                {
                    return Task.FromResult(_approvalQueue.Dequeue());
                }

                _activeApprovalTcs ??= new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() =>
                {
                    lock (_lock)
                    {
                        _activeApprovalTcs?.TrySetCanceled();
                    }
                });
                return _activeApprovalTcs.Task;
            }
        }

        public Task<byte[]> ListenForReplacementDictationAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                ListenDictationCalls++;
                IsListening = true;
                if (_dictationQueue.Count > 0)
                {
                    return Task.FromResult(_dictationQueue.Dequeue());
                }

                _activeDictationTcs ??= new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() =>
                {
                    lock (_lock)
                    {
                        _activeDictationTcs?.TrySetCanceled();
                    }
                });
                return _activeDictationTcs.Task;
            }
        }

        public void Cancel()
        {
            lock (_lock)
            {
                CancelCalls++;
                IsListening = false;
                _activeApprovalTcs?.TrySetCanceled();
                _activeApprovalTcs = null;
                _activeDictationTcs?.TrySetCanceled();
                _activeDictationTcs = null;
            }
        }

        public void Dispose() => Cancel();
    }

    private sealed class FakeReviewPlayer : ISpokenReview, IDisposable
    {
        private readonly SemaphoreSlim _spoken = new(0);
        private readonly SemaphoreSlim _started = new(0);
        private readonly SemaphoreSlim _gate = new(0);

        public int Calls { get; private set; }
        public string? LastDraft { get; private set; }
        public string? LastDestination { get; private set; }
        public bool HoldUntilReleased { get; set; }
        public bool IsSpeaking { get; private set; }

        public async Task<SpokenReviewResult> SpeakReviewAsync(
            string draft, string destinationName, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastDraft = draft;
            LastDestination = destinationName;
            IsSpeaking = true;
            _started.Release();

            if (HoldUntilReleased)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            IsSpeaking = false;
            _spoken.Release();

            var timings = new SpokenReviewTimings(50, 200, 50, new long[] { 50 }, 0);
            return new SpokenReviewResult(SpokenReviewOutcome.Completed, timings, null);
        }

        public void Cancel() { }
        public void Release() => _gate.Release();

        public async Task WaitUntilStarted() =>
            Assert.True(await _started.WaitAsync(TimeSpan.FromSeconds(5)), "speech never started");

        public async Task WaitUntilSpoken(int expected = 1)
        {
            for (int i = 0; i < expected; i++)
            {
                Assert.True(await _spoken.WaitAsync(TimeSpan.FromSeconds(5)), "speech never finished");
            }
        }

        public void Dispose()
        {
            _spoken.Dispose();
            _started.Dispose();
            _gate.Dispose();
        }
    }

    private sealed class RecordingAdapter : IDestinationAdapter
    {
        private readonly bool _ready;

        public RecordingAdapter(string id, string name, bool ready)
        {
            DestinationId = id;
            DisplayName = name;
            ProcessName = id;
            _ready = ready;
        }

        public string DestinationId { get; }
        public string DisplayName { get; }
        public string ProcessName { get; }
        public WindowCandidate? BoundWindow => null;
        public List<string> Sent { get; } = new();

        public DestinationStatus Probe() => new(
            _ready ? DestinationReadiness.Ready : DestinationReadiness.NotBound,
            Array.Empty<WindowCandidate>(),
            null,
            _ready ? "bound" : "not bound");

        public void Bind(WindowCandidate candidate) { }
        public void Unbind() { }

        public Task<SendResult> SendAsync(ConfirmedDraft draft, CancellationToken cancellationToken = default)
        {
            lock (Sent)
            {
                Sent.Add(draft.Text);
            }
            return Task.FromResult(new SendResult(SendStatus.Sent, "delivered", 1));
        }
    }
}
