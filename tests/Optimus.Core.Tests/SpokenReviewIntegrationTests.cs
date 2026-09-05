namespace Optimus.Core.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Speech;
using Optimus.Inference;
using Optimus.Providers;
using Optimus.Providers.Windows;
using Optimus.Shell.Models;
using Optimus.Shell.ViewModels;
using Xunit;

/// <summary>
/// The widget's spoken-review behaviour, driven through a fake engine so no audio device or
/// model is needed.
/// </summary>
public class SpokenReviewIntegrationTests
{
    private static (WidgetViewModel Widget, RecordingAdapter Claude) BuildWidget()
    {
        var claude = new RecordingAdapter("claude", "Claude", ready: true);
        var registry = new DestinationRegistry(new IDestinationAdapter[] { claude });

        var widget = new WidgetViewModel(action => action());
        widget.AttachDestinations(registry);
        return (widget, claude);
    }

    /// <summary>
    /// The invariant that matters most: speech finishing must not send anything.
    /// </summary>
    [Fact]
    public async Task FinishingTheSpokenReviewSendsNothing()
    {
        (WidgetViewModel widget, RecordingAdapter claude) = BuildWidget();
        using var speech = new FakeReviewPlayer();

        using (widget)
        {
            widget.AttachSpeech(speech);
            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("Add a retry to fetchUser in api.ts.");

            await speech.WaitUntilSpoken();
            await Task.Delay(100);

            Assert.Empty(claude.Sent);
            Assert.Equal(WidgetState.Confirm, widget.State);
        }
    }

    /// <summary>Capture is closed for the whole spoken window, then reopened.</summary>
    [Fact]
    public async Task MicrophoneIsClosedWhileSpeakingAndReopenedAfter()
    {
        (WidgetViewModel widget, _) = BuildWidget();
        using var speech = new FakeReviewPlayer { HoldUntilReleased = true };

        using (widget)
        {
            widget.AttachSpeech(speech);
            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("Some draft to read aloud.");

            await speech.WaitUntilStarted();
            Assert.True(widget.IsSpeakingReview);

            speech.Release();
            await speech.WaitUntilSpoken();
            await Task.Delay(100);

            Assert.False(widget.IsSpeakingReview);
        }
    }

    /// <summary>The exact visible draft and destination are what get spoken.</summary>
    [Fact]
    public async Task SpeaksTheVisibleDraftAndSelectedDestination()
    {
        (WidgetViewModel widget, _) = BuildWidget();
        using var speech = new FakeReviewPlayer();

        using (widget)
        {
            widget.AttachSpeech(speech);
            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("Bump the --timeout flag to 30 seconds.");

            await speech.WaitUntilSpoken();

            Assert.Equal("Bump the --timeout flag to 30 seconds.", speech.LastDraft);
            Assert.Equal("Claude", speech.LastDestination);
        }
    }

    /// <summary>Without a destination there is nothing to name, so it waits instead of guessing.</summary>
    [Fact]
    public async Task DoesNotSpeakUntilADestinationIsChosen()
    {
        (WidgetViewModel widget, _) = BuildWidget();
        using var speech = new FakeReviewPlayer();

        using (widget)
        {
            widget.AttachSpeech(speech);
            widget.LoadManualDraft("A draft with no destination yet.");
            await Task.Delay(150);

            Assert.Equal(0, speech.Calls);
            Assert.Contains("destination", widget.SpeechStatus, StringComparison.OrdinalIgnoreCase);

            widget.SelectedDestination = widget.Destinations[0];
            await speech.WaitUntilSpoken();

            Assert.Equal(1, speech.Calls);
        }
    }

    /// <summary>Re-probing or redundant updates must not read the same draft twice.</summary>
    [Fact]
    public async Task SpeaksOncePerDraftAndDestination()
    {
        (WidgetViewModel widget, _) = BuildWidget();
        using var speech = new FakeReviewPlayer();

        using (widget)
        {
            widget.AttachSpeech(speech);
            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("Only read me once.");
            await speech.WaitUntilSpoken();

            widget.RefreshDestinations();
            widget.SelectedDestination = widget.Destinations[0];
            await Task.Delay(150);

            Assert.Equal(1, speech.Calls);
        }
    }

    /// <summary>An edited draft cannot be sent until that exact edit has been read back.</summary>
    [Fact]
    public async Task EditedDraftIsRereadBeforeItCanBeSent()
    {
        (WidgetViewModel widget, RecordingAdapter claude) = BuildWidget();
        using var speech = new FakeReviewPlayer();

        using (widget)
        {
            widget.AttachSpeech(speech);
            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("Original draft.");
            await speech.WaitUntilSpoken();
            await Task.Delay(50);

            widget.DraftText = "Edited draft.";
            await widget.ConfirmAsync();
            await speech.WaitUntilSpoken();
            await Task.Delay(50);

            Assert.Empty(claude.Sent);
            Assert.Equal("Edited draft.", speech.LastDraft);

            await widget.ConfirmAsync();
            Assert.Single(claude.Sent);
            Assert.Equal("Edited draft.", claude.Sent[0]);
        }
    }

    /// <summary>A speech failure leaves the draft on screen and the widget usable.</summary>
    [Fact]
    public async Task SpeechFailureLeavesTheWidgetUsable()
    {
        (WidgetViewModel widget, RecordingAdapter claude) = BuildWidget();
        using var speech = new FakeReviewPlayer { Outcome = SpokenReviewOutcome.Failed };

        using (widget)
        {
            widget.AttachSpeech(speech);
            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("Draft that cannot be spoken.");

            await speech.WaitUntilSpoken();
            await Task.Delay(100);

            Assert.Equal(WidgetState.Confirm, widget.State);
            Assert.Equal("Draft that cannot be spoken.", widget.DraftText);
            Assert.True(widget.IsDraftEditable);
            Assert.Contains("unavailable", widget.SpeechStatus, StringComparison.OrdinalIgnoreCase);

            // The mouse fallback still works after a speech failure.
            await widget.ConfirmAsync();
            Assert.Single(claude.Sent);
        }
    }

    /// <summary>Cancel silences speech and allows the next draft to be read.</summary>
    [Fact]
    public async Task CancelStopsSpeechAndAllowsTheNextDraft()
    {
        (WidgetViewModel widget, _) = BuildWidget();
        using var speech = new FakeReviewPlayer();

        using (widget)
        {
            widget.AttachSpeech(speech);
            widget.SelectedDestination = widget.Destinations[0];
            widget.LoadManualDraft("First draft.");
            await speech.WaitUntilSpoken();

            widget.Cancel();
            Assert.True(speech.CancelCount >= 1);

            // The same text again: cancelling cleared the "already spoken" marker, so this is a
            // new review rather than a suppressed repeat.
            widget.LoadManualDraft("First draft.");
            await speech.WaitUntilSpoken();

            Assert.Equal(2, speech.Calls);
        }
    }

    /// <summary>Speaking must never be able to reach a destination adapter.</summary>
    private sealed class FakeReviewPlayer : ISpokenReview, IDisposable
    {
        private readonly SemaphoreSlim _spoken = new(0);
        private readonly SemaphoreSlim _started = new(0);
        private readonly SemaphoreSlim _gate = new(0);

        public int Calls { get; private set; }

        public int CancelCount { get; private set; }

        public string? LastDraft { get; private set; }

        public string? LastDestination { get; private set; }

        public bool HoldUntilReleased { get; init; }

        public SpokenReviewOutcome Outcome { get; init; } = SpokenReviewOutcome.Completed;

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

            var timings = new SpokenReviewTimings(120, 900, 110, new long[] { 90 }, 0);
            return new SpokenReviewResult(
                Outcome,
                Outcome == SpokenReviewOutcome.Completed ? timings : null,
                Outcome == SpokenReviewOutcome.Completed ? null : "engine unavailable");
        }

        public async Task<SpokenReviewResult> SpeakPromptAsync(
            string prompt, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastDraft = prompt;
            IsSpeaking = true;
            _started.Release();

            if (HoldUntilReleased)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            IsSpeaking = false;
            _spoken.Release();

            var timings = new SpokenReviewTimings(120, 900, 110, new long[] { 90 }, 0);
            return new SpokenReviewResult(
                Outcome,
                Outcome == SpokenReviewOutcome.Completed ? timings : null,
                Outcome == SpokenReviewOutcome.Completed ? null : "engine unavailable");
        }

        public void Cancel() => CancelCount++;

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

        public void Bind(WindowCandidate candidate)
        {
        }

        public void Unbind()
        {
        }

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
