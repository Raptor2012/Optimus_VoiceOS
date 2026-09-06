namespace Optimus.Shell.ViewModels;

using System.Windows.Input;

/// <summary>
/// Partial class adding capsule-specific UI properties: expand/collapse,
/// pause listening, mute speech, and end conversation commands.
/// </summary>
public sealed partial class WidgetViewModel
{
    private bool _isExpanded;
    private bool _isPaused;
    private bool _isSpeechMuted;
    private double _micEnergy;
    private double _playbackEnergy;

    /// <summary>Whether the capsule is showing its expanded panel.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Pause listening: stops mic capture but retains the conversation context.
    /// Draft, destination, and session state are preserved.
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused != value)
            {
                _isPaused = value;
                OnPropertyChanged();

                if (_isPaused)
                {
                    _controller?.Stop();
                    _approvalListener?.Cancel();
                }
                else
                {
                    _controller?.Start();
                }
            }
        }
    }

    /// <summary>
    /// Mute speech: suppresses TTS playback but retains visual updates.
    /// The orb still animates and labels still update, but no audio plays.
    /// </summary>
    public bool IsSpeechMuted
    {
        get => _isSpeechMuted;
        set
        {
            if (_isSpeechMuted != value)
            {
                _isSpeechMuted = value;
                OnPropertyChanged();
                NarrationMuteChanged?.Invoke(_isSpeechMuted);
            }
        }
    }

    /// <summary>Normalised microphone energy [0..1] for orb animation.</summary>
    public double MicEnergy
    {
        get => _micEnergy;
        set
        {
            if (System.Math.Abs(_micEnergy - value) > 0.005)
            {
                _micEnergy = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Normalised playback energy [0..1] for orb animation.</summary>
    public double PlaybackEnergy
    {
        get => _playbackEnergy;
        set
        {
            if (System.Math.Abs(_playbackEnergy - value) > 0.005)
            {
                _playbackEnergy = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Toggle expanded/collapsed capsule.</summary>
    public ICommand ToggleExpandedCommand => new RelayCommand(() => IsExpanded = !IsExpanded);

    /// <summary>Toggle pause listening.</summary>
    public ICommand TogglePauseCommand => new RelayCommand(() => IsPaused = !IsPaused);

    /// <summary>Toggle speech mute.</summary>
    public ICommand ToggleMuteCommand => new RelayCommand(() => IsSpeechMuted = !IsSpeechMuted);

    /// <summary>
    /// End conversation: stops voice processing, clears pending interaction.
    /// The agent's work continues; only the voice interaction is ended.
    /// </summary>
    public ICommand EndConversationCommand => new RelayCommand(EndConversation);

    private void EndConversation()
    {
        IsPaused = false;
        IsSpeechMuted = false;

        _approvalListener?.Cancel();
        _phoneApprovalListener?.Cancel();
        _speech?.Cancel();
        _phoneSpeech?.Cancel();
        _lastSpokenKey = string.Empty;

        if (IsSessionOpen)
        {
            EndSession($"Conversation ended — Hold {HotkeyLabel} to speak");
        }
        else
        {
            Cancel();
        }

        IsExpanded = false;
    }

    /// <summary>Short destination label for the collapsed capsule.</summary>
    public string ShortDestinationLabel
    {
        get
        {
            if (SelectedDestination == null) return "No target";
            string name = SelectedDestination.DisplayName;
            return name.Length > 20 ? name[..17] + "..." : name;
        }
    }

    /// <summary>Notify capsule properties when destination changes.</summary>
    private void NotifyCapsuleProperties()
    {
        OnPropertyChanged(nameof(ShortDestinationLabel));
    }
}
