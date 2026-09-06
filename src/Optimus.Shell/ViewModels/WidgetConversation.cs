namespace Optimus.Shell.ViewModels;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Narration;
using Optimus.Core.Speech;
using Optimus.Core.Voice;
using Optimus.Providers.Windows;
using Optimus.Shell.Models;

public sealed partial class WidgetViewModel
{
    private PersonalSettings _preferences = new();
    private string? _preferencesPath;
    private bool _showDetails;
    private int _choosingDestination;
    private string? _requiredAliasTitle;
    public PersonalSettings Preferences => _preferences;

    private static readonly System.Buffers.SearchValues<char> SentenceEnds = System.Buffers.SearchValues.Create(".!?\n");

    public bool CleanupEnabled
    {
        get => _preferences.CleanupEnabled;
        set { _preferences.CleanupEnabled = value; OnPropertyChanged(); SavePreferences(); }
    }
    public bool ShortReview
    {
        get => _preferences.ShortReview;
        set { _preferences.ShortReview = value; OnPropertyChanged(); SavePreferences(); }
    }
    /// <summary>
    /// Whether speaking interrupts the tool mid-sentence instead of waiting for it to finish.
    /// </summary>
    /// <remarks>
    /// Requires the microphone to stay open while the tool talks, so it is only safe on
    /// headphones. On speakers the microphone hears the tool and interrupts it constantly.
    /// </remarks>
    public bool BargeInEnabled
    {
        get => _preferences.BargeInEnabled;
        set { _preferences.BargeInEnabled = value; OnPropertyChanged(); SavePreferences(); }
    }
    public bool ShowDetails
    {
        get => _showDetails;
        set { _showDetails = value; OnPropertyChanged(); }
    }
    public Action? RequestOpenDetails { get; set; }
    public event Action<bool>? NarrationMuteChanged;

    public void LoadPreferences(string path)
    {
        _preferences = PersonalSettings.Load(path);
        _preferencesPath = path;
        RebuildAliases();
        SelectedDestination = Destinations.FirstOrDefault(d => d.DestinationId == _preferences.DestinationId);
        if (SelectedDestination != null) TryRestoreOrBind(SelectedDestination);
        OnPropertyChanged(nameof(CleanupEnabled));
        OnPropertyChanged(nameof(ShortReview));
    }

    private void SavePreferences()
    {
        if (_preferencesPath == null) return;
        _preferences.DestinationId = SelectedDestination?.DestinationId;
        foreach (DestinationOption destination in Destinations)
        {
            if (destination.Adapter.BoundWindow is { } window)
                _preferences.WindowTitles[destination.DestinationId] = window.Title;
        }
        try { _preferences.Save(_preferencesPath); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { StatusLine = $"Could not save preferences: {ex.Message}"; }
    }

    private void RebuildAliases()
    {
        var aliases = Destinations.SelectMany(d => new[]
        {
            new VoiceDestinationAlias(d.DestinationId, d.DisplayName),
            new VoiceDestinationAlias(d.DestinationId, d.DestinationId)
        }).Concat(_preferences.Aliases.Select(a => new VoiceDestinationAlias(a.Value.DestinationId, a.Key)));
        _destinationResolver = new VoiceDestinationResolver(aliases);
    }

    private void TryRestoreOrBind(DestinationOption destination)
    {
        var status = destination.Adapter.Probe();
        if (status.CanSend) return;
        var choices = status.Candidates;
        bool hasSavedWindow = _preferences.WindowTitles.ContainsKey(destination.DestinationId);
        WindowCandidate? selected = !hasSavedWindow && _requiredAliasTitle == null && choices.Count == 1 ? choices[0] : null;
        if (selected == null && _preferences.WindowTitles.TryGetValue(destination.DestinationId, out string? title))
        {
            var matches = choices.Where(w => string.Equals(w.Title, title, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 1) selected = matches[0];
        }
        if (selected == null) return;
        try
        {
            destination.Adapter.Bind(selected);
            destination.Status = destination.Adapter.Probe();
            SavePreferences();
            DestinationStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (ArgumentException) { } // Window closed between enumeration and binding; voice chooser handles it.
    }

    private void ApplyAliasWindow(string transcript, DestinationOption destination)
    {
        _requiredAliasTitle = null;
        string source = transcript.Trim();
        if (source.StartsWith("switch to ", StringComparison.OrdinalIgnoreCase)) source = source[10..];
        if (source.StartsWith("to ", StringComparison.OrdinalIgnoreCase)) source = source[3..];
        var alias = _preferences.Aliases.OrderByDescending(a => a.Key.Length).FirstOrDefault(a =>
            source.StartsWith(a.Key, StringComparison.OrdinalIgnoreCase) &&
            (source.Length == a.Key.Length || !char.IsLetterOrDigit(source[a.Key.Length])));
        if (alias.Value == null) return;
        _requiredAliasTitle = alias.Value.WindowTitle;
        var windows = destination.Adapter.Probe().Candidates.Where(w => w.Title == alias.Value.WindowTitle).ToArray();
        destination.Adapter.Unbind();
        if (windows.Length == 1) destination.Adapter.Bind(windows[0]);
        // A saved project alias is an exact target. Missing aliases must ask, never use another window.
        destination.Status = destination.Adapter.Probe();
        _preferences.WindowTitles[destination.DestinationId] = alias.Value.WindowTitle;
    }

    public void ProcessPhoneAudio(byte[] pcm)
    {
        int generation = BeginNewUtterance();
        _draftOriginPhone = true;
        ProcessAudioBytes(pcm, generation, _processingCts!.Token);
    }

    public void PhoneCaptureBeginning()
    {
        BeginNewUtterance();
        _draftOriginPhone = true;
        State = WidgetState.Listening;
        StatusLine = "Listening on Pixel...";
    }

    /// <summary>
    /// Recovers from a phone capture that can never arrive, so the widget does not wait forever
    /// on a device that has gone away.
    /// </summary>
    /// <remarks>
    /// Only a capture the phone owns is abandoned. A hold in progress on this PC keeps the
    /// microphone and finishes normally, even if the phone drops at the same moment.
    /// </remarks>
    public void PhoneCaptureAbandoned()
    {
        if (State != WidgetState.Listening || !_draftOriginPhone)
        {
            return;
        }

        if (_controller?.AudioCaptureService.IsCapturing == true)
        {
            return;
        }

        _phoneApprovalListener?.Cancel();
        _draftOriginPhone = false;
        State = HasDraft ? WidgetState.Confirm : WidgetState.Idle;
        StatusLine = HasDraft
            ? "Phone disconnected — review the draft, then confirm"
            : $"Phone disconnected — Hold {HotkeyLabel} to speak";
        _controller?.Start();
    }

    internal bool HandleConversationCommand(string text, bool duringApproval)
    {
        ConversationCommand? command = ConversationCommand.Parse(text);
        if (command == null) return false;
        return ExecuteConversationCommand(command, duringApproval);
    }

    internal bool ExecuteConversationCommand(ConversationCommand command, bool duringApproval)
    {
        switch (command.Kind)
        {
            case "switch":
                _requiredAliasTitle = null;
                var resolved = _destinationResolver?.Resolve("to " + command.Value);
                var target = resolved?.Status == VoiceDestinationResolutionStatus.Resolved && string.IsNullOrWhiteSpace(resolved.PromptText)
                    ? Destinations.FirstOrDefault(d => d.DestinationId == resolved.DestinationId) : null;
                State = WidgetState.Processing;
                SelectedDestination = target;
                if (target != null) ApplyAliasWindow(command.Value, target);
                if (target == null) { _ = AskForDestinationAsync(); return true; }
                break;
            case "cleanupOn": CleanupEnabled = true; break;
            case "cleanupOff": CleanupEnabled = false; break;
            case "interruptOn": BargeInEnabled = true; break;
            case "interruptOff": BargeInEnabled = false; break;
            case "shortReview": ShortReview = true; break;
            case "fullReview": ShortReview = false; break;
            case "detailsOn":
                ShowDetails = true;
                RequestOpenDetails?.Invoke();
                break;
            case "detailsOff": ShowDetails = false; break;
            case "mute": NarrationMuteChanged?.Invoke(true); break;
            case "unmute": NarrationMuteChanged?.Invoke(false); break;
            case "concise": NarrationMode = NarrationMode.Concise; break;
            case "comprehensive": NarrationMode = NarrationMode.Comprehensive; break;
            case "toolsOn": NarrateToolsAndSkills = true; break;
            case "toolsOff": NarrateToolsAndSkills = false; break;
            case "pause": IsPaused = true; break;
            case "resume": IsPaused = false; break;
            case "endConversation": EndConversation(); return true;
            case "openAo": OpenInAo(); return true;
            case "approveDecision":
                if (HasPendingDecision)
                {
                    ApproveDecision();
                    _ = SpeakThenResumeAsync("Decision approved.", duringApproval);
                    return true;
                }
                break;
            case "rejectDecision":
                if (HasPendingDecision)
                {
                    RejectDecision();
                    _ = SpeakThenResumeAsync("Decision rejected.", duringApproval);
                    return true;
                }
                break;
            case "alias":
                if (SelectedDestination?.Adapter.BoundWindow is not { } window)
                { _ = SpeakThenResumeAsync("Choose a window before naming it.", duringApproval); return true; }
                _preferences.Aliases[command.Value] = new(SelectedDestination.DestinationId, window.Title);
                RebuildAliases(); SavePreferences();
                break;
            case "append" when duringApproval:
                State = WidgetState.Processing;
                DraftText = DraftText.TrimEnd() + " " + command.Value;
                break;
            case "replace" when duringApproval:
                int first = DraftText.IndexOf(command.Value, StringComparison.OrdinalIgnoreCase);
                if (first < 0 || DraftText.IndexOf(command.Value, first + command.Value.Length, StringComparison.OrdinalIgnoreCase) >= 0)
                { _ = SpeakThenResumeAsync("That text does not identify one unique passage. Say replace, the exact words, with, the replacement.", true); return true; }
                State = WidgetState.Processing;
                DraftText = DraftText[..first] + command.Replacement + DraftText[(first + command.Value.Length)..];
                break;
            case "removeLast" when duringApproval:
                string draft = DraftText.TrimEnd().TrimEnd('.', '!', '?');
                int boundary = draft.AsSpan().LastIndexOfAny(SentenceEnds);
                State = WidgetState.Processing;
                DraftText = boundary >= 0 ? draft[..(boundary + 1)] : string.Empty;
                break;
            case "repeat" when duringApproval: break;
            case "status": _ = SpeakThenResumeAsync(string.IsNullOrWhiteSpace(NarrationStatus) ? "No agent update yet." : NarrationStatus, duringApproval); return true;
            default: return false;
        }
        string response = command.Kind == "switch" ? $"Now using {SelectedDestination?.DisplayName}." : "Updated.";
        _lastSpokenKey = string.Empty;
        if (duringApproval && HasDraft) { State = WidgetState.Confirm; MaybeSpeakReview(); }
        else _ = SpeakThenResumeAsync(response + " Speak your prompt after the chime.", false);
        return true;
    }

    private async Task SpeakThenResumeAsync(string message, bool review)
    {
        int generation = _utteranceGeneration;
        var speech = _draftOriginPhone ? _phoneSpeech : _speech;
        _controller?.Stop();
        State = WidgetState.ReadingDraft;
        try
        {
            if (speech != null)
            {
                var result = await speech.SpeakPromptAsync(message, _processingCts?.Token ?? default).ConfigureAwait(false);
                if (!result.ApprovalMayBegin) return;
            }
            _dispatchAction(() =>
            {
                if (!IsCurrent(generation)) return;
                if (review) { _lastSpokenKey = string.Empty; State = WidgetState.Confirm; MaybeSpeakReview(); }
                else StartRedictation();
            });
        }
        catch (OperationCanceledException) { }
        finally
        {
            _dispatchAction(() =>
            {
                if (IsCurrent(generation) && State == WidgetState.ReadingDraft)
                { State = WidgetState.Confirm; _controller?.Start(); }
            });
        }
    }

    private async Task AskForDestinationAsync()
    {
        if (Interlocked.Exchange(ref _choosingDestination, 1) != 0) return;
        int generation = _utteranceGeneration;
        var speech = _draftOriginPhone ? _phoneSpeech : _speech;
        var listener = _draftOriginPhone ? _phoneApprovalListener : _approvalListener;
        try
        {
            if (speech == null || listener == null || _pipeline == null) return;
            _controller?.Stop();
            for (int attempt = 0; attempt < 3 && IsCurrent(generation); attempt++)
            {
                _dispatchAction(() => State = WidgetState.ReadingDraft);
                var result = await speech.SpeakPromptAsync("Which agent? Say Claude, Antigravity, Codex, or a saved project name. Or cancel.", _processingCts?.Token ?? default).ConfigureAwait(false);
                if (!result.ApprovalMayBegin || !IsCurrent(generation)) return;
                _dispatchAction(() => State = WidgetState.AwaitingApproval);
                byte[] audio = await listener.ListenForApprovalAsync(_processingCts?.Token ?? default).ConfigureAwait(false);
                if (!IsCurrent(generation)) return;
                string heard = audio.Length == 0 ? "" : _pipeline.TranscribeOnly(audio, _processingCts?.Token ?? default).Text;
                if (ApprovalCommandClassifier.Classify(heard) == ApprovalCommand.Cancel)
                { _dispatchAction(Cancel); return; }
                var resolution = _destinationResolver?.Resolve("to " + heard.Trim().TrimEnd('.', '!', '?'));
                if (resolution?.Status != VoiceDestinationResolutionStatus.Resolved || !string.IsNullOrWhiteSpace(resolution.PromptText)) continue;
                _dispatchAction(() =>
                {
                    if (!IsCurrent(generation)) return;
                    State = WidgetState.Processing;
                    SelectedDestination = Destinations.First(d => d.DestinationId == resolution.DestinationId);
                    ApplyAliasWindow(heard, SelectedDestination);
                    State = WidgetState.Confirm;
                    if (HasDraft) MaybeSpeakReview(); else StartRedictation();
                });
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _dispatchAction(() => StatusLine = $"Voice selection unavailable: {ex.Message}"); }
        finally
        {
            Interlocked.Exchange(ref _choosingDestination, 0);
            _dispatchAction(() =>
            {
                if (IsCurrent(generation) && SelectedDestination == null)
                { State = WidgetState.Confirm; StatusLine = "Say an agent name on your next capture. Draft kept."; _controller?.Start(); }
            });
        }
    }
}
