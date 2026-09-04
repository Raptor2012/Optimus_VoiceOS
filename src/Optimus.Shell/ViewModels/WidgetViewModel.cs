namespace Optimus.Shell.ViewModels;

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Optimus.Core.Audio;
using Optimus.Shell.Models;

public sealed class WidgetViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Action<Action> _dispatchAction;
    private PushToTalkController? _controller;
    private WidgetState _state = WidgetState.Idle;
    private string _statusLine = "Ready — Hold F8 to speak";
    private string _draftText = string.Empty;
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

    public void DismissError()
    {
        ErrorMessage = string.Empty;
        State = WidgetState.Idle;
        StatusLine = $"Ready — Hold {HotkeyLabel} to speak";
    }

    public void Cancel()
    {
        DraftText = string.Empty;
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
        _dispatchAction(() =>
        {
            State = WidgetState.Idle;
            if (audioBytes.Length > 0)
            {
                // 16kHz mono 16-bit PCM has 32,000 bytes per second
                double seconds = (double)audioBytes.Length / 32000.0;
                double kb = audioBytes.Length / 1024.0;
                StatusLine = $"Captured {seconds:F1}s ({kb:F1} KB in memory) — Ready";
            }
            else
            {
                StatusLine = $"Ready — Hold {HotkeyLabel} to speak";
            }
        });
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
    }
}
