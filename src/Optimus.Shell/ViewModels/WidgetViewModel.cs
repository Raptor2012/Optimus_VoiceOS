namespace Optimus.Shell.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Optimus.Core.Audio;
using Optimus.Core.Narration;
using Optimus.Core.Speech;
using Optimus.Core.Voice;
using Optimus.Inference;
using Optimus.Providers;
using Optimus.Providers.Windows;
using Optimus.Shell.Models;

public sealed partial class WidgetViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Action<Action> _dispatchAction;
    private PushToTalkController? _controller;
    private VoicePipeline? _pipeline;
    private CancellationTokenSource? _processingCts;
    private int _utteranceGeneration;
    private WidgetState _state = WidgetState.Idle;
    private string _statusLine = "Ready — Hold F8 to speak";
    private string _draftText = string.Empty;
    private string _rawTranscript = string.Empty;
    private string _stageTimings = string.Empty;
    private string _destinationName = "Say Claude, Antigravity, or Codex";
    private string _errorMessage = string.Empty;
    private string _hotkeyLabel = "F8";
    private DestinationOption? _selectedDestination;
    private WindowCandidate? _selectedWindowChoice;
    private bool _isDraftEditable = true;
    private string _lastSentText = string.Empty;
    private string _phoneStatus = string.Empty;
    private ISpokenReview? _speech;
    private ISpokenReview? _phoneSpeech;
    private string _speechStatus = string.Empty;
    private bool _isSpeakingReview;
    private string _lastSpokenKey = string.Empty;
    private int _spokenReviewGeneration;
    private IApprovalListener? _approvalListener;
    private IApprovalListener? _phoneApprovalListener;
    private bool _isSessionOpen;
    private CancellationTokenSource? _sessionCts;
    private VoiceDestinationResolver? _destinationResolver;
    private int _isSending;
    private string _lastApprovalStatus = string.Empty;
    private bool _draftOriginPhone;
    private NarrationMode _narrationMode = NarrationMode.Concise;
    private bool _narrateToolsAndSkills;
    private string _narrationStatus = string.Empty;

    public event EventHandler<AgentRunEventArgs>? AgentRunStarting;
    public event EventHandler? AgentRunCancelled;
    public event EventHandler<DestinationOption?>? SelectedDestinationChanged;
    public event EventHandler? DestinationStatusChanged;
    public event EventHandler<WidgetSendStartingEventArgs>? SendingStarted;
    public event EventHandler<WidgetSendCompletedEventArgs>? SendCompleted;
    public event EventHandler? Cancelled;

    public WidgetState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsListening));
                OnPropertyChanged(nameof(IsConfirming));
                OnPropertyChanged(nameof(IsReadingDraft));
                OnPropertyChanged(nameof(IsAwaitingApproval));
                OnPropertyChanged(nameof(IsRedictating));
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(StatusBadgeColor));
                OnPropertyChanged(nameof(IsDraftVisible));
                OnPropertyChanged(nameof(IsConfirmPanelVisible));
                OnPropertyChanged(nameof(IsSessionListening));
                OnPropertyChanged(nameof(SessionBadgeText));
                OnPropertyChanged(nameof(SessionBadgeColor));
            }
        }
    }

    /// <summary>
    /// True while a continuous session is open, whether or not it is listening this instant.
    /// </summary>
    /// <remarks>
    /// A session opens on the first real hold and closes only on a hotkey tap. Leaving it open
    /// never authorises a send: a session utterance produces a draft and still has to clear the
    /// same spoken approval against the same named destination as a held one.
    /// </remarks>
    public bool IsSessionOpen
    {
        get => _isSessionOpen;
        private set
        {
            if (_isSessionOpen != value)
            {
                _isSessionOpen = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSessionListening));
                OnPropertyChanged(nameof(SessionBadgeText));
                OnPropertyChanged(nameof(SessionBadgeColor));
            }
        }
    }

    /// <summary>True when the session is open and actually waiting for speech right now.</summary>
    public bool IsSessionListening => _isSessionOpen && State == WidgetState.SessionListening;

    /// <summary>Distinguishes a session that is listening from one that is merely open.</summary>
    public string SessionBadgeText =>
        IsSessionListening ? $"SESSION LISTENING — TAP {HotkeyLabel} TO END" : "SESSION OPEN";

    public string SessionBadgeColor => IsSessionListening ? "#30D158" : "#6B7F8F";

    /// <summary>
    /// Reports whether agent narration is currently audible, so the session does not transcribe
    /// the tool's own speech. Left unset, the session assumes nothing is playing.
    /// </summary>
    public Func<bool>? NarrationActive { get; set; }

    public string StatusLine
    {
        get => _statusLine;
        set
        {
            if (_statusLine != value)
            {
                _statusLine = value;
                OnPropertyChanged();
            }
        }
    }

    public string DraftText
    {
        get => _draftText;
        set
        {
            if (_draftText != value)
            {
                _draftText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDraft));
                OnPropertyChanged(nameof(IsDraftVisible));
                OnPropertyChanged(nameof(IsConfirmPanelVisible));

                // If modified while awaiting approval or reading, invalidate approval and reread
                if (State is WidgetState.AwaitingApproval or WidgetState.ReadingDraft)
                {
                    _approvalListener?.Cancel();
                    _phoneApprovalListener?.Cancel();
                    _speech?.Cancel();
                    _phoneSpeech?.Cancel();
                    _lastSpokenKey = string.Empty;
                    State = WidgetState.Confirm;
                    _controller?.Start();
                    MaybeSpeakReview();
                }
            }
        }
    }

    /// <summary>Exact Parakeet output, shown above the draft so the user can compare.</summary>
    public string RawTranscript
    {
        get => _rawTranscript;
        set
        {
            if (_rawTranscript != value)
            {
                _rawTranscript = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasRawTranscript));
            }
        }
    }

    /// <summary>Per-stage latency, e.g. "audio 3.2s · STT 310 ms · cleanup 840 ms · total 1150 ms".</summary>
    public string StageTimings
    {
        get => _stageTimings;
        set
        {
            if (_stageTimings != value)
            {
                _stageTimings = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasStageTimings));
            }
        }
    }

    public bool HasRawTranscript => !string.IsNullOrWhiteSpace(RawTranscript);

    public bool HasStageTimings => !string.IsNullOrWhiteSpace(StageTimings);

    public bool HasDraft => !string.IsNullOrWhiteSpace(DraftText);

    /// <summary>Keeps the confirmed text visible while sending and after a recoverable failure.</summary>
    public bool IsDraftVisible =>
        HasDraft && State is WidgetState.Confirm or WidgetState.Sending or WidgetState.Error or WidgetState.ReadingDraft or WidgetState.AwaitingApproval or WidgetState.Redictating;

    /// <summary>A failed send remains retryable without another recording.</summary>
    public bool IsConfirmPanelVisible =>
        HasDraft && State is WidgetState.Confirm or WidgetState.Error or WidgetState.ReadingDraft or WidgetState.AwaitingApproval;

    /// <summary>The three configured destinations. Selection is always manual.</summary>
    public ObservableCollection<DestinationOption> Destinations { get; } = new();

    /// <summary>Candidate windows for the selected destination.</summary>
    public ObservableCollection<WindowCandidate> WindowChoices { get; } = new();

    public DestinationOption? SelectedDestination
    {
        get => _selectedDestination;
        set
        {
            if (!ReferenceEquals(_selectedDestination, value))
            {
                _selectedDestination = value;

                if (value != null)
                {
                    value.Status = value.Adapter.Probe();
                    DestinationName = FormatDestinationWithWindow(value.DisplayName, value.Adapter.BoundWindow);
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedDestination));
                UpdateWindowChoices();
                SelectedDestinationChanged?.Invoke(this, value);
                NotifyCapsuleProperties();
                SavePreferences();

                // If destination changed during review or approval, reset to Confirm and reread
                if (State is WidgetState.AwaitingApproval or WidgetState.ReadingDraft)
                {
                    _approvalListener?.Cancel();
                    _phoneApprovalListener?.Cancel();
                    State = WidgetState.Confirm;
                    _controller?.Start();
                }

                // A draft may already be waiting for a destination before it can be read out.
                MaybeSpeakReview();
            }
        }
    }

    public WindowCandidate? SelectedWindowChoice
    {
        get => _selectedWindowChoice;
        set
        {
            if (!ReferenceEquals(_selectedWindowChoice, value))
            {
                _selectedWindowChoice = value;
                OnPropertyChanged();
            }
        }
    }

    public bool HasSelectedDestination => SelectedDestination != null;

    /// <summary>True when the user still has to pick which window to target.</summary>
    public bool NeedsWindowChoice =>
        SelectedDestination is { IsReady: false } && WindowChoices.Count > 0;

    /// <summary>False while a send is in flight, so the draft cannot change mid-send.</summary>
    public bool IsDraftEditable
    {
        get => _isDraftEditable;
        set
        {
            if (_isDraftEditable != value)
            {
                _isDraftEditable = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The exact text of the last successful send, for verification.</summary>
    public string LastSentText
    {
        get => _lastSentText;
        private set
        {
            if (_lastSentText != value)
            {
                _lastSentText = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Phone endpoint state, shown so the user knows what address to dial.</summary>
    public string PhoneStatus
    {
        get => _phoneStatus;
        set
        {
            if (_phoneStatus != value)
            {
                _phoneStatus = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPhoneStatus));
            }
        }
    }

    public bool HasPhoneStatus => !string.IsNullOrWhiteSpace(PhoneStatus);

    public NarrationMode NarrationMode
    {
        get => _narrationMode;
        set { if (_narrationMode != value) { _narrationMode = value; OnPropertyChanged(); } }
    }

    public bool NarrateToolsAndSkills
    {
        get => _narrateToolsAndSkills;
        set { if (_narrateToolsAndSkills != value) { _narrateToolsAndSkills = value; OnPropertyChanged(); } }
    }

    public string NarrationStatus
    {
        get => _narrationStatus;
        set { if (_narrationStatus != value) { _narrationStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasNarrationStatus)); } }
    }

    public bool HasNarrationStatus => !string.IsNullOrWhiteSpace(NarrationStatus);

    public NarrationOptions CurrentNarrationOptions => new()
    {
        Mode = NarrationMode,
        NarrateToolsAndSkills = NarrateToolsAndSkills
    };

    /// <summary>
    /// True while the draft is being read aloud. Capture is closed for this whole window so the
    /// microphone cannot hear the app's own speech.
    /// </summary>
    public bool IsSpeakingReview
    {
        get => _isSpeakingReview;
        private set
        {
            if (_isSpeakingReview != value)
            {
                _isSpeakingReview = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Spoken-review latencies, or why speech was unavailable.</summary>
    public string SpeechStatus
    {
        get => _speechStatus;
        private set
        {
            if (_speechStatus != value)
            {
                _speechStatus = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSpeechStatus));
            }
        }
    }

    public bool HasSpeechStatus => !string.IsNullOrWhiteSpace(SpeechStatus);

    public string DestinationName
    {
        get => _destinationName;
        set
        {
            if (_destinationName != value)
            {
                _destinationName = value;
                OnPropertyChanged();
            }
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (_errorMessage != value)
            {
                _errorMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public string HotkeyLabel
    {
        get => _hotkeyLabel;
        set
        {
            if (_hotkeyLabel != value)
            {
                _hotkeyLabel = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsListening => State == WidgetState.Listening;
    public bool IsConfirming => State is WidgetState.Confirm or WidgetState.AwaitingApproval;
    public bool IsReadingDraft => State == WidgetState.ReadingDraft;
    public bool IsAwaitingApproval => State == WidgetState.AwaitingApproval;
    public bool IsRedictating => State == WidgetState.Redictating;
    public bool IsError => State == WidgetState.Error;
    public bool IsIdle => State == WidgetState.Idle;

    public string LastApprovalStatus
    {
        get => _lastApprovalStatus;
        private set
        {
            if (_lastApprovalStatus != value)
            {
                _lastApprovalStatus = value;
                OnPropertyChanged();
            }
        }
    }

    public string StatusBadgeColor => State switch
    {
        WidgetState.Listening => "#FF3B30", // Red recording indicator
        WidgetState.Processing => "#FF9500", // Orange
        WidgetState.Confirm => "#0A84FF", // Blue
        WidgetState.Sending => "#5E5CE6", // Purple
        WidgetState.Sent => "#30D158", // Green
        WidgetState.Error => "#FF453A", // Red
        WidgetState.ReadingDraft => "#BF5AF2", // Purple reading draft
        WidgetState.AwaitingApproval => "#0A84FF", // Blue awaiting approval
        WidgetState.Redictating => "#FF3B30", // Red redictating
        _ => "#30D158" // Green ready
    };

    public ICommand DismissErrorCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand RefreshDestinationsCommand { get; }
    public ICommand BindWindowCommand { get; }

    public WidgetViewModel(Action<Action>? dispatchAction = null)
    {
        _dispatchAction = dispatchAction ?? (action =>
        {
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(action);
            }
            else
            {
                action();
            }
        });

        DismissErrorCommand = new RelayCommand(DismissError);
        CancelCommand = new RelayCommand(Cancel);
        ConfirmCommand = new RelayCommand(Confirm);
        RefreshDestinationsCommand = new RelayCommand(RefreshDestinations);
        BindWindowCommand = new RelayCommand(BindSelectedWindow);
    }

    public void AttachController(PushToTalkController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _controller.StateChanged += OnCaptureStateChanged;
        _controller.AudioCaptured += OnAudioCaptured;
        _controller.ErrorOccurred += OnCaptureError;
        _controller.Tapped += OnHotkeyTapped;
        _controller.CapturePreparing += OnCapturePreparing;
    }

    public void DetachController()
    {
        if (_controller != null)
        {
            _controller.StateChanged -= OnCaptureStateChanged;
            _controller.AudioCaptured -= OnAudioCaptured;
            _controller.ErrorOccurred -= OnCaptureError;
            _controller.Tapped -= OnHotkeyTapped;
            _controller.CapturePreparing -= OnCapturePreparing;
            _controller = null;
        }
    }

    /// <summary>Supplies the STT + cleanup pipeline. Without one, capture still works and the
    /// widget reports the captured audio only.</summary>
    public void AttachPipeline(VoicePipeline pipeline) =>
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));

    /// <summary>
    /// Supplies the spoken-review player. Without one the widget stays fully usable and simply
    /// does not speak.
    /// </summary>
    public void AttachSpeech(ISpokenReview speech) =>
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));

    /// <summary>Supplies the phone spoken-review player for phone-originated drafts.</summary>
    public void AttachPhoneSpeech(ISpokenReview phoneSpeech) =>
        _phoneSpeech = phoneSpeech ?? throw new ArgumentNullException(nameof(phoneSpeech));

    /// <summary>Supplies the one-shot approval listener for spoken review confirmations.</summary>
    public void AttachApprovalListener(IApprovalListener listener) =>
        _approvalListener = listener ?? throw new ArgumentNullException(nameof(listener));

    /// <summary>Supplies the phone approval listener for phone-originated review confirmations.</summary>
    public void AttachPhoneApprovalListener(IApprovalListener listener) =>
        _phoneApprovalListener = listener ?? throw new ArgumentNullException(nameof(listener));

    /// <summary>Supplies the destination alias resolver for spoken routing prefixes.</summary>
    public void AttachDestinationResolver(VoiceDestinationResolver resolver) =>
        _destinationResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    /// <summary>Reports voice warmup, or why the voice is unavailable.</summary>
    public void SetSpeechReady(string status) => SpeechStatus = status;

    /// <summary>Loads the configured destinations. No destination is selected by default.</summary>
    public void AttachDestinations(DestinationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        Destinations.Clear();
        var aliases = new List<VoiceDestinationAlias>();
        foreach ((IDestinationAdapter adapter, DestinationStatus status) in registry.ProbeAll())
        {
            Destinations.Add(new DestinationOption(adapter, status));
            aliases.Add(new VoiceDestinationAlias(adapter.DestinationId, adapter.DisplayName));
            if (!string.Equals(adapter.DestinationId, adapter.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                aliases.Add(new VoiceDestinationAlias(adapter.DestinationId, adapter.DestinationId));
            }
        }

        if (_destinationResolver == null && aliases.Count > 0)
        {
            _destinationResolver = new VoiceDestinationResolver(aliases);
        }

        // Deliberately leaves SelectedDestination null: the user picks, always.
        OnPropertyChanged(nameof(Destinations));
    }

    /// <summary>
    /// Loads user-supplied text into the ordinary review/confirmation flow.
    /// </summary>
    /// <remarks>
    /// Used by the lightweight <c>--draft</c> dogfood path so destination adapters can be
    /// exercised without a microphone or model startup. It deliberately does not select or
    /// bind a destination and therefore cannot send by itself.
    /// </remarks>
    public void LoadManualDraft(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        BeginNewUtterance();
        _draftOriginPhone = false;
        RawTranscript = text;
        DraftText = text;
        StageTimings = "manual draft · STT skipped · cleanup skipped";
        ErrorMessage = string.Empty;
        IsDraftEditable = true;
        State = WidgetState.Confirm;
        StatusLine = "Manual draft loaded — choose a destination, review, then confirm";
        MaybeSpeakReview();
    }

    /// <summary>
    /// Shows a draft that came from the phone, using the same confirm flow as a desktop
    /// utterance so there is one gate, not two.
    /// </summary>
    public void LoadPhoneDraft(string rawTranscript, string cleanedDraft, string timings, string? destinationId = null)
    {
        BeginNewUtterance();
        _draftOriginPhone = true;
        RawTranscript = rawTranscript;
        DraftText = cleanedDraft;
        StageTimings = timings;
        ErrorMessage = string.Empty;
        IsDraftEditable = true;
        if (destinationId != null)
        {
            DestinationOption? matched = Destinations.FirstOrDefault(d =>
                string.Equals(d.DestinationId, destinationId, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
            {
                SelectedDestination = matched;
            }
        }
        State = WidgetState.Confirm;
        StatusLine = "Phone draft — review, then confirm";
        MaybeSpeakReview();
    }

    /// <summary>Mirrors a phone-confirmed draft into the widget's single send lifecycle.</summary>
    public void BeginPhoneSend(string text, string destinationId, string destinationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);

        DraftText = text;
        SelectedDestination = Destinations.FirstOrDefault(option =>
            string.Equals(option.DestinationId, destinationId, StringComparison.Ordinal));
        ErrorMessage = string.Empty;
        IsDraftEditable = false;
        State = WidgetState.Sending;
        StatusLine = $"Sending to {destinationName} from phone...";
    }

    /// <summary>Applies the adapter's real outcome to the same draft shown on both devices.</summary>
    public void CompletePhoneSend(string text, string destinationName, SendResult result)
    {
        IsDraftEditable = true;

        if (result.Succeeded)
        {
            LastSentText = text;
            ErrorMessage = string.Empty;
            State = WidgetState.Sent;
            StatusLine = $"Sent to {destinationName} ({result.ElapsedMilliseconds} ms)";
            if (SelectedDestination != null)
            {
                AgentRunStarting?.Invoke(this, new AgentRunEventArgs(SelectedDestination.Adapter, true, text));
            }
            return;
        }

        AgentRunCancelled?.Invoke(this, EventArgs.Empty);

        ErrorMessage = result.Detail;
        State = WidgetState.Error;
        StatusLine = $"NOT sent to {destinationName}";
    }

    public void DismissError()
    {
        ErrorMessage = string.Empty;
        if (HasDraft)
        {
            State = WidgetState.Confirm;
            StatusLine = "Review the draft, then confirm";
        }
        else
        {
            State = WidgetState.Idle;
            StatusLine = $"Ready — Hold {HotkeyLabel} to speak";
        }
    }

    public void Cancel()
    {
        // Claims a new generation so an in-flight result cannot land after the cancel.
        BeginNewUtterance();
        _approvalListener?.Cancel();
        _phoneApprovalListener?.Cancel();
        _speech?.Cancel();
        _phoneSpeech?.Cancel();
        _lastSpokenKey = string.Empty;
        SpeechStatus = string.Empty;
        DraftText = string.Empty;
        RawTranscript = string.Empty;
        StageTimings = string.Empty;
        ErrorMessage = string.Empty;
        State = WidgetState.Idle;
        StatusLine = $"Cancelled — Hold {HotkeyLabel} to speak";
        Cancelled?.Invoke(this, EventArgs.Empty);
        _controller?.Start();
    }

    /// <summary>
    /// Snapshots the visible draft and sends it to the selected destination.
    /// </summary>
    /// <remarks>
    /// The draft is captured into an immutable <see cref="ConfirmedDraft"/> before anything else
    /// happens, so what is transmitted is exactly the text on screen at the instant the user
    /// confirmed. The widget reports <c>Sent</c> only after the adapter reports success; a focus
    /// failure, a closed window or a rejected keystroke leaves the draft intact and says why,
    /// rather than claiming a send that did not happen.
    /// </remarks>
    public void Confirm() => _ = ConfirmAsync();

    public async Task ConfirmAsync()
    {
        if (Interlocked.CompareExchange(ref _isSending, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (State == WidgetState.Sending || State == WidgetState.Sent)
            {
                return;
            }

            string draftSnapshot = DraftText;
            DestinationOption? destination = SelectedDestination;

            if (string.IsNullOrWhiteSpace(draftSnapshot))
            {
                StatusLine = "Nothing to send.";
                return;
            }

            if (destination == null)
            {
                StatusLine = "Choose a destination first.";
                return;
            }

            // An edit or destination change invalidates the spoken review. Read the exact current
            // pair before allowing either the button or the later voice-approval path to send it.
            ISpokenReview? activeSpeech = _draftOriginPhone ? _phoneSpeech : _speech;
            if (activeSpeech != null)
            {
                if (IsSpeakingReview)
                {
                    StatusLine = "Wait for the spoken review to finish.";
                    return;
                }

                string currentSpokenKey = BuildSpokenKey(destination.DestinationId, destination.Adapter.BoundWindow?.Handle ?? 0, draftSnapshot);
                if (!string.Equals(_lastSpokenKey, currentSpokenKey, StringComparison.Ordinal))
                {
                    MaybeSpeakReview();
                    StatusLine = "The changed draft must be read aloud before sending.";
                    return;
                }
            }

            // Re-probe now: the picker's status may be seconds old.
            destination.Status = destination.Adapter.Probe();
            if (!destination.Status.CanSend)
            {
                State = WidgetState.Error;
                ErrorMessage = destination.Status.Detail;
                StatusLine = $"Not sent — {destination.DisplayName} is not ready";
                _controller?.Start();
                return;
            }

            ConfirmedDraft confirmed;
            try
            {
                confirmed = new ConfirmedDraft(draftSnapshot, destination.DestinationId);
            }
            catch (ArgumentException ex)
            {
                State = WidgetState.Error;
                ErrorMessage = ex.Message;
                _controller?.Start();
                return;
            }

            State = WidgetState.Sending;
            IsDraftEditable = false;
            StatusLine = $"Sending to {destination.DisplayName}...";
            SendingStarted?.Invoke(this, new WidgetSendStartingEventArgs(confirmed.Text, destination.DestinationId, destination.DisplayName));

            SendResult result;
            try
            {
                result = await destination.Adapter.SendAsync(confirmed).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                result = new SendResult(SendStatus.Failed, ex.Message, 0);
            }
            finally
            {
                IsDraftEditable = true;
            }

            SendCompleted?.Invoke(this, new WidgetSendCompletedEventArgs(confirmed.Text, destination.DisplayName, result));

            if (result.Succeeded)
            {
                State = WidgetState.Sent;
                StatusLine = $"Sent to {destination.DisplayName} ({result.ElapsedMilliseconds} ms)";
                LastSentText = confirmed.Text;
                ErrorMessage = string.Empty;
                AgentRunStarting?.Invoke(this, new AgentRunEventArgs(destination.Adapter, _draftOriginPhone, draftSnapshot));
                _controller?.Start();
                return;
            }

            // Not sent. Leave the draft exactly as it was so the user can retry or fix the target.
            AgentRunCancelled?.Invoke(this, EventArgs.Empty);
            State = WidgetState.Error;
            ErrorMessage = result.Detail;
            StatusLine = $"NOT sent to {destination.DisplayName}";
            _controller?.Start();
        }
        finally
        {
            Interlocked.Exchange(ref _isSending, 0);
        }
    }

    /// <summary>
    /// Reads the visible draft and selected destination aloud, once per draft/destination pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Speech needs both a draft and an explicitly chosen destination, because the review states
    /// the destination out loud. With a draft but no destination the widget waits and says so
    /// rather than reading a prompt with no target.
    /// </para>
    /// <para>
    /// Capture is stopped for the whole spoken window and restarted afterwards, so the microphone
    /// cannot pick up the app's own speech. Finishing the review never sends anything: it only
    /// plays the chime and reports that approval may begin. Confirm and Cancel stay live
    /// throughout as fallback controls.
    /// </para>
    /// </remarks>
    private void MaybeSpeakReview()
    {
        ISpokenReview? speech = _draftOriginPhone ? _phoneSpeech : _speech;
        if (_draftOriginPhone && speech == null)
        {
            SpeechStatus = "Phone voice unavailable: phone speech not configured.";
            return;
        }

        if (speech == null || State != WidgetState.Confirm)
        {
            return;
        }

        string draft = DraftText;
        DestinationOption? destination = SelectedDestination;

        if (string.IsNullOrWhiteSpace(draft))
        {
            return;
        }

        if (destination == null)
        {
            SpeechStatus = "Choose a destination by voice.";
            _ = AskForDestinationAsync();
            return;
        }

        TryRestoreOrBind(destination);
        DestinationStatus probeStatus = destination.Adapter.Probe();
        destination.Status = probeStatus;
        DestinationName = FormatDestinationWithWindow(destination.DisplayName, destination.Adapter.BoundWindow);

        int generation = Volatile.Read(ref _utteranceGeneration);
        int reviewGeneration = Interlocked.Increment(ref _spokenReviewGeneration);

        if (!probeStatus.CanSend)
        {
            _ = RunWindowSelectionFlowAsync(generation, reviewGeneration, destination, speech);
            return;
        }

        // One reading per draft and destination, so re-probing or a redundant property change
        // cannot make it speak twice.
        string key = BuildSpokenKey(destination.DestinationId, destination.Adapter.BoundWindow?.Handle ?? 0, draft);
        if (string.Equals(_lastSpokenKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _lastSpokenKey = key;

        string destinationName = DestinationName;
        State = WidgetState.ReadingDraft;
        IsSpeakingReview = true;
        SpeechStatus = "Reading the draft aloud...";
        _controller?.Stop();

        _ = Task.Run(async () =>
        {
            SpokenReviewResult result;
            try
            {
                result = ShortReview
                    ? await speech.SpeakPromptAsync($"Send the displayed prompt to {destinationName}?", _processingCts?.Token ?? default).ConfigureAwait(false)
                    : await speech.SpeakReviewAsync(draft, destinationName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new SpokenReviewResult(SpokenReviewOutcome.Failed, null, ex.Message);
            }

            _dispatchAction(() =>
            {
                // A destination change or newer review cancelled this completion. It must not
                // reopen capture or overwrite the state owned by the newer spoken review.
                if (Volatile.Read(ref _spokenReviewGeneration) != reviewGeneration)
                {
                    return;
                }

                IsSpeakingReview = false;

                // A newer utterance started while this was reading; its own review owns the UI.
                if (!IsCurrent(generation))
                {
                    _controller?.Start();
                    return;
                }

                switch (result.Outcome)
                {
                    case SpokenReviewOutcome.Completed:
                        SpeechStatus = result.Timings?.Summary ?? "Review spoken.";
                        StatusLine = $"Send this to {destinationName}, or redictate?";
                        IApprovalListener? activeListener = _draftOriginPhone ? _phoneApprovalListener : _approvalListener;
                        if (activeListener != null)
                        {
                            State = WidgetState.AwaitingApproval;
                            // Keep hotkey disabled during approval listening.
                            _ = RunApprovalListeningLoopAsync(generation, reviewGeneration, destination);
                        }
                        else
                        {
                            State = WidgetState.Confirm;
                            _controller?.Start();
                        }
                        break;

                    case SpokenReviewOutcome.Cancelled:
                        SpeechStatus = "Reading cancelled.";
                        State = WidgetState.Confirm;
                        _controller?.Start();
                        break;

                    default:
                        // Speech is a convenience; losing it must not block the send path.
                        SpeechStatus = $"Voice unavailable: {result.FailureDetail}";
                        StatusLine = "Review the draft, then confirm";
                        State = WidgetState.Confirm;
                        _controller?.Start();
                        break;
                }
            });
        });
    }

    private static string BuildSpokenKey(string destinationId, long windowHandle, string draft) =>
        destinationId + "\u001f" + windowHandle + "\u001f" + draft;

    public static string FormatDestinationWithWindow(string appName, WindowCandidate? boundWindow)
    {
        if (boundWindow == null || string.IsNullOrWhiteSpace(boundWindow.Title))
        {
            return appName;
        }

        return $"{appName} ({boundWindow.Title})";
    }

    /// <summary>Re-probes every destination and refreshes the picker.</summary>
    public void RefreshDestinations()
    {
        foreach (DestinationOption option in Destinations)
        {
            option.Status = option.Adapter.Probe();
        }

        if (SelectedDestination != null)
        {
            DestinationName = FormatDestinationWithWindow(SelectedDestination.DisplayName, SelectedDestination.Adapter.BoundWindow);
        }

        UpdateWindowChoices();
        DestinationStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Binds the selected destination to the exact window the user picked.</summary>
    public void BindSelectedWindow()
    {
        DestinationOption? destination = SelectedDestination;
        WindowCandidate? window = SelectedWindowChoice;

        if (destination == null || window == null)
        {
            return;
        }

        try
        {
            destination.Adapter.Bind(window);
            SavePreferences();
            destination.Status = destination.Adapter.Probe();
            DestinationName = FormatDestinationWithWindow(destination.DisplayName, destination.Adapter.BoundWindow);
            StatusLine = $"{destination.DisplayName} bound to {window.DisplayLabel}";
            ErrorMessage = string.Empty;
            DestinationStatusChanged?.Invoke(this, EventArgs.Empty);

            if (State == WidgetState.Error)
            {
                State = HasDraft ? WidgetState.Confirm : WidgetState.Idle;
            }
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
            State = WidgetState.Error;
        }

        UpdateWindowChoices();
        _lastSpokenKey = string.Empty;
        MaybeSpeakReview();
    }

    private void UpdateWindowChoices()
    {
        WindowChoices.Clear();

        DestinationOption? destination = SelectedDestination;
        if (destination == null)
        {
            OnPropertyChanged(nameof(NeedsWindowChoice));
            return;
        }

        foreach (WindowCandidate candidate in destination.Status.Candidates)
        {
            WindowChoices.Add(candidate);
        }

        // Pre-select only when there is exactly one option. With several, the user must choose:
        // picking for them is the substitution this slice forbids.
        SelectedWindowChoice = WindowChoices.Count == 1 ? WindowChoices[0] : null;
        OnPropertyChanged(nameof(NeedsWindowChoice));
    }

    /// <summary>
    /// Reports a held capture. Session listening shares the same device and raises the same
    /// event, but it is not a hold and must not be announced as one.
    /// </summary>
    /// <remarks>
    /// Treating it as a hold wedged the widget: the state moved to <c>Listening</c>, which the
    /// session does not consider a resting state, so the loop stopped listening and nothing
    /// released the state again. The controller only reports <c>IsCapturing</c> while the key
    /// is actually down, which is the distinction that matters here.
    /// </remarks>
    private void OnCaptureStateChanged(object? sender, CaptureStateChangedEventArgs e)
    {
        _dispatchAction(() =>
        {
            if (e.IsCapturing && _controller?.IsCapturing == true)
            {
                AgentRunCancelled?.Invoke(this, EventArgs.Empty);
                State = WidgetState.Listening;
                StatusLine = "Listening... release key to finish";
            }
        });
    }

    /// <summary>A tap, unlike a hold, carries no speech: it ends an open session.</summary>
    private void OnHotkeyTapped(object? sender, EventArgs e) =>
        _dispatchAction(() =>
        {
            if (IsSessionOpen)
            {
                EndSession($"Session ended — Hold {HotkeyLabel} to speak");
            }
        });

    /// <summary>
    /// Releases the shared microphone so a hold always wins over session listening.
    /// </summary>
    private void OnCapturePreparing(object? sender, EventArgs e) => _approvalListener?.Cancel();

    /// <summary>Opens a continuous session and starts waiting for follow-up speech.</summary>
    private void OpenSession()
    {
        if (_approvalListener == null || IsSessionOpen)
        {
            return;
        }

        IsSessionOpen = true;
        _sessionCts?.Dispose();
        _sessionCts = new CancellationTokenSource();
        CancellationToken token = _sessionCts.Token;
        _ = Task.Run(() => RunSessionLoopAsync(token), token);
    }

    /// <summary>Closes the session and returns the widget to plain push-to-talk.</summary>
    public void EndSession(string status)
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
        IsSessionOpen = false;
        _approvalListener?.Cancel();

        if (State == WidgetState.SessionListening)
        {
            State = HasDraft ? WidgetState.Confirm : WidgetState.Idle;
        }

        StatusLine = status;
        _controller?.Start();
    }

    /// <summary>
    /// Waits for the next hands-free utterance whenever the widget is at rest, and feeds it into
    /// the same pipeline a held capture uses.
    /// </summary>
    /// <remarks>
    /// The loop only listens from a resting state, never while the tool is speaking, processing,
    /// sending, or already listening for an approval. Each listening window is short and returns
    /// empty on silence, which bounds how much audio is ever retained while nobody is talking.
    /// </remarks>
    private async Task RunSessionLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && IsSessionOpen)
        {
            if (!CanListenInSession())
            {
                _dispatchAction(ReleaseStaleListeningState);

                try
                {
                    await Task.Delay(150, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            IApprovalListener? listener = _approvalListener;
            if (listener == null)
            {
                return;
            }

            _dispatchAction(() =>
            {
                // While barging in through a readback the speech owns the state; announcing
                // session listening there would replace what the user is being read.
                if (IsSessionOpen && CanListenInSession() && State != WidgetState.ReadingDraft)
                {
                    State = WidgetState.SessionListening;
                    StatusLine = $"Session open — just speak, or tap {HotkeyLabel} to end";
                }
            });

            byte[] audio;
            try
            {
                audio = await listener.ListenForSessionUtteranceAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                // The listener is busy with an approval; wait for it rather than fighting it.
                continue;
            }
            catch (Exception)
            {
                _dispatchAction(() => EndSession("Session listening failed — hold to speak"));
                return;
            }

            if (token.IsCancellationRequested || !IsSessionOpen)
            {
                return;
            }

            if (audio.Length == 0)
            {
                continue;
            }

            int generation = 0;
            CancellationToken processingToken = default;
            _dispatchAction(() =>
            {
                // BeginNewUtterance already silences the readback and any agent narration, so an
                // interruption stops the tool talking as a consequence of being heard.
                generation = BeginNewUtterance();
                _draftOriginPhone = false;
                processingToken = _processingCts!.Token;
            });

            ProcessAudioBytes(audio, generation, processingToken);
        }
    }

    /// <summary>
    /// Frees a listening state that nothing is actually listening for.
    /// </summary>
    /// <remarks>
    /// A hold that never reports its release, or a capture announced by something that has since
    /// gone away, would otherwise park the widget in a state the session refuses to act from.
    /// Phone captures are left alone; they have their own recovery and their own owner.
    /// </remarks>
    private void ReleaseStaleListeningState()
    {
        if (State != WidgetState.Listening ||
            _draftOriginPhone ||
            _controller?.IsCapturing == true ||
            _controller?.AudioCaptureService.IsCapturing == true)
        {
            return;
        }

        State = HasDraft ? WidgetState.Confirm : WidgetState.Idle;
        StatusLine = HasDraft
            ? "Review the draft, then confirm"
            : $"Ready — Hold {HotkeyLabel} to speak";
    }

    /// <summary>The session may only take the microphone when nothing else needs it.</summary>
    /// <remarks>
    /// Without barge-in the tool stops listening while it talks, so it cannot transcribe itself.
    /// With barge-in the microphone stays open through the readback, which only holds up on
    /// headphones; on speakers the tool hears its own voice and interrupts itself immediately.
    /// An approval listener is never pre-empted either way, because it is already listening.
    /// </remarks>
    private bool CanListenInSession()
    {
        if (!IsSessionOpen ||
            _controller?.AudioCaptureService.IsCapturing == true ||
            _approvalListener?.IsListening == true)
        {
            return false;
        }

        if (BargeInEnabled)
        {
            return State is WidgetState.Idle or WidgetState.Confirm or WidgetState.Sent
                or WidgetState.Error or WidgetState.SessionListening or WidgetState.ReadingDraft;
        }

        return !IsSpeakingReview &&
            NarrationActive?.Invoke() != true &&
            State is WidgetState.Idle or WidgetState.Confirm or WidgetState.Sent
                or WidgetState.Error or WidgetState.SessionListening;
    }

    /// <summary>
    /// Abandons any in-flight processing and claims a new utterance generation.
    /// </summary>
    /// <remarks>
    /// Cancellation alone cannot prevent a stale overwrite: a slow pipeline can finish just
    /// before the token is observed, so its continuation is already queued and will still
    /// dispatch. The generation number is the authority — a continuation applies its result
    /// only if it still owns the current generation.
    /// </remarks>
    private int BeginNewUtterance()
    {
        State = WidgetState.Processing;
        AgentRunCancelled?.Invoke(this, EventArgs.Empty);
        Interlocked.Increment(ref _spokenReviewGeneration);
        _approvalListener?.Cancel();
        _phoneApprovalListener?.Cancel();
        _speech?.Cancel();
        _phoneSpeech?.Cancel();
        _lastSpokenKey = string.Empty;
        if (IsSpeakingReview)
        {
            IsSpeakingReview = false;
            _controller?.Start();
        }
        _processingCts?.Cancel();
        _processingCts?.Dispose();
        _processingCts = new CancellationTokenSource();
        return Interlocked.Increment(ref _utteranceGeneration);
    }

    private bool IsCurrent(int generation) =>
        Volatile.Read(ref _utteranceGeneration) == generation;

    private void OnAudioCaptured(object? sender, byte[] audioBytes)
    {
        if (audioBytes.Length == 0)
        {
            _dispatchAction(() =>
            {
                // Still supersedes: a zero-length capture means the user started again.
                BeginNewUtterance();
                State = WidgetState.Idle;
                StatusLine = $"Ready — Hold {HotkeyLabel} to speak";
                _controller?.Start();
            });
            return;
        }

        double seconds = audioBytes.Length / 32000.0;

        if (_pipeline == null)
        {
            _dispatchAction(() =>
            {
                BeginNewUtterance();
                State = WidgetState.Idle;
                StatusLine = $"Captured {seconds:F1}s ({audioBytes.Length / 1024.0:F1} KB in memory) — Ready";
                _controller?.Start();
            });
            return;
        }

        int generation = 0;
        CancellationToken token = default;

        _dispatchAction(() =>
        {
            generation = BeginNewUtterance();
            _draftOriginPhone = false;
            token = _processingCts!.Token;
            // The first real hold opens a session; every later utterance can be hands free.
            OpenSession();
        });

        ProcessAudioBytes(audioBytes, generation, token);
    }

    private void ProcessAudioBytes(byte[] audioBytes, int generation, CancellationToken token)
    {
        double seconds = audioBytes.Length / 32000.0;

        _dispatchAction(() =>
        {
            if (!IsCurrent(generation)) return;
            State = WidgetState.Processing;
            RawTranscript = string.Empty;
            DraftText = string.Empty;
            StageTimings = string.Empty;
            StatusLine = $"Transcribing {seconds:F1}s...";
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var totalStopwatch = Stopwatch.StartNew();

                TranscriptionResult transcription = _pipeline!.TranscribeOnly(audioBytes, token);

                if (string.IsNullOrWhiteSpace(transcription.Text))
                {
                    _dispatchAction(() =>
                    {
                        if (!IsCurrent(generation)) return;
                        State = WidgetState.Idle;
                        StatusLine = $"No speech detected — Hold {HotkeyLabel} to speak";
                        _controller?.Start();
                    });
                    return;
                }

                string textToClean = transcription.Text;
                bool handled = false;
                _dispatchAction(() =>
                {
                    if (IsCurrent(generation)) handled = HandleConversationCommand(transcription.Text, duringApproval: false);
                });
                if (handled) return;

                // Destination voice prefix stripping and routing
                VoiceDestinationResolver? resolver = _destinationResolver;
                bool hasExplicitDestinationPrefix = false;
                if (resolver != null)
                {
                    VoiceDestinationResolution resolution = resolver.Resolve(transcription.Text);
                    if (resolution.Status == VoiceDestinationResolutionStatus.Resolved && !string.IsNullOrWhiteSpace(resolution.PromptText))
                    {
                        hasExplicitDestinationPrefix = true;
                    }
                }

                // If not handled deterministically and no explicit destination prefix, check if it is a conversational command
                if (!hasExplicitDestinationPrefix && _pipeline?.IntentInterpreter != null && !string.IsNullOrWhiteSpace(transcription.Text))
                {
                    try
                    {
                        InterpretedIntent? llmIntent = await _pipeline.IntentInterpreter.InterpretAsync(transcription.Text, token).ConfigureAwait(false);
                        if (llmIntent != null && IsCurrent(generation))
                        {
                            ConversationCommand? convCmd = llmIntent.ToConversationCommand();
                            if (convCmd != null)
                            {
                                _dispatchAction(() =>
                                {
                                    if (IsCurrent(generation))
                                        handled = ExecuteConversationCommand(convCmd, duringApproval: false);
                                });
                                if (handled) return;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Degrades to normal prompt processing
                    }
                }

                if (resolver != null)
                {
                    VoiceDestinationResolution resolution = resolver.Resolve(transcription.Text);
                    if (resolution.Status is VoiceDestinationResolutionStatus.Unknown or VoiceDestinationResolutionStatus.Ambiguous)
                    {
                        _dispatchAction(() => { if (IsCurrent(generation)) SelectedDestination = null; });
                    }
                    if (resolution.Status == VoiceDestinationResolutionStatus.Resolved && resolution.DestinationId != null)
                    {
                        _dispatchAction(() =>
                        {
                            DestinationOption? matched = Destinations.FirstOrDefault(d =>
                                string.Equals(d.DestinationId, resolution.DestinationId, StringComparison.OrdinalIgnoreCase));
                            if (matched != null)
                            {
                                SelectedDestination = matched;
                                ApplyAliasWindow(transcription.Text, matched);
                            }
                        });
                    }
                    textToClean = resolution.PromptText;
                }

                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(textToClean))
                {
                    _dispatchAction(() => { if (IsCurrent(generation)) StartRedictation(); });
                    return;
                }

                CleanupResult cleanup;
                if (!CleanupEnabled)
                {
                    cleanup = new CleanupResult(textToClean, 0, false, "Cleanup off");
                }
                else if (!string.IsNullOrWhiteSpace(textToClean))
                {
                    try
                    {
                        cleanup = await _pipeline!.Cleaner.CleanAsync(textToClean, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        cleanup = new CleanupResult(textToClean, 0, false, ex.Message);
                    }
                }
                else
                {
                    cleanup = new CleanupResult(string.Empty, 0, false, "Empty prompt");
                }

                totalStopwatch.Stop();

                _dispatchAction(() =>
                {
                    if (!IsCurrent(generation)) return;

                    RawTranscript = textToClean;
                    DraftText = cleanup.Text;
                    StageTimings = $"audio {seconds:F1}s · STT {transcription.ElapsedMilliseconds} ms · cleanup {cleanup.ElapsedMilliseconds} ms · total {totalStopwatch.ElapsedMilliseconds} ms";
                    State = WidgetState.Confirm;
                    StatusLine = cleanup.Applied
                        ? "Review the draft, then confirm"
                        : "Your words — review, then confirm";
                    MaybeSpeakReview();
                });
            }
            catch (OperationCanceledException)
            {
                // Cancelled
            }
            catch (Exception ex)
            {
                _dispatchAction(() =>
                {
                    if (!IsCurrent(generation)) return;
                    ErrorMessage = ex.Message;
                    State = WidgetState.Error;
                    StatusLine = "Processing failed";
                    _controller?.Start();
                });
            }
        }, token);
    }

    private async Task RunApprovalListeningLoopAsync(int utteranceGen, int reviewGen, DestinationOption destination)
    {
        IApprovalListener? listener = _draftOriginPhone ? _phoneApprovalListener : _approvalListener;
        if (listener == null)
        {
            return;
        }

        const int maxAttempts = 3;
        int attempt = 0;

        while (attempt < maxAttempts)
        {
            if (!IsCurrent(utteranceGen) ||
                Volatile.Read(ref _spokenReviewGeneration) != reviewGen ||
                State != WidgetState.AwaitingApproval)
            {
                return;
            }

            CancellationToken token = _processingCts?.Token ?? default;
            byte[] approvalAudio;
            try
            {
                approvalAudio = await listener.ListenForApprovalAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                _dispatchAction(() =>
                {
                    if (IsCurrent(utteranceGen) && Volatile.Read(ref _spokenReviewGeneration) == reviewGen)
                    {
                        State = WidgetState.Confirm;
                        StatusLine = "Approval listening failed — review the draft, then confirm";
                        _controller?.Start();
                    }
                });
                return;
            }

            if (!IsCurrent(utteranceGen) || Volatile.Read(ref _spokenReviewGeneration) != reviewGen || State != WidgetState.AwaitingApproval)
            {
                return;
            }

            // Silence timeout or empty audio
            if (approvalAudio.Length == 0)
            {
                _dispatchAction(() =>
                {
                    if (IsCurrent(utteranceGen) && Volatile.Read(ref _spokenReviewGeneration) == reviewGen && State == WidgetState.AwaitingApproval)
                    {
                        State = WidgetState.Confirm;
                        StatusLine = "Review the draft, then confirm";
                        _controller?.Start();
                    }
                });
                return;
            }

            // Transcribe using Parakeet ONLY — NEVER call cleaner for commands!
            string commandText = string.Empty;
            if (_pipeline != null)
            {
                try
                {
                    TranscriptionResult transResult = _pipeline.TranscribeOnly(approvalAudio, token);
                    commandText = transResult.Text;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    commandText = string.Empty;
                }
            }

            ApprovalCommand command = ApprovalCommandClassifier.Classify(commandText);

            if (!IsCurrent(utteranceGen) || Volatile.Read(ref _spokenReviewGeneration) != reviewGen || State != WidgetState.AwaitingApproval)
            {
                return;
            }

            LastApprovalStatus = $"Heard: \"{commandText}\" ({command})";
            bool commandHandled = false;
            _dispatchAction(() =>
            {
                if (IsCurrent(utteranceGen) && Volatile.Read(ref _spokenReviewGeneration) == reviewGen)
                    commandHandled = HandleConversationCommand(commandText, duringApproval: true);
            });
            if (commandHandled) return;

            // Fall through to Gemma intent interpretation if deterministic matching didn't recognize it
            if (command == ApprovalCommand.Unknown && _pipeline?.IntentInterpreter != null && !string.IsNullOrWhiteSpace(commandText))
            {
                try
                {
                    InterpretedIntent? llmIntent = await _pipeline.IntentInterpreter.InterpretAsync(commandText, token).ConfigureAwait(false);
                    if (llmIntent != null && IsCurrent(utteranceGen) && Volatile.Read(ref _spokenReviewGeneration) == reviewGen && State == WidgetState.AwaitingApproval)
                    {
                        ConversationCommand? convCmd = llmIntent.ToConversationCommand();
                        if (convCmd != null)
                        {
                            _dispatchAction(() =>
                            {
                                if (IsCurrent(utteranceGen) && Volatile.Read(ref _spokenReviewGeneration) == reviewGen)
                                    commandHandled = ExecuteConversationCommand(convCmd, duringApproval: true);
                            });
                            if (commandHandled)
                            {
                                LastApprovalStatus = $"Heard: \"{commandText}\" (interpreted: {convCmd.Kind})";
                                return;
                            }
                        }

                        ApprovalCommand llmApproval = llmIntent.ToApprovalCommand();
                        if (llmApproval != ApprovalCommand.Unknown)
                        {
                            command = llmApproval;
                            LastApprovalStatus = $"Heard: \"{commandText}\" (interpreted: {command})";
                        }
                    }
                }
                catch (Exception)
                {
                    // Degrades to standard Unknown handling
                }
            }

            switch (command)
            {
                case ApprovalCommand.Affirmative:
                    Task? send = null;
                    _dispatchAction(() => send = ConfirmAsync());
                    if (send != null) await send.ConfigureAwait(false);
                    return;

                case ApprovalCommand.Cancel:
                    _dispatchAction(Cancel);
                    return;

                case ApprovalCommand.Redictate:
                    _dispatchAction(StartRedictation);
                    return;

                case ApprovalCommand.UseOriginal:
                    _dispatchAction(UseOriginalDraft);
                    return;

                default: // Unknown
                    attempt++;
                    if (attempt < maxAttempts)
                    {
                        _dispatchAction(() =>
                        {
                            if (!IsCurrent(utteranceGen) || Volatile.Read(ref _spokenReviewGeneration) != reviewGen || State != WidgetState.AwaitingApproval)
                            {
                                return;
                            }

                            StatusLine = string.IsNullOrWhiteSpace(commandText)
                                ? $"Didn't catch that. Send to {destination.DisplayName}, or redictate?"
                                : $"Didn't catch \"{commandText}\". Send to {destination.DisplayName}, or redictate?";
                        });
                        var speech = _draftOriginPhone ? _phoneSpeech : _speech;
                        if (speech != null)
                        {
                            try
                            {
                                var retry = await speech.SpeakPromptAsync(
                                    $"Say send to confirm {destination.DisplayName}, redictate, or cancel.", token).ConfigureAwait(false);
                                if (!retry.ApprovalMayBegin) return;
                            }
                            catch (OperationCanceledException) { return; }
                        }
                    }
                    else
                    {
                        _dispatchAction(() =>
                        {
                            if (!IsCurrent(utteranceGen) || Volatile.Read(ref _spokenReviewGeneration) != reviewGen || State != WidgetState.AwaitingApproval)
                            {
                                return;
                            }

                            State = WidgetState.Confirm;
                            StatusLine = "Review the draft, then confirm";
                            _controller?.Start();
                        });
                        return;
                    }
                    break;
            }
        }
    }

    private void StartRedictation()
    {
        bool wasPhone = _draftOriginPhone;
        int generation = BeginNewUtterance();
        _draftOriginPhone = wasPhone;
        CancellationToken token = _processingCts!.Token;

        State = WidgetState.Redictating;
        DraftText = string.Empty;
        RawTranscript = string.Empty;
        StageTimings = string.Empty;
        ErrorMessage = string.Empty;
        StatusLine = "Listening for new draft... speak now";
        _controller?.Stop();

        IApprovalListener? listener = _draftOriginPhone ? _phoneApprovalListener : _approvalListener;
        if (listener == null)
        {
            State = WidgetState.Idle;
            StatusLine = $"Ready — Hold {HotkeyLabel} to speak";
            _controller?.Start();
            return;
        }

        _ = Task.Run(async () =>
        {
            byte[] redictatedAudio;
            try
            {
                redictatedAudio = await listener.ListenForReplacementDictationAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _dispatchAction(() =>
                {
                    if (IsCurrent(generation))
                    {
                        ErrorMessage = ex.Message;
                        State = WidgetState.Error;
                        StatusLine = "Redictation capture failed";
                        _controller?.Start();
                    }
                });
                return;
            }

            if (!IsCurrent(generation))
            {
                return;
            }

            if (redictatedAudio.Length == 0)
            {
                _dispatchAction(() =>
                {
                    if (IsCurrent(generation))
                    {
                        State = WidgetState.Idle;
                        StatusLine = $"Ready — Hold {HotkeyLabel} to speak";
                        _controller?.Start();
                    }
                });
                return;
            }

            ProcessAudioBytes(redictatedAudio, generation, token);
        });
    }

    public void UseOriginalDraft()
    {
        if (string.IsNullOrWhiteSpace(RawTranscript))
        {
            return;
        }

        _lastSpokenKey = string.Empty;
        if (!string.Equals(DraftText, RawTranscript, StringComparison.Ordinal))
        {
            DraftText = RawTranscript;
        }
        else
        {
            State = WidgetState.Confirm;
            MaybeSpeakReview();
        }

        StatusLine = "Reverted to raw transcript — review, then confirm";
    }

    private async Task RunWindowSelectionFlowAsync(
        int utteranceGen,
        int reviewGen,
        DestinationOption destination,
        ISpokenReview speech)
    {
        IApprovalListener? listener = _draftOriginPhone ? _phoneApprovalListener : _approvalListener;
        if (listener == null)
        {
            _dispatchAction(() =>
            {
                State = WidgetState.Confirm;
                _controller?.Start();
            });
            return;
        }

        const int maxAttempts = 3;
        int attempt = 0;

        while (attempt < maxAttempts)
        {
            if (!IsCurrent(utteranceGen) ||
                Volatile.Read(ref _spokenReviewGeneration) != reviewGen)
            {
                return;
            }

            CancellationToken token = _processingCts?.Token ?? default;

            // Probe candidates
            destination.Status = destination.Adapter.Probe();
            IReadOnlyList<WindowCandidate> candidates = destination.Status.Candidates;
            _dispatchAction(UpdateWindowChoices);

            string prompt;
            if (candidates.Count == 0)
            {
                prompt = $"{destination.Status.Detail}. Say refresh windows to try again, or cancel.";
                _dispatchAction(() =>
                {
                    StatusLine = prompt;
                });
            }
            else if (candidates.Count == 1)
            {
                string title = string.IsNullOrWhiteSpace(candidates[0].Title) ? candidates[0].ProcessName : candidates[0].Title;
                prompt = $"Found one {destination.DisplayName} window: {title}. Use this window?";
                _dispatchAction(() =>
                {
                    StatusLine = $"Found one {destination.DisplayName} window: {title}. Say \"use that window\" or \"yes\" to bind.";
                });
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"Found {candidates.Count} {destination.DisplayName} windows. ");
                string[] numberWords = { "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };
                for (int i = 0; i < candidates.Count; i++)
                {
                    string num = i < numberWords.Length ? numberWords[i] : (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string title = string.IsNullOrWhiteSpace(candidates[i].Title) ? candidates[i].ProcessName : candidates[i].Title;
                    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"Window {num}: {title}. ");
                }
                sb.Append("Which window would you like to use?");
                prompt = sb.ToString();
                _dispatchAction(() =>
                {
                    StatusLine = $"Choose {destination.DisplayName} window: say \"window one\", \"window two\", or window title.";
                });
            }

            _dispatchAction(() =>
            {
                if (IsCurrent(utteranceGen) && Volatile.Read(ref _spokenReviewGeneration) == reviewGen)
                {
                    State = WidgetState.ReadingDraft;
                    IsSpeakingReview = true;
                    SpeechStatus = "Asking for window selection...";
                    _controller?.Stop();
                }
            });

            SpokenReviewResult speakResult;
            try
            {
                speakResult = await speech.SpeakPromptAsync(prompt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                speakResult = new SpokenReviewResult(SpokenReviewOutcome.Failed, null, ex.Message);
            }

            if (!IsCurrent(utteranceGen) || Volatile.Read(ref _spokenReviewGeneration) != reviewGen)
            {
                return;
            }

            if (speakResult.Outcome == SpokenReviewOutcome.Cancelled)
            {
                _dispatchAction(() =>
                {
                    IsSpeakingReview = false;
                    State = WidgetState.Confirm;
                    _controller?.Start();
                });
                return;
            }

            if (speakResult.Outcome == SpokenReviewOutcome.Failed)
            {
                _dispatchAction(() =>
                {
                    IsSpeakingReview = false;
                    SpeechStatus = $"Voice unavailable: {speakResult.FailureDetail}";
                    State = WidgetState.Confirm;
                    _controller?.Start();
                });
                return;
            }

            _dispatchAction(() =>
            {
                IsSpeakingReview = false;
                State = WidgetState.AwaitingApproval;
                SpeechStatus = "Listening for window choice...";
            });

            byte[] audio;
            try
            {
                audio = await listener.ListenForApprovalAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                _dispatchAction(() =>
                {
                    if (IsCurrent(utteranceGen) && Volatile.Read(ref _spokenReviewGeneration) == reviewGen)
                    {
                        State = WidgetState.Confirm;
                        StatusLine = "Window selection listening failed — review or select manually";
                        _controller?.Start();
                    }
                });
                return;
            }

            if (!IsCurrent(utteranceGen) || Volatile.Read(ref _spokenReviewGeneration) != reviewGen || State != WidgetState.AwaitingApproval)
            {
                return;
            }

            if (audio.Length == 0)
            {
                attempt++;
                continue;
            }

            string heard = string.Empty;
            if (_pipeline != null)
            {
                try
                {
                    TranscriptionResult transResult = _pipeline.TranscribeOnly(audio, token);
                    heard = transResult.Text;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    heard = string.Empty;
                }
            }

            WindowSelectionResult selectionResult = WindowSelectionCommandClassifier.Classify(heard, candidates);
            LastApprovalStatus = $"Heard: \"{heard}\" ({selectionResult.Command})";

            switch (selectionResult.Command)
            {
                case WindowSelectionCommandType.Selected:
                    _dispatchAction(() =>
                    {
                        try
                        {
                            destination.Adapter.Bind(selectionResult.SelectedCandidate!);
                            SavePreferences();
                            destination.Status = destination.Adapter.Probe();
                            SelectedWindowChoice = selectionResult.SelectedCandidate;
                            UpdateWindowChoices();
                            DestinationName = FormatDestinationWithWindow(destination.DisplayName, destination.Adapter.BoundWindow);
                            StatusLine = $"{destination.DisplayName} bound to {selectionResult.SelectedCandidate!.DisplayLabel}";
                            DestinationStatusChanged?.Invoke(this, EventArgs.Empty);
                            _lastSpokenKey = string.Empty;
                            State = WidgetState.Confirm;
                            _controller?.Start();
                            MaybeSpeakReview();
                        }
                        catch (Exception ex)
                        {
                            ErrorMessage = ex.Message;
                            State = WidgetState.Error;
                            _controller?.Start();
                        }
                    });
                    return;

                case WindowSelectionCommandType.Refresh:
                    _dispatchAction(RefreshDestinations);
                    attempt = 0;
                    continue;

                case WindowSelectionCommandType.Repeat:
                    continue;

                case WindowSelectionCommandType.Cancel:
                    _dispatchAction(() =>
                    {
                        State = WidgetState.Confirm;
                        StatusLine = "Window selection cancelled — draft kept";
                        _controller?.Start();
                    });
                    return;

                default: // Unknown
                    attempt++;
                    if (attempt < maxAttempts)
                    {
                        string reprompt = candidates.Count == 1
                            ? "Didn't catch that. Say \"use that window\", or \"cancel\"."
                            : "Didn't catch that. Say \"window one\", \"window two\", \"repeat options\", or \"cancel\".";
                        try
                        {
                            await speech.SpeakPromptAsync(reprompt, token).ConfigureAwait(false);
                        }
                        catch { }
                    }
                    else
                    {
                        _dispatchAction(() =>
                        {
                            State = WidgetState.Confirm;
                            StatusLine = "Select a window to bind, then confirm";
                            _controller?.Start();
                        });
                        return;
                    }
                    break;
            }
        }
    }

    private void OnCaptureError(object? sender, CaptureErrorEventArgs e)
    {
        _dispatchAction(() =>
        {
            ErrorMessage = e.Message;
            State = WidgetState.Error;
            StatusLine = "Error occurred";
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        DetachController();
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
        _approvalListener?.Cancel();
        if (_approvalListener is IDisposable disposableListener)
        {
            disposableListener.Dispose();
        }
        _phoneApprovalListener?.Cancel();
        if (_phoneApprovalListener is IDisposable disposablePhoneListener)
        {
            disposablePhoneListener.Dispose();
        }
        _speech?.Cancel();
        if (_speech is IDisposable disposableSpeech)
        {
            disposableSpeech.Dispose();
        }
        _phoneSpeech?.Cancel();
        if (_phoneSpeech is IDisposable disposablePhoneSpeech)
        {
            disposablePhoneSpeech.Dispose();
        }
        _processingCts?.Cancel();
        _processingCts?.Dispose();
        _processingCts = null;
    }
}

public sealed class AgentRunEventArgs(IDestinationAdapter adapter, bool speakOnPhone, string confirmedPrompt) : EventArgs
{
    public IDestinationAdapter Adapter { get; } = adapter;
    public bool SpeakOnPhone { get; } = speakOnPhone;
    public string ConfirmedPrompt { get; } = confirmedPrompt;
}

public sealed class WidgetSendStartingEventArgs(string text, string destinationId, string destinationName) : EventArgs
{
    public string Text { get; } = text;
    public string DestinationId { get; } = destinationId;
    public string DestinationName { get; } = destinationName;
}

public sealed class WidgetSendCompletedEventArgs(string text, string destinationName, SendResult result) : EventArgs
{
    public string Text { get; } = text;
    public string DestinationName { get; } = destinationName;
    public SendResult Result { get; } = result;
}
