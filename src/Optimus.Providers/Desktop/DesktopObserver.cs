namespace Optimus.Providers.Desktop;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;
using Optimus.Providers.Windows;

/// <summary>
/// Information describing a top-level window.
/// </summary>
public sealed record WindowInfo(
    IntPtr Hwnd,
    int ProcessId,
    string ProcessName,
    string Title,
    string ClassName,
    ScreenRegion Bounds,
    bool IsForeground,
    bool IsVisible,
    bool IsIconic)
{
    public long HandleValue => Hwnd.ToInt64();

    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(Title)
            ? $"{ProcessName} (pid {ProcessId}, window 0x{HandleValue:X})"
            : $"{Title} — {ProcessName} (pid {ProcessId}, window 0x{HandleValue:X})";
}

/// <summary>
/// Information describing a single UI Automation accessible control within a window.
/// </summary>
public sealed record AccessibleControlInfo(
    string Id,
    string Name,
    string ControlType,
    ScreenRegion Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    bool IsKeyboardFocusable,
    bool HasKeyboardFocus,
    IReadOnlyList<string> SupportedPatterns,
    string? CurrentValue = null,
    int Depth = 0)
{
    public ScreenPoint Center => new(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2);
}

/// <summary>
/// Snapshot of a window's accessibility control tree strictly scoped to that window.
/// </summary>
public sealed record WindowAccessibilitySnapshot(
    IntPtr Hwnd,
    int ProcessId,
    string WindowTitle,
    IReadOnlyList<AccessibleControlInfo> Elements,
    string LayoutHash,
    DateTimeOffset CapturedAtUtc)
{
    public AccessibleControlInfo? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Elements.FirstOrDefault(e =>
            string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public AccessibleControlInfo? FindByName(string name, string? controlType = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Elements.FirstOrDefault(e =>
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) &&
            (controlType == null || string.Equals(e.ControlType, controlType, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>
/// Combined window observation snapshot with validity check and layout hash.
/// </summary>
public sealed record ObservationSnapshot(
    WindowInfo Window,
    WindowAccessibilitySnapshot Accessibility,
    DateTimeOffset ObservedAtUtc,
    bool IsValid)
{
    public string LayoutHash => Accessibility.LayoutHash;
}

public interface IUiAccessibilityInspector
{
    WindowAccessibilitySnapshot InspectWindow(
        IntPtr hwnd,
        int maxDepth = 6,
        int maxElements = 150,
        string? filterControlType = null);
}

public interface IWindowInfoProvider
{
    IReadOnlyList<WindowInfo> ListWindows(string? filterProcessName = null, bool includeEmptyTitles = false);
    WindowInfo? GetWindowInfo(IntPtr hwnd);
}

/// <summary>
/// Windows UI Automation tree inspector that strictly limits traversal to the specified window.
/// </summary>
public sealed class UiaAccessibilityInspector : IUiAccessibilityInspector
{
    public WindowAccessibilitySnapshot InspectWindow(
        IntPtr hwnd,
        int maxDepth = 6,
        int maxElements = 150,
        string? filterControlType = null)
    {
        if (hwnd == IntPtr.Zero || !NativeWindowApi.IsWindow(hwnd))
        {
            return new WindowAccessibilitySnapshot(
                hwnd,
                0,
                string.Empty,
                Array.Empty<AccessibleControlInfo>(),
                string.Empty,
                DateTimeOffset.UtcNow);
        }

        _ = NativeWindowApi.GetWindowThreadProcessId(hwnd, out uint pid);
        int targetPid = (int)pid;

        string windowTitle = ReadWindowText(hwnd);
        var elements = new List<AccessibleControlInfo>();

        try
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);

            // Scope strictly to descendants of the target root
            TraverseElement(root, 0, maxDepth, maxElements, targetPid, filterControlType, elements);
        }
        catch (ElementNotAvailableException)
        {
            // Window closed during traversal
        }
        catch (COMException)
        {
            // Transient COM error
        }

        string layoutHash = ComputeLayoutHash(elements);

        return new WindowAccessibilitySnapshot(
            Hwnd: hwnd,
            ProcessId: targetPid,
            WindowTitle: windowTitle,
            Elements: elements,
            LayoutHash: layoutHash,
            CapturedAtUtc: DateTimeOffset.UtcNow);
    }

    private static void TraverseElement(
        AutomationElement element,
        int depth,
        int maxDepth,
        int maxElements,
        int targetPid,
        string? filterControlType,
        List<AccessibleControlInfo> results)
    {
        if (depth > maxDepth || results.Count >= maxElements)
        {
            return;
        }

        try
        {
            // Strictly enforce process isolation - elements must belong to the relevant window's process
            if (element.Current.ProcessId != targetPid)
            {
                return;
            }

            string controlType = element.Current.ControlType.ProgrammaticName ?? string.Empty;
            if (controlType.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase))
            {
                controlType = controlType["ControlType.".Length..];
            }

            bool matchesFilter = string.IsNullOrEmpty(filterControlType) ||
                                string.Equals(controlType, filterControlType, StringComparison.OrdinalIgnoreCase);

            string autoId = element.Current.AutomationId ?? string.Empty;
            string name = element.Current.Name ?? string.Empty;
            System.Windows.Rect boundsRect = element.Current.BoundingRectangle;
            var bounds = new ScreenRegion(
                (int)Math.Max(0, boundsRect.X),
                (int)Math.Max(0, boundsRect.Y),
                (int)Math.Max(0, boundsRect.Width),
                (int)Math.Max(0, boundsRect.Height));

            var patterns = new List<string>();
            string? currentValue = null;

            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out _))
            {
                patterns.Add("Invoke");
            }

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? valPattern) &&
                valPattern is ValuePattern vp)
            {
                patterns.Add("Value");
                currentValue = vp.Current.Value;
            }

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? txtPattern) &&
                txtPattern is TextPattern tp)
            {
                patterns.Add("Text");
                currentValue ??= tp.DocumentRange.GetText(200);
            }

            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out _))
            {
                patterns.Add("Toggle");
            }

            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _))
            {
                patterns.Add("SelectionItem");
            }

            if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _))
            {
                patterns.Add("ExpandCollapse");
            }

            if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out _))
            {
                patterns.Add("Scroll");
            }

            string elementId = !string.IsNullOrEmpty(autoId)
                ? autoId
                : GetDeterministicId(element, controlType, results.Count);

            if (matchesFilter)
            {
                results.Add(new AccessibleControlInfo(
                    Id: elementId,
                    Name: name,
                    ControlType: controlType,
                    Bounds: bounds,
                    IsEnabled: element.Current.IsEnabled,
                    IsOffscreen: element.Current.IsOffscreen,
                    IsKeyboardFocusable: element.Current.IsKeyboardFocusable,
                    HasKeyboardFocus: element.Current.HasKeyboardFocus,
                    SupportedPatterns: patterns,
                    CurrentValue: currentValue,
                    Depth: depth));
            }

            // Recurse children within this element subtree only
            TreeWalker walker = TreeWalker.ControlViewWalker;
            AutomationElement? child = walker.GetFirstChild(element);
            while (child != null && results.Count < maxElements)
            {
                TraverseElement(child, depth + 1, maxDepth, maxElements, targetPid, filterControlType, results);
                child = walker.GetNextSibling(child);
            }
        }
        catch (ElementNotAvailableException) { }
        catch (COMException) { }
    }

    private static string GetDeterministicId(AutomationElement element, string controlType, int index)
    {
        try
        {
            int[] runtimeId = element.GetRuntimeId();
            if (runtimeId.Length > 0)
            {
                return string.Join('.', runtimeId);
            }
        }
        catch { }

        return $"{controlType}_{index}";
    }

    private static string ReadWindowText(IntPtr hWnd)
    {
        char[] buffer = new char[512];
        int length = NativeWindowApi.GetWindowText(hWnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    public static string ComputeLayoutHash(IEnumerable<AccessibleControlInfo> elements)
    {
        var sb = new StringBuilder();
        foreach (AccessibleControlInfo el in elements)
        {
            sb.Append(el.Id).Append('|')
              .Append(el.Name).Append('|')
              .Append(el.ControlType).Append('|')
              .Append(el.Bounds.X / 10).Append(',')
              .Append(el.Bounds.Y / 10).Append(';')
              .Append(el.IsEnabled).Append(';')
              .Append(el.SupportedPatterns.Count).Append('\n');
        }

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hashBytes)[..16];
    }
}

/// <summary>
/// Default window enumerator using Win32 API.
/// </summary>
public sealed class NativeWindowInfoProvider : IWindowInfoProvider
{
    public IReadOnlyList<WindowInfo> ListWindows(string? filterProcessName = null, bool includeEmptyTitles = false)
    {
        var results = new List<WindowInfo>();
        IntPtr foregroundHwnd = NativeWindowApi.GetForegroundWindow();

        NativeWindowApi.EnumWindows((hWnd, _) =>
        {
            if (!NativeWindowApi.IsWindowVisible(hWnd))
            {
                return true;
            }

            // Exclude owned child popups/dialogs
            if (NativeWindowApi.GetWindow(hWnd, NativeWindowApi.GW_OWNER) != IntPtr.Zero)
            {
                return true;
            }

            string title = ReadWindowText(hWnd);
            if (!includeEmptyTitles && string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            uint threadId = NativeWindowApi.GetWindowThreadProcessId(hWnd, out uint pid);
            if (threadId == 0 || pid == 0)
            {
                return true;
            }

            string processName;
            try
            {
                using Process proc = Process.GetProcessById((int)pid);
                processName = proc.ProcessName;
            }
            catch
            {
                return true;
            }

            if (!string.IsNullOrEmpty(filterProcessName) &&
                !string.Equals(processName, filterProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            NativeWindowApi.RECT rect;
            if (!NativeWindowApi.GetWindowRect(hWnd, out rect) || rect.Width <= 0 || rect.Height <= 0)
            {
                return true;
            }

            string className = ReadClassName(hWnd);
            bool isForeground = hWnd == foregroundHwnd;
            bool isIconic = NativeWindowApi.IsIconic(hWnd);

            results.Add(new WindowInfo(
                Hwnd: hWnd,
                ProcessId: (int)pid,
                ProcessName: processName,
                Title: title,
                ClassName: className,
                Bounds: new ScreenRegion(rect.Left, rect.Top, rect.Width, rect.Height),
                IsForeground: isForeground,
                IsVisible: true,
                IsIconic: isIconic));

            return true;
        }, IntPtr.Zero);

        return results;
    }

    public WindowInfo? GetWindowInfo(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeWindowApi.IsWindow(hwnd))
        {
            return null;
        }

        _ = NativeWindowApi.GetWindowThreadProcessId(hwnd, out uint pid);
        string processName = string.Empty;
        try
        {
            using Process proc = Process.GetProcessById((int)pid);
            processName = proc.ProcessName;
        }
        catch { }

        string title = ReadWindowText(hwnd);
        string className = ReadClassName(hwnd);
        NativeWindowApi.RECT rect;
        NativeWindowApi.GetWindowRect(hwnd, out rect);
        IntPtr foregroundHwnd = NativeWindowApi.GetForegroundWindow();

        return new WindowInfo(
            Hwnd: hwnd,
            ProcessId: (int)pid,
            ProcessName: processName,
            Title: title,
            ClassName: className,
            Bounds: new ScreenRegion(rect.Left, rect.Top, rect.Width, rect.Height),
            IsForeground: hwnd == foregroundHwnd,
            IsVisible: NativeWindowApi.IsWindowVisible(hwnd),
            IsIconic: NativeWindowApi.IsIconic(hwnd));
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

public interface IDesktopObserver
{
    IReadOnlyList<WindowInfo> ListWindows(string? filterProcessName = null, bool includeEmptyTitles = false);
    WindowAccessibilitySnapshot InspectAccessibility(IntPtr hwnd, int maxDepth = 6, int maxElements = 150, string? filterControlType = null);
    CapturedScreen CaptureWindow(IntPtr hwnd, ScreenRegion? region = null, double scaleFactor = 1.0);
    IReadOnlyList<VisibleTextNode> ReadContent(IntPtr hwnd);
    ObservationSnapshot ObserveWindow(IntPtr hwnd);
}

/// <summary>
/// Provides desktop observation services: listing windows, inspecting accessibility trees scoped to the relevant window,
/// capturing screenshots, and reading visible text content.
/// </summary>
public sealed class DesktopObserver : IDesktopObserver
{
    private readonly IWindowInfoProvider _windowInfoProvider;
    private readonly IUiAccessibilityInspector _accessibilityInspector;
    private readonly ScreenCapture _screenCapture;
    private readonly IWindowTextSource _textSource;

    public DesktopObserver(
        IWindowInfoProvider? windowInfoProvider = null,
        IUiAccessibilityInspector? accessibilityInspector = null,
        ScreenCapture? screenCapture = null,
        IWindowTextSource? textSource = null)
    {
        _windowInfoProvider = windowInfoProvider ?? new NativeWindowInfoProvider();
        _accessibilityInspector = accessibilityInspector ?? new UiaAccessibilityInspector();
        _screenCapture = screenCapture ?? new ScreenCapture();
        _textSource = textSource ?? new UiaWindowTextSource();
    }

    public IReadOnlyList<WindowInfo> ListWindows(string? filterProcessName = null, bool includeEmptyTitles = false) =>
        _windowInfoProvider.ListWindows(filterProcessName, includeEmptyTitles);

    public WindowAccessibilitySnapshot InspectAccessibility(
        IntPtr hwnd,
        int maxDepth = 6,
        int maxElements = 150,
        string? filterControlType = null)
    {
        // Limit accessibility data to the relevant window
        return _accessibilityInspector.InspectWindow(hwnd, maxDepth, maxElements, filterControlType);
    }

    public CapturedScreen CaptureWindow(IntPtr hwnd, ScreenRegion? region = null, double scaleFactor = 1.0) =>
        _screenCapture.CaptureWindow(hwnd, region, scaleFactor);

    public IReadOnlyList<VisibleTextNode> ReadContent(IntPtr hwnd) =>
        _textSource.Read(hwnd);

    public ObservationSnapshot ObserveWindow(IntPtr hwnd)
    {
        WindowInfo? info = _windowInfoProvider.GetWindowInfo(hwnd);
        bool isValid = info != null && info.IsVisible;

        WindowAccessibilitySnapshot accessibility = InspectAccessibility(hwnd);

        return new ObservationSnapshot(
            Window: info ?? new WindowInfo(
                hwnd, 0, string.Empty, string.Empty, string.Empty,
                new ScreenRegion(0, 0, 0, 0), false, false, false),
            Accessibility: accessibility,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            IsValid: isValid);
    }
}
