namespace Optimus.Shell.ViewModels;

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Optimus.Core.Audio;
using Optimus.Inference;
using Optimus.Shell.Models;

public sealed class WidgetViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Action<Action> _dispatchAction;
    private PushToTalkController? _controller;
    private VoicePipeline? _pipeline;
    private CancellationTokenSource? _processingCts;
    private WidgetState _state = WidgetState.Idle;
    private string _statusLine = "Ready — Hold F8 to speak";
    private string _draftText = string.Empty;
    private string _rawTranscript = string.Empty;
    private string _stageTimings = string.Empty;
    private string _destinationName = "Claude (configured)";
    private string _errorMessage = string.Empty;
    private string _hotkeyLabel = "F8";

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

    public void DismissError()
    {
        ErrorMessage = string.Empty;
        State = WidgetState.Idle;
        StatusLine = $"Ready — Hold {HotkeyLabel} to speak";
    }

    public void Cancel()
    {
        _processingCts?.Cancel();
        DraftText = string.Empty;
        RawTranscript = string.Empty;
        StageTimings = string.Empty;
        ErrorMessage = string.Empty;
        State = WidgetState.Idle;
        StatusLine = $"Cancelled — Hold {HotkeyLabel} to speak";
    }

    public void Confirm()
    {
        State = WidgetState.Sending;
        StatusLine = $"Sending to {DestinationName}...";
        // Sent transition will be wired in S003
        State = WidgetState.Sent;
        StatusLine = $"Sent to {DestinationName}";
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

    private void OnAudioCaptured(object? sender, byte[] audioBytes)
    {
        if (audioBytes.Length == 0)
        {
            _dispatchAction(() =>
            {
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
                State = WidgetState.Idle;
                StatusLine = $"Captured {seconds:F1}s ({audioBytes.Length / 1024.0:F1} KB in memory) — Ready";
            });
            return;
        }

        _dispatchAction(() =>
        {
            State = WidgetState.Processing;
            RawTranscript = string.Empty;
            DraftText = string.Empty;
            StageTimings = string.Empty;
            StatusLine = $"Transcribing {seconds:F1}s...";
        });

        _processingCts?.Dispose();
        _processingCts = new CancellationTokenSource();
        CancellationToken token = _processingCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                VoicePipelineResult result = await _pipeline.ProcessAsync(audioBytes, token).ConfigureAwait(false);

                _dispatchAction(() =>
                {
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
