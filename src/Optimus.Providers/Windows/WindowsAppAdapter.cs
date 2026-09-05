namespace Optimus.Providers.Windows;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Sends a confirmed draft to one bound Windows application window by focusing it and
/// synthesizing keyboard input.
/// </summary>
/// <remarks>
/// <para>
/// The adapter never chooses a window on the user's behalf. It binds only to a window the user
/// picked, revalidates that exact window before every send, and fails when the binding is stale
/// or when several candidates exist. There is no "closest match" path, because sending a prompt
/// into the wrong application is worse than not sending it.
/// </para>
/// <para>
/// What success means here is bounded and worth stating: the adapter can prove the window was
/// found, was brought to the foreground, and accepted every synthesized keystroke. It cannot
/// prove the application parsed the text or acted on it, because that is behind the app's own
/// UI. <see cref="SendStatus.Sent"/> therefore means "delivered to the focused target", not
/// "the agent replied".
/// </para>
/// </remarks>
public sealed class WindowsAppAdapter : IDestinationAdapter
{
    /// <summary>Characters per SendInput batch; large batches get dropped by some apps.</summary>
    private const int ChunkSize = 200;

    private const int FocusAttempts = 20;
    private const int FocusPollMs = 25;

    private readonly object _lock = new();
    private WindowCandidate? _bound;

    public WindowsAppAdapter(string destinationId, string displayName, string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);

        DestinationId = destinationId;
        DisplayName = displayName;
        ProcessName = processName;
    }

    public string DestinationId { get; }

    public string DisplayName { get; }

    public string ProcessName { get; }

    public WindowCandidate? BoundWindow
    {
        get
        {
            lock (_lock)
            {
                return _bound;
            }
        }
    }

    /// <summary>Delay between focusing and typing. Raise if an app misses leading characters.</summary>
    public int FocusSettleMs { get; set; } = 120;

    public DestinationStatus Probe()
    {
        IReadOnlyList<WindowCandidate> candidates = WindowFinder.FindByProcessName(ProcessName);

        WindowCandidate? bound;
        lock (_lock)
        {
            bound = _bound;
        }

        if (bound != null)
        {
            if (WindowFinder.IsStillValid(bound))
            {
                return new DestinationStatus(
                    DestinationReadiness.Ready,
                    candidates,
                    bound,
                    $"Bound to {bound.DisplayLabel}");
            }

            return new DestinationStatus(
                DestinationReadiness.BoundWindowGone,
                candidates,
                null,
                $"The bound {DisplayName} window has closed. Choose a window again.");
        }

        if (candidates.Count == 0)
        {
            return new DestinationStatus(
                DestinationReadiness.NotRunning,
                candidates,
                null,
                $"{DisplayName} has no open window (looking for process '{ProcessName}').");
        }

        if (candidates.Count == 1)
        {
            // Exactly one candidate is not a guess; it is the only possible target. It still
            // has to be bound explicitly before a send is allowed.
            return new DestinationStatus(
                DestinationReadiness.NotBound,
                candidates,
                null,
                $"One {DisplayName} window found. Select it to bind.");
        }

        return new DestinationStatus(
            DestinationReadiness.AmbiguousWindow,
            candidates,
            null,
            $"{candidates.Count} {DisplayName} windows are open. Pick the exact one to use.");
    }

    public void Bind(WindowCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!string.Equals(candidate.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Window belongs to '{candidate.ProcessName}', not '{ProcessName}'.", nameof(candidate));
        }

        if (!WindowFinder.IsStillValid(candidate))
        {
            throw new ArgumentException("That window no longer exists.", nameof(candidate));
        }

        lock (_lock)
        {
            _bound = candidate;
        }
    }

    public void Unbind()
    {
        lock (_lock)
        {
            _bound = null;
        }
    }

    /// <summary>Observes visible text from the exact window currently bound by the user.</summary>
    public AgentWindowObserver CreateObserver()
    {
        WindowCandidate? bound = BoundWindow;
        if (bound == null || !WindowFinder.IsStillValid(bound))
        {
            throw new InvalidOperationException($"{DisplayName} is not bound to a live window.");
        }

        return new AgentWindowObserver(bound);
    }

    public async Task<SendResult> SendAsync(ConfirmedDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var stopwatch = Stopwatch.StartNew();

        if (!string.Equals(draft.DestinationId, DestinationId, StringComparison.Ordinal))
        {
            stopwatch.Stop();
            return new SendResult(
                SendStatus.NotReady,
                $"Draft was confirmed for '{draft.DestinationId}', not '{DestinationId}'.",
                stopwatch.ElapsedMilliseconds);
        }

        // Revalidate immediately before sending; a window can close while the user reads.
        DestinationStatus status = Probe();
        if (!status.CanSend || status.Bound == null)
        {
            stopwatch.Stop();
            return new SendResult(SendStatus.NotReady, status.Detail, stopwatch.ElapsedMilliseconds);
        }

        WindowCandidate target = status.Bound;

        try
        {
            if (!Focus(target.Hwnd, target.ProcessId))
            {
                stopwatch.Stop();
                return new SendResult(
                    SendStatus.FocusFailed,
                    $"Could not bring {DisplayName} to the foreground. Nothing was typed.",
                    stopwatch.ElapsedMilliseconds);
            }

            await Task.Delay(FocusSettleMs, cancellationToken).ConfigureAwait(false);

            // Re-check the foreground right before typing: if focus moved in the settle window,
            // the keystrokes would land in whatever is now in front.
            if (!IsTargetForeground(target.Hwnd, target.ProcessId))
            {
                stopwatch.Stop();
                return new SendResult(
                    SendStatus.FocusFailed,
                    $"{DisplayName} lost focus before typing began. Nothing was typed.",
                    stopwatch.ElapsedMilliseconds);
            }

            if (!TypeText(draft.Text, target.Hwnd, target.ProcessId, out string typeDetail))
            {
                stopwatch.Stop();
                return new SendResult(SendStatus.InputRejected, typeDetail, stopwatch.ElapsedMilliseconds);
            }

            if (!IsTargetForeground(target.Hwnd, target.ProcessId))
            {
                stopwatch.Stop();
                return new SendResult(
                    SendStatus.InputRejected,
                    $"{DisplayName} lost focus mid-typing; the prompt was not submitted.",
                    stopwatch.ElapsedMilliseconds);
            }

            if (!SendKey(NativeWindowApi.VK_RETURN))
            {
                stopwatch.Stop();
                return new SendResult(
                    SendStatus.InputRejected,
                    "The text was typed but the submit key was rejected.",
                    stopwatch.ElapsedMilliseconds);
            }

            stopwatch.Stop();
            return new SendResult(
                SendStatus.Sent,
                $"Delivered to {target.DisplayLabel}",
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new SendResult(SendStatus.Cancelled, "Send was cancelled.", stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Checks whether the target window or any of its root ancestors or owned popups/sub-windows
    /// is currently the foreground window on Windows.
    /// </summary>
    public static bool IsTargetForeground(IntPtr hWnd, int targetPid = 0)
    {
        IntPtr fg = NativeWindowApi.GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            return false;
        }

        if (fg == hWnd)
        {
            return true;
        }

        IntPtr root = NativeWindowApi.GetAncestor(fg, NativeWindowApi.GA_ROOT);
        if (root == hWnd)
        {
            return true;
        }

        IntPtr rootOwner = NativeWindowApi.GetAncestor(fg, NativeWindowApi.GA_ROOTOWNER);
        if (rootOwner == hWnd)
        {
            return true;
        }

        if (targetPid > 0)
        {
            uint threadId = NativeWindowApi.GetWindowThreadProcessId(fg, out uint fgPid);
            if (threadId != 0 && fgPid == (uint)targetPid)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Brings the window to the foreground and confirms it actually got there.
    /// </summary>
    /// <remarks>
    /// <c>SetForegroundWindow</c> can return true without taking focus under Windows'
    /// foreground-lock rules, so its return value is not trusted. Attaching to the active
    /// foreground and target input queues, pulsing an Alt keystroke, and using Z-order
    /// promotion lifts the restriction, verified by polling <c>GetForegroundWindow</c>.
    /// </remarks>
    private static bool Focus(IntPtr hWnd, int targetPid = 0)
    {
        if (hWnd == IntPtr.Zero || !NativeWindowApi.IsWindow(hWnd))
        {
            return false;
        }

        if (NativeWindowApi.IsIconic(hWnd))
        {
            NativeWindowApi.ShowWindow(hWnd, NativeWindowApi.SW_RESTORE);
        }
        else
        {
            NativeWindowApi.ShowWindow(hWnd, NativeWindowApi.SW_SHOW);
        }

        if (IsTargetForeground(hWnd, targetPid))
        {
            return true;
        }

        IntPtr fgWnd = NativeWindowApi.GetForegroundWindow();
        uint fgThread = fgWnd != IntPtr.Zero ? NativeWindowApi.GetWindowThreadProcessId(fgWnd, out _) : 0;
        uint currentThread = NativeWindowApi.GetCurrentThreadId();
        uint targetThread = NativeWindowApi.GetWindowThreadProcessId(hWnd, out _);

        bool attachedFg = false;
        bool attachedTarget = false;

        try
        {
            NativeWindowApi.AllowSetForegroundWindow(NativeWindowApi.ASFW_ANY);
            NativeWindowApi.SystemParametersInfo(NativeWindowApi.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, 0);

            if (fgThread != 0 && fgThread != currentThread)
            {
                attachedFg = NativeWindowApi.AttachThreadInput(currentThread, fgThread, true);
            }

            if (targetThread != 0 && targetThread != currentThread)
            {
                attachedTarget = NativeWindowApi.AttachThreadInput(currentThread, targetThread, true);
            }

            NativeWindowApi.BypassForegroundLock();

            NativeWindowApi.SetWindowPos(hWnd, NativeWindowApi.HWND_TOPMOST, 0, 0, 0, 0,
                NativeWindowApi.SWP_NOMOVE | NativeWindowApi.SWP_NOSIZE | NativeWindowApi.SWP_SHOWWINDOW);
            NativeWindowApi.SetWindowPos(hWnd, NativeWindowApi.HWND_NOTOPMOST, 0, 0, 0, 0,
                NativeWindowApi.SWP_NOMOVE | NativeWindowApi.SWP_NOSIZE | NativeWindowApi.SWP_SHOWWINDOW);

            NativeWindowApi.BringWindowToTop(hWnd);
            NativeWindowApi.SetForegroundWindow(hWnd);
            NativeWindowApi.SwitchToThisWindow(hWnd, true);

            for (int attempt = 0; attempt < FocusAttempts; attempt++)
            {
                if (IsTargetForeground(hWnd, targetPid))
                {
                    return true;
                }

                Thread.Sleep(FocusPollMs);

                NativeWindowApi.SetForegroundWindow(hWnd);
                NativeWindowApi.BringWindowToTop(hWnd);
            }

            return IsTargetForeground(hWnd, targetPid);
        }
        finally
        {
            if (attachedFg)
            {
                NativeWindowApi.AttachThreadInput(currentThread, fgThread, false);
            }

            if (attachedTarget)
            {
                NativeWindowApi.AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }

    /// <summary>
    /// Types the draft as Unicode keystrokes, keeping newlines as Shift+Enter so an embedded
    /// line break cannot submit the prompt early.
    /// </summary>
    private bool TypeText(string text, IntPtr expectedForeground, int expectedPid, out string detail)
    {
        detail = string.Empty;

        // Normalize line endings so CRLF does not produce two breaks.
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                                .Replace('\r', '\n');

        var buffer = new List<NativeWindowApi.INPUT>(ChunkSize * 2);

        foreach (char c in normalized)
        {
            if (c == '\n')
            {
                if (!FlushChunk(buffer, out detail))
                {
                    return false;
                }

                if (!SendShiftEnter())
                {
                    detail = "A line break was rejected by the target window.";
                    return false;
                }

                continue;
            }

            buffer.Add(UnicodeKey(c, keyUp: false));
            buffer.Add(UnicodeKey(c, keyUp: true));

            if (buffer.Count >= ChunkSize * 2)
            {
                if (!FlushChunk(buffer, out detail))
                {
                    return false;
                }

                if (!IsTargetForeground(expectedForeground, expectedPid))
                {
                    detail = $"{DisplayName} lost focus mid-typing; the prompt was not submitted.";
                    return false;
                }
            }
        }

        return FlushChunk(buffer, out detail);
    }

    private static bool FlushChunk(List<NativeWindowApi.INPUT> buffer, out string detail)
    {
        detail = string.Empty;

        if (buffer.Count == 0)
        {
            return true;
        }

        NativeWindowApi.INPUT[] inputs = buffer.ToArray();
        buffer.Clear();

        uint sent = NativeWindowApi.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeWindowApi.INPUT>());

        if (sent != (uint)inputs.Length)
        {
            detail = $"Windows accepted only {sent} of {inputs.Length} keystrokes.";
            return false;
        }

        return true;
    }

    private static NativeWindowApi.INPUT UnicodeKey(char c, bool keyUp) => new()
    {
        type = NativeWindowApi.INPUT_KEYBOARD,
        u = new NativeWindowApi.InputUnion
        {
            ki = new NativeWindowApi.KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = NativeWindowApi.KEYEVENTF_UNICODE | (keyUp ? NativeWindowApi.KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    private static NativeWindowApi.INPUT VirtualKey(ushort vk, bool keyUp) => new()
    {
        type = NativeWindowApi.INPUT_KEYBOARD,
        u = new NativeWindowApi.InputUnion
        {
            ki = new NativeWindowApi.KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = keyUp ? NativeWindowApi.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    private static bool SendKey(ushort vk)
    {
        NativeWindowApi.INPUT[] inputs =
        {
            VirtualKey(vk, keyUp: false),
            VirtualKey(vk, keyUp: true)
        };

        return NativeWindowApi.SendInput(
            (uint)inputs.Length, inputs, Marshal.SizeOf<NativeWindowApi.INPUT>()) == (uint)inputs.Length;
    }

    private static bool SendShiftEnter()
    {
        NativeWindowApi.INPUT[] inputs =
        {
            VirtualKey(NativeWindowApi.VK_SHIFT, keyUp: false),
            VirtualKey(NativeWindowApi.VK_RETURN, keyUp: false),
            VirtualKey(NativeWindowApi.VK_RETURN, keyUp: true),
            VirtualKey(NativeWindowApi.VK_SHIFT, keyUp: true)
        };

        return NativeWindowApi.SendInput(
            (uint)inputs.Length, inputs, Marshal.SizeOf<NativeWindowApi.INPUT>()) == (uint)inputs.Length;
    }
}
