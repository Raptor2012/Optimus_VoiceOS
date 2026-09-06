namespace Optimus.Shell.ViewModels;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;

/// <summary>
/// Model for an agent question, plan review, or pending decision displayed in the companion capsule.
/// </summary>
public sealed class DecisionModel
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Title { get; init; } = "Decision Required";
    public string? Subtitle { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? ProjectName { get; init; }
    public string? TaskTitle { get; init; }
    public string PrimaryActionLabel { get; init; } = "Approve & Start";
    public string SecondaryActionLabel { get; init; } = "Request Changes";
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Partial class adding capsule-specific UI properties: expand/collapse,
/// pause listening, mute speech, end conversation, decision cards,
/// assistant response cards, destination locking, and Open in AO.
/// </summary>
public sealed partial class WidgetViewModel
{
    private bool _isExpanded;
    private bool _isPaused;
    private bool _isSpeechMuted;
    private double _micEnergy;
    private double _playbackEnergy;
    private DecisionModel? _currentDecision;
    private string _assistantResponseText = string.Empty;
    private string _assistantResponseHeadline = "ASSISTANT RESPONSE";
    private bool _isDestinationLocked = true;
    private string? _lockedDestinationLabel;

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
            if (Math.Abs(_micEnergy - value) > 0.005)
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
            if (Math.Abs(_playbackEnergy - value) > 0.005)
            {
                _playbackEnergy = value;
                OnPropertyChanged();
            }
        }
    }

    #region Decision card properties & commands

    /// <summary>Pending decision card data, or null when no decision is pending.</summary>
    public DecisionModel? CurrentDecision
    {
        get => _currentDecision;
        set
        {
            if (!ReferenceEquals(_currentDecision, value))
            {
                _currentDecision = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPendingDecision));
                OnPropertyChanged(nameof(DecisionTitle));
                OnPropertyChanged(nameof(DecisionSubtitle));
                OnPropertyChanged(nameof(DecisionDescription));
                OnPropertyChanged(nameof(DecisionPrimaryLabel));
                OnPropertyChanged(nameof(DecisionSecondaryLabel));
                OnPropertyChanged(nameof(DecisionHasOptions));
                OnPropertyChanged(nameof(DecisionOptions));
            }
        }
    }

    public bool HasPendingDecision => _currentDecision != null;
    public string DecisionTitle => _currentDecision?.Title ?? "Decision Required";
    public string DecisionSubtitle => _currentDecision?.Subtitle ??
        (_currentDecision?.ProjectName != null ? $"{_currentDecision.ProjectName} · {_currentDecision.TaskTitle ?? "Plan Review"}" : string.Empty);
    public string DecisionDescription => _currentDecision?.Description ?? string.Empty;
    public string DecisionPrimaryLabel => _currentDecision?.PrimaryActionLabel ?? "Approve & Start";
    public string DecisionSecondaryLabel => _currentDecision?.SecondaryActionLabel ?? "Request Changes";
    public bool DecisionHasOptions => _currentDecision?.Options != null && _currentDecision.Options.Count > 0;
    public IReadOnlyList<string> DecisionOptions => _currentDecision?.Options ?? Array.Empty<string>();

    public event EventHandler<DecisionModel>? DecisionApproved;
    public event EventHandler<DecisionModel>? DecisionRejected;

    public ICommand ApproveDecisionCommand => new RelayCommand(ApproveDecision);
    public ICommand RejectDecisionCommand => new RelayCommand(RejectDecision);

    public void ApproveDecision()
    {
        if (_currentDecision == null) return;
        var d = _currentDecision;
        CurrentDecision = null;
        DecisionApproved?.Invoke(this, d);
        StatusLine = $"Approved: {d.Title}";
    }

    public void RejectDecision()
    {
        if (_currentDecision == null) return;
        var d = _currentDecision;
        CurrentDecision = null;
        DecisionRejected?.Invoke(this, d);
        StatusLine = $"Rejected: {d.Title}";
    }

    public void SetPendingDecision(DecisionModel decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        CurrentDecision = decision;
        IsExpanded = true;
    }

    public void ClearPendingDecision()
    {
        CurrentDecision = null;
    }

    #endregion

    #region Assistant response properties & commands

    /// <summary>The latest public response or progress update from the assistant/agent.</summary>
    public string AssistantResponseText
    {
        get => _assistantResponseText;
        set
        {
            if (_assistantResponseText != value)
            {
                _assistantResponseText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAssistantResponse));
            }
        }
    }

    /// <summary>Headline or destination attribution for the assistant response.</summary>
    public string AssistantResponseHeadline
    {
        get => _assistantResponseHeadline;
        set
        {
            if (_assistantResponseHeadline != value)
            {
                _assistantResponseHeadline = value;
                OnPropertyChanged();
            }
        }
    }

    public bool HasAssistantResponse => !string.IsNullOrWhiteSpace(_assistantResponseText);

    public ICommand ClearAssistantResponseCommand => new RelayCommand(() => AssistantResponseText = string.Empty);

    public void SetAssistantResponse(string headline, string text)
    {
        AssistantResponseHeadline = headline;
        AssistantResponseText = text;
    }

    #endregion

    #region Destination locking & labels

    /// <summary>
    /// Whether voice routing is locked to the selected destination card or alias.
    /// In conversation mode, browsing never silently changes the voice destination.
    /// </summary>
    public bool IsDestinationLocked
    {
        get => _isDestinationLocked;
        set
        {
            if (_isDestinationLocked != value)
            {
                _isDestinationLocked = value;
                OnPropertyChanged();
                NotifyCapsuleProperties();
            }
        }
    }

    /// <summary>Custom locked destination display label (e.g. project/task or voice alias).</summary>
    public string? LockedDestinationLabel
    {
        get => _lockedDestinationLabel;
        set
        {
            if (_lockedDestinationLabel != value)
            {
                _lockedDestinationLabel = value;
                OnPropertyChanged();
                NotifyCapsuleProperties();
            }
        }
    }

    /// <summary>Toggle destination locking.</summary>
    public ICommand ToggleDestinationLockCommand => new RelayCommand(() => IsDestinationLocked = !IsDestinationLocked);

    /// <summary>Short destination label for the collapsed capsule.</summary>
    public string ShortDestinationLabel
    {
        get
        {
            string name = _lockedDestinationLabel ?? SelectedDestination?.DisplayName ?? "No target";
            string label = name.Length > 20 ? name[..17] + "..." : name;
            return _isDestinationLocked && SelectedDestination != null ? $"🔒 {label}" : label;
        }
    }

    /// <summary>Notify capsule properties when destination changes.</summary>
    private void NotifyCapsuleProperties()
    {
        OnPropertyChanged(nameof(ShortDestinationLabel));
        OnPropertyChanged(nameof(IsDestinationLocked));
    }

    #endregion

    #region Capsule commands

    /// <summary>Toggle expanded/collapsed capsule.</summary>
    public ICommand ToggleExpandedCommand => new RelayCommand(() => IsExpanded = !IsExpanded);

    /// <summary>Toggle pause listening.</summary>
    public ICommand TogglePauseCommand => new RelayCommand(() => IsPaused = !IsPaused);

    /// <summary>Toggle speech mute.</summary>
    public ICommand ToggleMuteCommand => new RelayCommand(() => IsSpeechMuted = !IsSpeechMuted);

    /// <summary>Redictate the current prompt (fallback control for mouse/accessibility).</summary>
    public ICommand RedictateCommand => new RelayCommand(StartRedictation);

    /// <summary>
    /// End conversation: stops voice processing, clears pending interaction.
    /// The agent's work continues; only the voice interaction is ended.
    /// </summary>
    public ICommand EndConversationCommand => new RelayCommand(EndConversation);

    public void EndConversation()
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

    /// <summary>
    /// Opens or brings forward the Agent Orchestrator desktop application or dashboard.
    /// </summary>
    public ICommand OpenInAoCommand => new RelayCommand(OpenInAo);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    public void OpenInAo()
    {
        try
        {
            var aoProcesses = Process.GetProcessesByName("agent-orchestrator");
            if (aoProcesses.Length > 0)
            {
                var p = aoProcesses[0];
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(p.MainWindowHandle);
                    StatusLine = "Switched to AO";
                    return;
                }
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string aoExe = Path.Combine(localAppData, "Programs", "agent-orchestrator", "agent-orchestrator.exe");
            if (File.Exists(aoExe))
            {
                Process.Start(new ProcessStartInfo(aoExe) { UseShellExecute = true });
                StatusLine = "Launching AO";
                return;
            }

            Process.Start(new ProcessStartInfo("http://localhost:3001") { UseShellExecute = true });
            StatusLine = "Opened AO in browser";
        }
        catch (Exception ex)
        {
            StatusLine = $"Could not open AO: {ex.Message}";
        }
    }

    #endregion
}
