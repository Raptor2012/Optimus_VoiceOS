namespace Optimus.Providers.Desktop;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using Optimus.Providers.Windows;

public enum TextEntryMode
{
    Auto,
    Accessible,
    Type,
    Clipboard
}

public enum ClickType
{
    Left,
    DoubleClick,
    Right
}

/// <summary>
/// Clipboard management service with restoration support.
/// </summary>
public interface IClipboardService
{
    string? GetText();
    void SetText(string text);
    void Restore(string? previousText);
}

/// <summary>
/// Default WPF implementation of clipboard service.
/// </summary>
public sealed class WpfClipboardService : IClipboardService
{
    public string? GetText()
    {
        try
        {
            string? result = null;
            ExecuteOnStaThread(() =>
            {
                if (Clipboard.ContainsText())
                {
                    result = Clipboard.GetText();
                }
            });
            return result;
        }
        catch
        {
            return null;
        }
    }

    public void SetText(string text)
    {
        ExecuteOnStaThread(() =>
        {
            Clipboard.SetText(text);
        });
    }

    public void Restore(string? previousText)
    {
        try
        {
            ExecuteOnStaThread(() =>
            {
                if (previousText != null)
                {
                    Clipboard.SetText(previousText);
                }
                else
                {
                    Clipboard.Clear();
                }
            });
        }
        catch
        {
            // Best effort restoration
        }
    }

    private static void ExecuteOnStaThread(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            action();
            return;
        }

        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(2));

        if (threadEx != null)
        {
            throw threadEx;
        }
    }
}

/// <summary>
/// Low-level input simulation interface for keyboard and mouse.
/// </summary>
public interface IInputSimulator
{
    bool SendMouseClick(int screenX, int screenY, ClickType clickType);
    bool SendMouseScroll(int deltaY, int deltaX = 0, int? screenX = null, int? screenY = null);
    bool SendKeyboardShortcut(IReadOnlyList<ushort> virtualKeys);
    bool SendUnicodeText(string text);
}

/// <summary>
/// Default Win32 implementation of input simulator tagged with Optimus synthetic signature.
/// </summary>
public sealed class WindowsInputSimulator : IInputSimulator
{
    public bool SendMouseClick(int screenX, int screenY, ClickType clickType)
    {
        int screenWidth = NativeWindowApi.GetSystemMetrics(NativeWindowApi.SM_CXSCREEN);
        int screenHeight = NativeWindowApi.GetSystemMetrics(NativeWindowApi.SM_CYSCREEN);
        if (screenWidth <= 0 || screenHeight <= 0)
        {
            screenWidth = 1920;
            screenHeight = 1080;
        }

        int normX = (int)Math.Round((double)screenX * 65535 / (screenWidth - 1));
        int normY = (int)Math.Round((double)screenY * 65535 / (screenHeight - 1));

        uint downFlag = clickType == ClickType.Right ? NativeWindowApi.MOUSEEVENTF_RIGHTDOWN : NativeWindowApi.MOUSEEVENTF_LEFTDOWN;
        uint upFlag = clickType == ClickType.Right ? NativeWindowApi.MOUSEEVENTF_RIGHTUP : NativeWindowApi.MOUSEEVENTF_LEFTUP;

        var moveInput = CreateMouseInput(normX, normY, NativeWindowApi.MOUSEEVENTF_MOVE | NativeWindowApi.MOUSEEVENTF_ABSOLUTE, 0);
        var downInput = CreateMouseInput(normX, normY, downFlag, 0);
        var upInput = CreateMouseInput(normX, normY, upFlag, 0);

        if (clickType == ClickType.DoubleClick)
        {
            NativeWindowApi.INPUT[] inputs = { moveInput, downInput, upInput, downInput, upInput };
            return NativeWindowApi.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeWindowApi.INPUT>()) == (uint)inputs.Length;
        }
        else
        {
            NativeWindowApi.INPUT[] inputs = { moveInput, downInput, upInput };
            return NativeWindowApi.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeWindowApi.INPUT>()) == (uint)inputs.Length;
        }
    }

    public bool SendMouseScroll(int deltaY, int deltaX = 0, int? screenX = null, int? screenY = null)
    {
        var inputs = new List<NativeWindowApi.INPUT>();

        if (screenX.HasValue && screenY.HasValue)
        {
            int screenWidth = Math.Max(1, NativeWindowApi.GetSystemMetrics(NativeWindowApi.SM_CXSCREEN));
            int screenHeight = Math.Max(1, NativeWindowApi.GetSystemMetrics(NativeWindowApi.SM_CYSCREEN));
            int normX = (int)Math.Round((double)screenX.Value * 65535 / (screenWidth - 1));
            int normY = (int)Math.Round((double)screenY.Value * 65535 / (screenHeight - 1));
            inputs.Add(CreateMouseInput(normX, normY, NativeWindowApi.MOUSEEVENTF_MOVE | NativeWindowApi.MOUSEEVENTF_ABSOLUTE, 0));
        }

        if (deltaY != 0)
        {
            inputs.Add(CreateMouseInput(0, 0, NativeWindowApi.MOUSEEVENTF_WHEEL, (uint)deltaY));
        }

        if (deltaX != 0)
        {
            inputs.Add(CreateMouseInput(0, 0, NativeWindowApi.MOUSEEVENTF_HWHEEL, (uint)deltaX));
        }

        if (inputs.Count == 0) return true;

        NativeWindowApi.INPUT[] array = inputs.ToArray();
        return NativeWindowApi.SendInput((uint)array.Length, array, Marshal.SizeOf<NativeWindowApi.INPUT>()) == (uint)array.Length;
    }

    public bool SendKeyboardShortcut(IReadOnlyList<ushort> virtualKeys)
    {
        if (virtualKeys == null || virtualKeys.Count == 0) return true;

        var inputs = new List<NativeWindowApi.INPUT>(virtualKeys.Count * 2);

        // Press down in order
        foreach (ushort vk in virtualKeys)
        {
            inputs.Add(CreateKeyInput(vk, 0, keyUp: false));
        }

        // Release in reverse order
        for (int i = virtualKeys.Count - 1; i >= 0; i--)
        {
            inputs.Add(CreateKeyInput(virtualKeys[i], 0, keyUp: true));
        }

        NativeWindowApi.INPUT[] array = inputs.ToArray();
        return NativeWindowApi.SendInput((uint)array.Length, array, Marshal.SizeOf<NativeWindowApi.INPUT>()) == (uint)array.Length;
    }

    public bool SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;

        var inputs = new List<NativeWindowApi.INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            if (c == '\n')
            {
                // Shift+Enter preserves draft without premature submission
                inputs.Add(CreateKeyInput(NativeWindowApi.VK_SHIFT, 0, keyUp: false));
                inputs.Add(CreateKeyInput(NativeWindowApi.VK_RETURN, 0, keyUp: false));
                inputs.Add(CreateKeyInput(NativeWindowApi.VK_RETURN, 0, keyUp: true));
                inputs.Add(CreateKeyInput(NativeWindowApi.VK_SHIFT, 0, keyUp: true));
                continue;
            }

            if (c == '\r') continue;

            inputs.Add(CreateUnicodeKeyInput(c, keyUp: false));
            inputs.Add(CreateUnicodeKeyInput(c, keyUp: true));
        }

        NativeWindowApi.INPUT[] array = inputs.ToArray();
        return NativeWindowApi.SendInput((uint)array.Length, array, Marshal.SizeOf<NativeWindowApi.INPUT>()) == (uint)array.Length;
    }

    private static NativeWindowApi.INPUT CreateMouseInput(int dx, int dy, uint flags, uint mouseData) => new()
    {
        type = NativeWindowApi.INPUT_MOUSE,
        u = new NativeWindowApi.InputUnion
        {
            mi = new NativeWindowApi.MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                mouseData = mouseData,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = ManualTakeoverDetector.SyntheticInputSignature
            }
        }
    };

    private static NativeWindowApi.INPUT CreateKeyInput(ushort vk, ushort scan, bool keyUp) => new()
    {
        type = NativeWindowApi.INPUT_KEYBOARD,
        u = new NativeWindowApi.InputUnion
        {
            ki = new NativeWindowApi.KEYBDINPUT
            {
                wVk = vk,
                wScan = scan,
                dwFlags = keyUp ? NativeWindowApi.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = ManualTakeoverDetector.SyntheticInputSignature
            }
        }
    };

    private static NativeWindowApi.INPUT CreateUnicodeKeyInput(char c, bool keyUp) => new()
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
                dwExtraInfo = ManualTakeoverDetector.SyntheticInputSignature
            }
        }
    };
}

/// <summary>
/// Accessible element invocation and text modification interface.
/// </summary>
public interface IUiAccessibleInvoker
{
    bool TryInvokeElement(IntPtr hwnd, string? elementId, string? elementName, string? controlType, out string detail);
    bool TrySetAccessibleText(IntPtr hwnd, string? elementId, string newText, bool preserveExisting, out string actualText, out string detail);
}

/// <summary>
/// UI Automation implementation of accessible invoker.
/// </summary>
public sealed class UiaAccessibleInvoker : IUiAccessibleInvoker
{
    public bool TryInvokeElement(IntPtr hwnd, string? elementId, string? elementName, string? controlType, out string detail)
    {
        detail = string.Empty;
        if (hwnd == IntPtr.Zero || !NativeWindowApi.IsWindow(hwnd))
        {
            detail = "Invalid or closed target window.";
            return false;
        }

        try
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            AutomationElement? target = FindElement(root, elementId, elementName, controlType);
            if (target == null)
            {
                detail = $"Control '{elementId ?? elementName}' not found in target window.";
                return false;
            }

            if (target.TryGetCurrentPattern(InvokePattern.Pattern, out object? invPattern) &&
                invPattern is InvokePattern ip)
            {
                ip.Invoke();
                detail = "Invoked via InvokePattern.";
                return true;
            }

            if (target.TryGetCurrentPattern(TogglePattern.Pattern, out object? togPattern) &&
                togPattern is TogglePattern tp)
            {
                tp.Toggle();
                detail = "Toggled via TogglePattern.";
                return true;
            }

            if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? selPattern) &&
                selPattern is SelectionItemPattern sip)
            {
                sip.Select();
                detail = "Selected via SelectionItemPattern.";
                return true;
            }

            if (target.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? expPattern) &&
                expPattern is ExpandCollapsePattern ecp)
            {
                ecp.Expand();
                detail = "Expanded via ExpandCollapsePattern.";
                return true;
            }

            detail = "Element found but does not support accessible invocation patterns.";
            return false;
        }
        catch (ElementNotAvailableException)
        {
            detail = "Element disappeared while attempting to invoke.";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            detail = $"Invocation operation rejected: {ex.Message}";
            return false;
        }
    }

    public bool TrySetAccessibleText(IntPtr hwnd, string? elementId, string newText, bool preserveExisting, out string actualText, out string detail)
    {
        actualText = string.Empty;
        detail = string.Empty;

        if (hwnd == IntPtr.Zero || !NativeWindowApi.IsWindow(hwnd))
        {
            detail = "Invalid window handle.";
            return false;
        }

        try
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            AutomationElement? target = !string.IsNullOrEmpty(elementId)
                ? FindElement(root, elementId, null, null)
                : FindDefaultEditableElement(root);

            if (target == null)
            {
                detail = "No editable text control found in window.";
                return false;
            }

            if (!target.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern) ||
                pattern is not ValuePattern valuePattern ||
                valuePattern.Current.IsReadOnly)
            {
                detail = "Target control does not support writable ValuePattern.";
                return false;
            }

            string existingText = valuePattern.Current.Value ?? string.Empty;
            string combinedText = newText;

            // Preserve content and avoid overwriting unsent text
            if (preserveExisting && !string.IsNullOrWhiteSpace(existingText))
            {
                if (existingText.EndsWith(newText, StringComparison.Ordinal) ||
                    string.Equals(existingText, newText, StringComparison.Ordinal))
                {
                    combinedText = existingText;
                }
                else
                {
                    combinedText = existingText + (existingText.EndsWith('\n') ? "" : "\n") + newText;
                }
            }

            valuePattern.SetValue(combinedText);
            actualText = valuePattern.Current.Value ?? combinedText;
            detail = $"Accessible text set successfully. Existing content {(preserveExisting ? "preserved" : "overwritten")}.";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"Accessible text entry failed: {ex.Message}";
            return false;
        }
    }

    private static AutomationElement? FindElement(
        AutomationElement root,
        string? elementId,
        string? elementName,
        string? controlType)
    {
        TreeWalker walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);

        int checkedCount = 0;
        while (queue.Count > 0 && checkedCount++ < 200)
        {
            AutomationElement current = queue.Dequeue();
            try
            {
                string autoId = current.Current.AutomationId ?? string.Empty;
                string name = current.Current.Name ?? string.Empty;
                string ct = current.Current.ControlType.ProgrammaticName ?? string.Empty;

                if (!string.IsNullOrEmpty(elementId) && string.Equals(autoId, elementId, StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                if (!string.IsNullOrEmpty(elementName) && string.Equals(name, elementName, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(controlType) || ct.EndsWith(controlType, StringComparison.OrdinalIgnoreCase))
                    {
                        return current;
                    }
                }

                AutomationElement? child = walker.GetFirstChild(current);
                while (child != null)
                {
                    queue.Enqueue(child);
                    child = walker.GetNextSibling(child);
                }
            }
            catch (ElementNotAvailableException) { }
        }

        return null;
    }

    private static AutomationElement? FindDefaultEditableElement(AutomationElement root)
    {
        TreeWalker walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);

        int checkedCount = 0;
        while (queue.Count > 0 && checkedCount++ < 150)
        {
            AutomationElement current = queue.Dequeue();
            try
            {
                if (current.Current.IsEnabled && !current.Current.IsPassword)
                {
                    if (current.TryGetCurrentPattern(ValuePattern.Pattern, out object? valPattern) &&
                        valPattern is ValuePattern vp &&
                        !vp.Current.IsReadOnly)
                    {
                        return current;
                    }
                }

                AutomationElement? child = walker.GetFirstChild(current);
                while (child != null)
                {
                    queue.Enqueue(child);
                    child = walker.GetNextSibling(child);
                }
            }
            catch (ElementNotAvailableException) { }
        }

        return null;
    }
}

public interface IDesktopExecutor
{
    ActionResult FocusWindow(IntPtr hwnd, DateTimeOffset? observationTimestamp = null);
    ActionResult InvokeAccessibleElement(IntPtr hwnd, string? elementId = null, string? elementName = null, string? controlType = null, DateTimeOffset? observationTimestamp = null);
    ActionResult ClickCoordinates(int x, int y, ClickType clickType = ClickType.Left, IntPtr? targetHwnd = null, DateTimeOffset? observationTimestamp = null);
    ActionResult Scroll(int deltaY, int deltaX = 0, int? x = null, int? y = null, IntPtr? targetHwnd = null, DateTimeOffset? observationTimestamp = null);
    ActionResult EnterText(string text, IntPtr hwnd, string? targetElementId = null, bool preserveExisting = true, TextEntryMode mode = TextEntryMode.Auto, DateTimeOffset? observationTimestamp = null);
    ActionResult PressShortcut(string shortcut, IntPtr hwnd, DateTimeOffset? observationTimestamp = null);
    Task<ActionResult> WaitForStateChangeAsync(IntPtr hwnd, ObservationSnapshot initialObservation, Func<ObservationSnapshot, bool>? predicate = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes desktop actions: window focus, accessible invocation, coordinate clicks, scrolling,
/// text entry (preserving content and restoring clipboard), and shortcut presses with takeover detection.
/// </summary>
public sealed class DesktopExecutor : IDesktopExecutor
{
    private static readonly ushort[] PasteKeys = { NativeWindowApi.VK_CONTROL, (ushort)'V' };
    private static readonly char[] ShortcutSeparators = { '+', '-', ' ' };

    private readonly IDesktopObserver _observer;
    private readonly IManualTakeoverDetector _takeoverDetector;
    private readonly IInputSimulator _inputSimulator;
    private readonly IClipboardService _clipboardService;
    private readonly IUiAccessibleInvoker _accessibleInvoker;

    public DesktopExecutor(
        IDesktopObserver? observer = null,
        IManualTakeoverDetector? takeoverDetector = null,
        IInputSimulator? inputSimulator = null,
        IClipboardService? clipboardService = null,
        IUiAccessibleInvoker? accessibleInvoker = null)
    {
        _observer = observer ?? new DesktopObserver();
        _takeoverDetector = takeoverDetector ?? new ManualTakeoverDetector();
        _inputSimulator = inputSimulator ?? new WindowsInputSimulator();
        _clipboardService = clipboardService ?? new WpfClipboardService();
        _accessibleInvoker = accessibleInvoker ?? new UiaAccessibleInvoker();
    }

    public IManualTakeoverDetector TakeoverDetector => _takeoverDetector;

    private ActionResult? CheckTakeover(DateTimeOffset? observationTimestamp)
    {
        if (!_takeoverDetector.IsTakeoverDetected)
        {
            return null;
        }

        if (observationTimestamp.HasValue && _takeoverDetector.CanResume(observationTimestamp.Value))
        {
            // Fresh observation taken strictly after takeover event; resume authorized
            _takeoverDetector.Reset();
            return null;
        }

        return ActionResult.Paused(
            "Manual user takeover detected. Execution paused. Resuming requires a fresh observation taken after the takeover event.");
    }

    public ActionResult FocusWindow(IntPtr hwnd, DateTimeOffset? observationTimestamp = null)
    {
        if (CheckTakeover(observationTimestamp) is { } paused) return paused;

        if (hwnd == IntPtr.Zero || !NativeWindowApi.IsWindow(hwnd))
        {
            return ActionResult.Unresolved("Invalid window handle; window cannot be focused.");
        }

        if (NativeWindowApi.IsIconic(hwnd))
        {
            NativeWindowApi.ShowWindow(hwnd, NativeWindowApi.SW_RESTORE);
        }
        else
        {
            NativeWindowApi.ShowWindow(hwnd, NativeWindowApi.SW_SHOW);
        }

        IntPtr fg = NativeWindowApi.GetForegroundWindow();
        if (fg == hwnd)
        {
            return ActionResult.Completed("Window is in the foreground.", observedResult: $"0x{hwnd.ToInt64():X}");
        }

        uint fgThread = fg != IntPtr.Zero ? NativeWindowApi.GetWindowThreadProcessId(fg, out _) : 0;
        uint currentThread = NativeWindowApi.GetCurrentThreadId();
        uint targetThread = NativeWindowApi.GetWindowThreadProcessId(hwnd, out _);

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
            NativeWindowApi.BringWindowToTop(hwnd);
            NativeWindowApi.SetForegroundWindow(hwnd);
            NativeWindowApi.SwitchToThisWindow(hwnd, true);

            for (int i = 0; i < 10; i++)
            {
                if (NativeWindowApi.GetForegroundWindow() == hwnd)
                {
                    return ActionResult.Completed("Window brought to foreground successfully.", observedResult: $"0x{hwnd.ToInt64():X}");
                }
                Thread.Sleep(20);
                NativeWindowApi.SetForegroundWindow(hwnd);
            }

            return ActionResult.Completed("Focus command dispatched to window.", observedResult: $"0x{hwnd.ToInt64():X}");
        }
        finally
        {
            if (attachedFg) NativeWindowApi.AttachThreadInput(currentThread, fgThread, false);
            if (attachedTarget) NativeWindowApi.AttachThreadInput(currentThread, targetThread, false);
        }
    }

    public ActionResult InvokeAccessibleElement(
        IntPtr hwnd,
        string? elementId = null,
        string? elementName = null,
        string? controlType = null,
        DateTimeOffset? observationTimestamp = null)
    {
        if (CheckTakeover(observationTimestamp) is { } paused) return paused;

        // Prefer accessible invocation over coordinates
        if (_accessibleInvoker.TryInvokeElement(hwnd, elementId, elementName, controlType, out string detail))
        {
            return ActionResult.Completed($"Accessible invocation succeeded. {detail}", observedResult: elementId ?? elementName);
        }

        // If direct accessible pattern failed, check if element has visible coordinates to click
        WindowAccessibilitySnapshot snapshot = _observer.InspectAccessibility(hwnd);
        AccessibleControlInfo? element = !string.IsNullOrEmpty(elementId)
            ? snapshot.FindById(elementId)
            : snapshot.FindByName(elementName ?? string.Empty, controlType);

        if (element != null && element.Bounds.Width > 0 && element.Bounds.Height > 0 && !element.IsOffscreen)
        {
            ScreenPoint center = element.Center;
            ActionResult clickRes = ClickCoordinates(center.X, center.Y, ClickType.Left, hwnd, observationTimestamp);
            if (clickRes.Success)
            {
                return ActionResult.Completed(
                    $"Element lacked invocation pattern; clicked center coordinates ({center.X}, {center.Y}).",
                    observedResult: $"{center.X},{center.Y}");
            }
        }

        return ActionResult.Unresolved($"Could not invoke element '{elementId ?? elementName}': {detail}");
    }

    public ActionResult ClickCoordinates(
        int x,
        int y,
        ClickType clickType = ClickType.Left,
        IntPtr? targetHwnd = null,
        DateTimeOffset? observationTimestamp = null)
    {
        if (CheckTakeover(observationTimestamp) is { } paused) return paused;

        if (targetHwnd.HasValue && targetHwnd.Value != IntPtr.Zero)
        {
            FocusWindow(targetHwnd.Value, observationTimestamp);
        }

        bool success = _inputSimulator.SendMouseClick(x, y, clickType);
        if (!success)
        {
            return ActionResult.Unresolved($"Failed to synthesize {clickType} mouse click at ({x}, {y}).");
        }

        return ActionResult.Completed($"Clicked at ({x}, {y}) ({clickType}).", observedResult: $"{x},{y}");
    }

    public ActionResult Scroll(
        int deltaY,
        int deltaX = 0,
        int? x = null,
        int? y = null,
        IntPtr? targetHwnd = null,
        DateTimeOffset? observationTimestamp = null)
    {
        if (CheckTakeover(observationTimestamp) is { } paused) return paused;

        if (targetHwnd.HasValue && targetHwnd.Value != IntPtr.Zero)
        {
            FocusWindow(targetHwnd.Value, observationTimestamp);
        }

        bool success = _inputSimulator.SendMouseScroll(deltaY, deltaX, x, y);
        if (!success)
        {
            return ActionResult.Unresolved("Failed to synthesize mouse scroll.");
        }

        return ActionResult.Completed($"Scrolled deltaY: {deltaY}, deltaX: {deltaX}.", observedResult: $"{deltaX},{deltaY}");
    }

    public ActionResult EnterText(
        string text,
        IntPtr hwnd,
        string? targetElementId = null,
        bool preserveExisting = true,
        TextEntryMode mode = TextEntryMode.Auto,
        DateTimeOffset? observationTimestamp = null)
    {
        if (CheckTakeover(observationTimestamp) is { } paused) return paused;

        if (hwnd != IntPtr.Zero)
        {
            FocusWindow(hwnd, observationTimestamp);
        }

        // 1. Use accessible entry where supported
        if (mode is TextEntryMode.Auto or TextEntryMode.Accessible)
        {
            if (_accessibleInvoker.TrySetAccessibleText(hwnd, targetElementId, text, preserveExisting, out string actualText, out string detail))
            {
                return ActionResult.Completed($"Text entered via accessible ValuePattern. {detail}", observedResult: actualText);
            }

            if (mode == TextEntryMode.Accessible)
            {
                return ActionResult.Unresolved($"Accessible text entry failed: {detail}");
            }
        }

        // 2. Clipboard entry only when necessary with restore
        if (mode == TextEntryMode.Clipboard || text.Contains('\n') || text.Length > 80)
        {
            string? priorClipboard = _clipboardService.GetText();
            try
            {
                _clipboardService.SetText(text);
                // Send Ctrl+V paste shortcut
                bool pasted = _inputSimulator.SendKeyboardShortcut(PasteKeys);
                if (pasted)
                {
                    Thread.Sleep(50);
                    return ActionResult.Completed(
                        "Text entered via clipboard paste with original clipboard restored.",
                        observedResult: text);
                }
            }
            finally
            {
                _clipboardService.Restore(priorClipboard);
            }
        }

        // 3. Keystroke typing fallback
        bool typed = _inputSimulator.SendUnicodeText(text);
        if (!typed)
        {
            return ActionResult.Unresolved("Failed to synthesize text entry keystrokes.");
        }

        return ActionResult.Completed("Text entered via keystrokes.", observedResult: text);
    }

    public ActionResult PressShortcut(
        string shortcut,
        IntPtr hwnd,
        DateTimeOffset? observationTimestamp = null)
    {
        if (CheckTakeover(observationTimestamp) is { } paused) return paused;

        if (hwnd != IntPtr.Zero)
        {
            FocusWindow(hwnd, observationTimestamp);
        }

        IReadOnlyList<ushort> virtualKeys = ParseShortcut(shortcut);
        if (virtualKeys.Count == 0)
        {
            return ActionResult.Unresolved($"Unrecognized or empty shortcut: '{shortcut}'.");
        }

        bool success = _inputSimulator.SendKeyboardShortcut(virtualKeys);
        if (!success)
        {
            return ActionResult.Unresolved($"Failed to synthesize shortcut '{shortcut}'.");
        }

        return ActionResult.Completed($"Shortcut '{shortcut}' sent successfully.", observedResult: shortcut);
    }

    /// <summary>
    /// Waits for meaningful state changes with bounded deadlines, one reobservation on layout change,
    /// then reports an obstacle.
    /// </summary>
    public async Task<ActionResult> WaitForStateChangeAsync(
        IntPtr hwnd,
        ObservationSnapshot initialObservation,
        Func<ObservationSnapshot, bool>? predicate = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan deadline = timeout ?? TimeSpan.FromMilliseconds(3000);
        var stopwatch = Stopwatch.StartNew();
        bool hasReobservedLayoutChange = false;

        while (stopwatch.Elapsed < deadline)
        {
            if (_takeoverDetector.IsTakeoverDetected)
            {
                return ActionResult.Paused("Manual user takeover detected while waiting for state change.");
            }

            ObservationSnapshot current = _observer.ObserveWindow(hwnd);
            if (!current.IsValid)
            {
                return ActionResult.Stopped("Target window closed or became invalid during observation.");
            }

            // Check if meaningful predicate condition is satisfied
            if (predicate != null && predicate(current))
            {
                return ActionResult.Completed("Expected state change observed successfully.", observedResult: current.LayoutHash);
            }

            // Check if layout changed
            if (current.LayoutHash != initialObservation.LayoutHash)
            {
                if (!hasReobservedLayoutChange)
                {
                    // "one reobservation on layout change, then report obstacle"
                    hasReobservedLayoutChange = true;
                    ObservationSnapshot reobserved = _observer.ObserveWindow(hwnd);
                    if (predicate != null && predicate(reobserved))
                    {
                        return ActionResult.Completed("Expected state change observed after layout reobservation.", observedResult: reobserved.LayoutHash);
                    }

                    // Layout changed and condition was not satisfied upon reobservation; report obstacle immediately
                    return ActionResult.Unresolved(
                        $"Obstacle: window layout changed unexpectedly during execution (initial hash: {initialObservation.LayoutHash}, new hash: {reobserved.LayoutHash}) and target state was not satisfied.",
                        observedResult: reobserved.LayoutHash);
                }
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return ActionResult.Unresolved(
            $"Obstacle: bounded deadline ({deadline.TotalMilliseconds} ms) expired without meaningful state change.",
            observedResult: initialObservation.LayoutHash);
    }

    public static IReadOnlyList<ushort> ParseShortcut(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return Array.Empty<ushort>();

        var keys = new List<ushort>();
        string[] parts = shortcut.Split(ShortcutSeparators, StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            string clean = part.Trim().ToUpperInvariant();
            switch (clean)
            {
                case "CTRL":
                case "CONTROL":
                    keys.Add(NativeWindowApi.VK_CONTROL);
                    break;
                case "ALT":
                case "MENU":
                    keys.Add(NativeWindowApi.VK_MENU);
                    break;
                case "SHIFT":
                    keys.Add(NativeWindowApi.VK_SHIFT);
                    break;
                case "ENTER":
                case "RETURN":
                    keys.Add(NativeWindowApi.VK_RETURN);
                    break;
                case "ESC":
                case "ESCAPE":
                    keys.Add(NativeWindowApi.VK_ESCAPE);
                    break;
                case "TAB":
                    keys.Add(NativeWindowApi.VK_TAB);
                    break;
                case "SPACE":
                    keys.Add(NativeWindowApi.VK_SPACE);
                    break;
                case "BACKSPACE":
                case "BACK":
                    keys.Add(NativeWindowApi.VK_BACK);
                    break;
                case "DELETE":
                case "DEL":
                    keys.Add(NativeWindowApi.VK_DELETE);
                    break;
                case "UP":
                    keys.Add(NativeWindowApi.VK_UP);
                    break;
                case "DOWN":
                    keys.Add(NativeWindowApi.VK_DOWN);
                    break;
                case "LEFT":
                    keys.Add(NativeWindowApi.VK_LEFT);
                    break;
                case "RIGHT":
                    keys.Add(NativeWindowApi.VK_RIGHT);
                    break;
                case "PAGEUP":
                case "PGUP":
                    keys.Add(NativeWindowApi.VK_PRIOR);
                    break;
                case "PAGEDOWN":
                case "PGDN":
                    keys.Add(NativeWindowApi.VK_NEXT);
                    break;
                case "HOME":
                    keys.Add(NativeWindowApi.VK_HOME);
                    break;
                case "END":
                    keys.Add(NativeWindowApi.VK_END);
                    break;
                default:
                    if (clean.Length == 1 && clean[0] >= 'A' && clean[0] <= 'Z')
                    {
                        keys.Add((ushort)clean[0]);
                    }
                    else if (clean.Length == 1 && clean[0] >= '0' && clean[0] <= '9')
                    {
                        keys.Add((ushort)clean[0]);
                    }
                    else if (clean.StartsWith('F') && int.TryParse(clean[1..], out int fNum) && fNum >= 1 && fNum <= 24)
                    {
                        keys.Add((ushort)(0x70 + (fNum - 1))); // VK_F1 is 0x70
                    }
                    break;
            }
        }

        return keys;
    }
}
