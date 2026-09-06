namespace Optimus.Providers.Tests.Desktop;

using System;
using System.Collections.Generic;
using System.Linq;
using Optimus.Providers.Desktop;
using Xunit;

public sealed class ObservationScopingTests
{
    private sealed class FakeWindowInfoProvider : IWindowInfoProvider
    {
        public List<WindowInfo> Windows { get; set; } = new();

        public IReadOnlyList<WindowInfo> ListWindows(string? filterProcessName = null, bool includeEmptyTitles = false)
        {
            return Windows.Where(w =>
                (string.IsNullOrEmpty(filterProcessName) || string.Equals(w.ProcessName, filterProcessName, StringComparison.OrdinalIgnoreCase)) &&
                (includeEmptyTitles || !string.IsNullOrWhiteSpace(w.Title))).ToList();
        }

        public WindowInfo? GetWindowInfo(IntPtr hwnd)
        {
            return Windows.FirstOrDefault(w => w.Hwnd == hwnd);
        }
    }

    private sealed class FakeAccessibilityInspector : IUiAccessibilityInspector
    {
        public Dictionary<IntPtr, WindowAccessibilitySnapshot> Snapshots { get; } = new();
        public IntPtr LastInspectedHwnd { get; private set; }
        public int LastMaxDepth { get; private set; }
        public int LastMaxElements { get; private set; }

        public WindowAccessibilitySnapshot InspectWindow(
            IntPtr hwnd,
            int maxDepth = 6,
            int maxElements = 150,
            string? filterControlType = null)
        {
            LastInspectedHwnd = hwnd;
            LastMaxDepth = maxDepth;
            LastMaxElements = maxElements;

            if (Snapshots.TryGetValue(hwnd, out WindowAccessibilitySnapshot? snapshot))
            {
                var elements = snapshot.Elements;
                if (!string.IsNullOrEmpty(filterControlType))
                {
                    elements = elements.Where(e => string.Equals(e.ControlType, filterControlType, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                return snapshot with { Elements = elements };
            }

            return new WindowAccessibilitySnapshot(
                hwnd, 0, string.Empty, Array.Empty<AccessibleControlInfo>(), "empty_hash", DateTimeOffset.UtcNow);
        }
    }

    [Fact]
    public void InspectAccessibility_IsStrictlyScopedToTargetWindow()
    {
        var targetHwnd = new IntPtr(1001);
        var otherHwnd = new IntPtr(2002);

        var fakeInspector = new FakeAccessibilityInspector();
        var targetElements = new List<AccessibleControlInfo>
        {
            new("btn_send", "Send", "Button", new ScreenRegion(100, 200, 50, 30), true, false, true, false, new[] { "Invoke" }),
            new("txt_input", "Prompt", "Edit", new ScreenRegion(100, 100, 200, 80), true, false, true, true, new[] { "Value" }, "Hello world")
        };

        fakeInspector.Snapshots[targetHwnd] = new WindowAccessibilitySnapshot(
            targetHwnd, 4200, "Claude Desktop", targetElements, "target_hash", DateTimeOffset.UtcNow);

        fakeInspector.Snapshots[otherHwnd] = new WindowAccessibilitySnapshot(
            otherHwnd, 8800, "Antigravity", new[]
            {
                new AccessibleControlInfo("other_btn", "Submit", "Button", new ScreenRegion(500, 500, 50, 30), true, false, true, false, new[] { "Invoke" })
            }, "other_hash", DateTimeOffset.UtcNow);

        var observer = new DesktopObserver(accessibilityInspector: fakeInspector);

        WindowAccessibilitySnapshot result = observer.InspectAccessibility(targetHwnd);

        Assert.Equal(targetHwnd, fakeInspector.LastInspectedHwnd);
        Assert.Equal(targetHwnd, result.Hwnd);
        Assert.Equal(4200, result.ProcessId);
        Assert.Equal(2, result.Elements.Count);
        Assert.NotNull(result.FindById("btn_send"));
        Assert.NotNull(result.FindById("txt_input"));
        Assert.Null(result.FindById("other_btn")); // Must NOT contain elements from another window
    }

    [Fact]
    public void InspectAccessibility_RespectsDepthAndElementLimits()
    {
        var targetHwnd = new IntPtr(1001);
        var fakeInspector = new FakeAccessibilityInspector();
        var observer = new DesktopObserver(accessibilityInspector: fakeInspector);

        observer.InspectAccessibility(targetHwnd, maxDepth: 4, maxElements: 25);

        Assert.Equal(4, fakeInspector.LastMaxDepth);
        Assert.Equal(25, fakeInspector.LastMaxElements);
    }

    [Fact]
    public void ListWindows_FiltersByProcessNameAndEmptyTitles()
    {
        var fakeWinProvider = new FakeWindowInfoProvider();
        fakeWinProvider.Windows.AddRange(new[]
        {
            new WindowInfo(new IntPtr(1), 100, "claude", "Claude Conversation", "Chrome_WidgetWin_1", new ScreenRegion(0, 0, 800, 600), true, true, false),
            new WindowInfo(new IntPtr(2), 200, "claude", "", "Chrome_WidgetWin_1", new ScreenRegion(0, 0, 100, 100), false, true, false),
            new WindowInfo(new IntPtr(3), 300, "antigravity", "Antigravity IDE", "WorkbenchWindow", new ScreenRegion(0, 0, 1000, 800), false, true, false)
        });

        var observer = new DesktopObserver(windowInfoProvider: fakeWinProvider);

        // Filter by process name
        IReadOnlyList<WindowInfo> claudeWindows = observer.ListWindows(filterProcessName: "claude", includeEmptyTitles: false);
        Assert.Single(claudeWindows);
        Assert.Equal("Claude Conversation", claudeWindows[0].Title);

        // Include empty titles
        IReadOnlyList<WindowInfo> allClaudeWindows = observer.ListWindows(filterProcessName: "claude", includeEmptyTitles: true);
        Assert.Equal(2, allClaudeWindows.Count);
    }
}
