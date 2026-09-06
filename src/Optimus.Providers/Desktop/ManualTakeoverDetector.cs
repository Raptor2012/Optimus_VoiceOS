namespace Optimus.Providers.Desktop;

using System;

/// <summary>
/// Event arguments when manual user takeover is detected.
/// </summary>
public sealed class TakeoverEventArgs : EventArgs
{
    public TakeoverEventArgs(string inputType, DateTimeOffset timestamp)
    {
        InputType = inputType;
        Timestamp = timestamp;
    }

    public string InputType { get; }

    public DateTimeOffset Timestamp { get; }
}

/// <summary>
/// Detects physical user input during automation to allow graceful pause and takeover.
/// </summary>
public interface IManualTakeoverDetector
{
    bool IsTakeoverDetected { get; }

    DateTimeOffset? LastTakeoverTimeUtc { get; }

    string? LastTakeoverReason { get; }

    void RecordPhysicalInput(string inputType = "HardwareInput");

    void Reset();

    bool CanResume(DateTimeOffset observationTimestamp);

    void ResumeWithFreshObservation(DateTimeOffset freshObservationTimestamp);

    event EventHandler<TakeoverEventArgs>? TakeoverTriggered;
}

/// <summary>
/// Default implementation of manual takeover detection. Real hardware input pauses execution,
/// and resuming requires a fresh observation taken strictly after the takeover event.
/// </summary>
public sealed class ManualTakeoverDetector : IManualTakeoverDetector
{
    /// <summary>
    /// Magic signature placed in SendInput dwExtraInfo to distinguish synthetic input from user input.
    /// 0x4F505449 represents ASCII 'OPTI'.
    /// </summary>
    public static readonly IntPtr SyntheticInputSignature = new(0x4F505449);

    private readonly object _lock = new();
    private bool _isTakeoverDetected;
    private DateTimeOffset? _lastTakeoverTimeUtc;
    private string? _lastTakeoverReason;

    public event EventHandler<TakeoverEventArgs>? TakeoverTriggered;

    public bool IsTakeoverDetected
    {
        get
        {
            lock (_lock)
            {
                return _isTakeoverDetected;
            }
        }
    }

    public DateTimeOffset? LastTakeoverTimeUtc
    {
        get
        {
            lock (_lock)
            {
                return _lastTakeoverTimeUtc;
            }
        }
    }

    public string? LastTakeoverReason
    {
        get
        {
            lock (_lock)
            {
                return _lastTakeoverReason;
            }
        }
    }

    /// <summary>
    /// Called when real user physical input (keyboard, mouse, touch) is detected.
    /// </summary>
    public void RecordPhysicalInput(string inputType = "HardwareInput")
    {
        TakeoverEventArgs args;
        lock (_lock)
        {
            _isTakeoverDetected = true;
            _lastTakeoverTimeUtc = DateTimeOffset.UtcNow;
            _lastTakeoverReason = $"Manual user takeover detected via physical {inputType}.";
            args = new TakeoverEventArgs(inputType, _lastTakeoverTimeUtc.Value);
        }

        TakeoverTriggered?.Invoke(this, args);
    }

    /// <summary>
    /// Evaluates if an input event is synthetic or real physical user input.
    /// </summary>
    public void ProcessRawInputEvent(IntPtr extraInfo, bool isInjectedFlag, string inputType = "HardwareInput")
    {
        if (extraInfo == SyntheticInputSignature || isInjectedFlag)
        {
            // Input was synthesized by Optimus; ignore it.
            return;
        }

        // Hardware input from the user.
        RecordPhysicalInput(inputType);
    }

    /// <summary>
    /// Clears the takeover state unconditionally.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _isTakeoverDetected = false;
            _lastTakeoverReason = null;
        }
    }

    /// <summary>
    /// Checks whether execution can resume with the given observation timestamp.
    /// Resuming requires a fresh observation taken strictly after the takeover occurred.
    /// </summary>
    public bool CanResume(DateTimeOffset observationTimestamp)
    {
        lock (_lock)
        {
            if (!_isTakeoverDetected)
            {
                return true;
            }

            if (!_lastTakeoverTimeUtc.HasValue)
            {
                return true;
            }

            return observationTimestamp > _lastTakeoverTimeUtc.Value;
        }
    }

    /// <summary>
    /// Resumes execution only if the observation is verified to be fresh (newer than the takeover).
    /// </summary>
    public void ResumeWithFreshObservation(DateTimeOffset freshObservationTimestamp)
    {
        lock (_lock)
        {
            if (!_isTakeoverDetected)
            {
                return;
            }

            if (_lastTakeoverTimeUtc.HasValue && freshObservationTimestamp <= _lastTakeoverTimeUtc.Value)
            {
                throw new InvalidOperationException(
                    $"Cannot resume execution: observation timestamp ({freshObservationTimestamp:O}) is not newer than takeover event ({_lastTakeoverTimeUtc.Value:O}). A fresh observation is required.");
            }

            _isTakeoverDetected = false;
            _lastTakeoverReason = null;
        }
    }
}
