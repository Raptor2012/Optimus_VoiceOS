namespace Optimus.Providers.Windows;

using System;
using System.Collections.Generic;
using System.Diagnostics;

/// <summary>One concrete top-level window the user could bind a destination to.</summary>
public sealed record WindowCandidate(
    long Handle,
    int ProcessId,
    string ProcessName,
    string Title,
    string ClassName)
{
    public IntPtr Hwnd => new(Handle);

    /// <summary>What the picker shows. Includes the handle because titles can collide.</summary>
    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(Title)
            ? $"{ProcessName} (pid {ProcessId}, window 0x{Handle:X})"
            : $"{Title} — {ProcessName} (pid {ProcessId}, window 0x{Handle:X})";
}

/// <summary>Enumerates real top-level application windows.</summary>
public static class WindowFinder
{
    /// <summary>
    /// Every visible, titled, un-owned top-level window belonging to a process with this name.
    /// </summary>
    /// <remarks>
    /// Owned windows are skipped so tooltips, popups and dialogs are never offered as targets.
    /// The comparison is ordinal-ignore-case on the process name exactly as Windows reports it;
    /// no fuzzy or partial matching, because a near-match is a wrong window.
    /// </remarks>
    public static IReadOnlyList<WindowCandidate> FindByProcessName(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);

        var results = new List<WindowCandidate>();

        NativeWindowApi.EnumWindows(
            (hWnd, _) =>
            {
                if (!NativeWindowApi.IsWindowVisible(hWnd))
                {
                    return true;
                }

                if (NativeWindowApi.GetWindowTextLength(hWnd) == 0)
                {
                    return true;
                }

                if (NativeWindowApi.GetWindow(hWnd, NativeWindowApi.GW_OWNER) != IntPtr.Zero)
                {
                    return true;
                }

                uint threadId = NativeWindowApi.GetWindowThreadProcessId(hWnd, out uint pid);
                if (threadId == 0 || pid == 0)
                {
                    return true;
                }

                string actualName;
                try
                {
                    using Process process = Process.GetProcessById((int)pid);
                    actualName = process.ProcessName;
                }
                catch (ArgumentException)
                {
                    return true; // Exited between enumeration and lookup.
                }
                catch (InvalidOperationException)
                {
                    return true;
                }

                if (!string.Equals(actualName, processName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                results.Add(new WindowCandidate(
                    hWnd.ToInt64(),
                    (int)pid,
                    actualName,
                    ReadWindowText(hWnd),
                    ReadClassName(hWnd)));

                return true;
            },
            IntPtr.Zero);

        return results;
    }

    /// <summary>
    /// Confirms a previously bound window still exists and is still the same application.
    /// </summary>
    /// <remarks>
    /// A raw HWND is not a durable identity: Windows reuses handles. Re-checking the process id
    /// and name means a recycled handle now owned by something else is rejected rather than
    /// typed into.
    /// </remarks>
    public static bool IsStillValid(WindowCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        IntPtr hWnd = candidate.Hwnd;

        if (!NativeWindowApi.IsWindow(hWnd) || !NativeWindowApi.IsWindowVisible(hWnd))
        {
            return false;
        }

        uint threadId = NativeWindowApi.GetWindowThreadProcessId(hWnd, out uint pid);
        if (threadId == 0 || pid != (uint)candidate.ProcessId)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById((int)pid);
            return string.Equals(process.ProcessName, candidate.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string ReadWindowText(IntPtr hWnd)
    {
        char[] buffer = new char[512];
        int length = NativeWindowApi.GetWindowText(hWnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static string ReadClassName(IntPtr hWnd)
    {
        char[] buffer = new char[256];
        int length = NativeWindowApi.GetClassName(hWnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}
