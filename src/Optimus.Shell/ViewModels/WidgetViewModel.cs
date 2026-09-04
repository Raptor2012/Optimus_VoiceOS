namespace Optimus.Shell.ViewModels;

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Optimus.Core.Audio;
using Optimus.Inference;
using Optimus.Providers;
using Optimus.Providers.Windows;
using Optimus.Shell.Models;

public sealed class WidgetViewModel : INotifyPropertyChanged, IDisposable
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
    private string _destinationName = "Claude (configured)";
    private string _errorMessage = string.Empty;
    private string _hotkeyLabel = "F8";
    private DestinationOption? _selectedDestination;
    private WindowCandidate? _selectedWindowChoice;
    private bool _isDraftEditable = true;
    private string _lastSentText = string.Empty;
    private string _phoneStatus = string.Empty;

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
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(StatusBadgeColor));
                OnPropertyChanged(nameof(IsDraftVisible));
                OnPropertyChanged(nameof(IsConfirmPanelVisible));
            }
        }
    }

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
        HasDraft && State is WidgetState.Confirm or WidgetState.Sending or WidgetState.Error;

    /// <summary>A failed send remains retryable without another recording.</summary>
    public bool IsConfirmPanelVisible =>
        HasDraft && State is WidgetState.Confirm or WidgetState.Error;

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
                    DestinationName = value.DisplayName;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedDestination));
                UpdateWindowChoices();
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
    public bool IsConfirming => State == WidgetState.Confirm;
    public bool IsError => State == WidgetState.Error;
    public bool IsIdle => State == WidgetState.Idle;

    public string StatusBadgeColor => State switch
    {
        WidgetState.Listening => "#FF3B30", // Red recording indicator
        WidgetState.Processing => "#FF9500", // Orange
        WidgetState.Confirm => "#0A84FF", // Blue
        WidgetState.Sending => "#5E5CE6", // Purple
        WidgetState.Sent => "#30D158", // Green
        WidgetState.Error => "#FF453A", // Red
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
    }

    public void DetachController()
    {
        if (_controller != null)
        {
            _controller.StateChanged -= OnCaptureStateChanged;
            _controller.AudioCaptured -= OnAudioCaptured;
            _controller.ErrorOccurred -= OnCaptureError;
            _controller = null;
        }
    }

    /// <summary>Supplies the STT + cleanup pipeline. Without one, capture still works and the
    /// widget reports the captured audio only.</summary>
    public void AttachPipeline(VoicePipeline pipeline) =>
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));

    /// <summary>Loads the configured destinations. No destination is selected by default.</summary>
    public void AttachDestinations(DestinationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        Destinations.Clear();
        foreach ((IDestinationAdapter adapter, DestinationStatus status) in registry.ProbeAll())
        {
            Destinations.Add(new DestinationOption(adapter, status));
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
        RawTranscript = text;
        DraftText = text;
        StageTimings = "manual draft · STT skipped · cleanup skipped";
        ErrorMessage = string.Empty;
        IsDraftEditable = true;
        State = WidgetState.Confirm;
        StatusLine = "Manual draft loaded — choose a destination, review, then confirm";
    }

    /// <summary>
    /// Shows a draft that came from the phone, using the same confirm flow as a desktop
    /// utterance so there is one gate, not two.
    /// </summary>
    public void LoadPhoneDraft(string rawTranscript, string cleanedDraft, string timings)
    {
        BeginNewUtterance();
        RawTranscript = rawTranscript;
        DraftText = cleanedDraft;
        StageTimings = timings;
        ErrorMessage = string.Empty;
        IsDraftEditable = true;
        State = WidgetState.Confirm;
        StatusLine = "Phone draft — review, then confirm";
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
        DraftText = string.Empty;
        RawTranscript = string.Empty;
        StageTimings = string.Empty;
        ErrorMessage = string.Empty;
        State = WidgetState.Idle;
        StatusLine = $"Cancelled — Hold {HotkeyLabel} to speak";
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
        if (State == WidgetState.Sending)
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

        // Re-probe now: the picker's status may be seconds old.
        destination.Status = destination.Adapter.Probe();
        if (!destination.Status.CanSend)
        {
            State = WidgetState.Error;
            ErrorMessage = destination.Status.Detail;
            StatusLine = $"Not sent — {destination.DisplayName} is not ready";
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
            return;
        }

        State = WidgetState.Sending;
        IsDraftEditable = false;
        StatusLine = $"Sending to {destination.DisplayName}...";

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

        if (result.Succeeded)
        {
            State = WidgetState.Sent;
            StatusLine = $"Sent to {destination.DisplayName} ({result.ElapsedMilliseconds} ms)";
            LastSentText = confirmed.Text;
            ErrorMessage = string.Empty;
            return;
        }

        // Not sent. Leave the draft exactly as it was so the user can retry or fix the target.
        State = WidgetState.Error;
        ErrorMessage = result.Detail;
        StatusLine = $"NOT sent to {destination.DisplayName}";
    }

    /// <summary>Re-probes every destination and refreshes the picker.</summary>
    public void RefreshDestinations()
    {
        foreach (DestinationOption option in Destinations)
        {
            option.Status = option.Adapter.Probe();
        }

        UpdateWindowChoices();
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
            destination.Status = destination.Adapter.Probe();
            StatusLine = $"{destination.DisplayName} bound to {window.DisplayLabel}";
            ErrorMessage = string.Empty;

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

    private void OnCaptureStateChanged(object? sender, CaptureStateChangedEventArgs e)
    {
        _dispatchAction(() =>
        {
            if (e.IsCapturing)
            {
                State = WidgetState.Listening;
                StatusLine = "Listening... release key to finish";
            }
        });
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
            });
            return;
        }

        // 16 kHz mono PCM16 is 32,000 bytes per second.
        double seconds = audioBytes.Length / 32000.0;

        if (_pipeline == null)
        {
            _dispatchAction(() =>
            {
                BeginNewUtterance();
                State = WidgetState.Idle;
                StatusLine = $"Captured {seconds:F1}s ({audioBytes.Length / 1024.0:F1} KB in memory) — Ready";
            });
            return;
        }

        int generation = 0;
        CancellationToken token = default;

        _dispatchAction(() =>
        {
            generation = BeginNewUtterance();
            token = _processingCts!.Token;

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
                VoicePipelineResult result = await _pipeline.ProcessAsync(audioBytes, token).ConfigureAwait(false);

                _dispatchAction(() =>
                {
                    // A newer recording (or a cancel) started while this one was decoding.
                    if (!IsCurrent(generation))
                    {
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(result.RawTranscript))
                    {
                        State = WidgetState.Idle;
                        StatusLine = $"No speech detected — Hold {HotkeyLabel} to speak";
                        return;
                    }

                    RawTranscript = result.RawTranscript;
                    DraftText = result.CleanedDraft;
                    StageTimings = result.TimingSummary;
                    State = WidgetState.Confirm;
                    StatusLine = result.CleanupApplied
                        ? "Review the draft, then confirm"
                        : $"Cleanup unavailable ({result.CleanupUnavailableReason}) — showing raw transcript";
                });
            }
            catch (OperationCanceledException)
            {
                // Cancel() already reset the widget.
            }
            catch (Exception ex)
            {
                _dispatchAction(() =>
                {
                    // A superseded utterance must not raise an error over the live one.
                    if (!IsCurrent(generation))
                    {
                        return;
                    }

                    ErrorMessage = ex.Message;
                    State = WidgetState.Error;
                    StatusLine = "Processing failed";
                });
            }
        }, token);
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
        _processingCts?.Cancel();
        _processingCts?.Dispose();
        _processingCts = null;
    }
}
