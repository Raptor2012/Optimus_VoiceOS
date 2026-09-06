namespace Optimus.Providers.Tests.Desktop;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Optimus.Providers.Desktop;
using Optimus.Providers.Windows;
using Xunit;

public sealed class InvalidationTests
{
    private sealed class FakeWindowInfoProvider : IWindowInfoProvider
    {
        public bool WindowExists { get; set; } = true;
        public bool WindowVisible { get; set; } = true;

        public IReadOnlyList<WindowInfo> ListWindows(string? filterProcessName = null, bool includeEmptyTitles = false)
        {
            if (!WindowExists) return Array.Empty<WindowInfo>();
            return new[]
            {
                new WindowInfo(new IntPtr(100), 10, "test", "Test Window", "TestClass", new ScreenRegion(0, 0, 100, 100), true, WindowVisible, false)
            };
        }

        public WindowInfo? GetWindowInfo(IntPtr hwnd)
        {
            if (!WindowExists) return null;
            return new WindowInfo(hwnd, 10, "test", "Test Window", "TestClass", new ScreenRegion(0, 0, 100, 100), true, WindowVisible, false);
        }
    }

    private sealed class FakeObserver : IDesktopObserver
    {
        private readonly FakeWindowInfoProvider _winInfo;
        public int ObservationCount { get; private set; }
        public string CurrentHash { get; set; } = "hash_v1";

        public FakeObserver(FakeWindowInfoProvider winInfo)
        {
            _winInfo = winInfo;
        }

        public IReadOnlyList<WindowInfo> ListWindows(string? filterProcessName = null, bool includeEmptyTitles = false) =>
            _winInfo.ListWindows(filterProcessName, includeEmptyTitles);

        public WindowAccessibilitySnapshot InspectAccessibility(IntPtr hwnd, int maxDepth = 6, int maxElements = 150, string? filterControlType = null) =>
            new(hwnd, 10, "Test Window", Array.Empty<AccessibleControlInfo>(), CurrentHash, DateTimeOffset.UtcNow);

        public CapturedScreen CaptureWindow(IntPtr hwnd, ScreenRegion? region = null, double scaleFactor = 1.0) =>
            new(Array.Empty<byte>(), 100, 100, 0, 0, 1.0, DateTimeOffset.UtcNow);

        public IReadOnlyList<VisibleTextNode> ReadContent(IntPtr hwnd) => Array.Empty<VisibleTextNode>();

        public ObservationSnapshot ObserveWindow(IntPtr hwnd)
        {
            ObservationCount++;
            WindowInfo? info = _winInfo.GetWindowInfo(hwnd);
            bool valid = info != null && info.IsVisible;
            return new ObservationSnapshot(
                Window: info ?? new WindowInfo(hwnd, 0, "", "", "", new ScreenRegion(0, 0, 0, 0), false, false, false),
                Accessibility: new WindowAccessibilitySnapshot(hwnd, 10, "Test Window", Array.Empty<AccessibleControlInfo>(), CurrentHash, DateTimeOffset.UtcNow),
                ObservedAtUtc: DateTimeOffset.UtcNow,
                IsValid: valid);
        }
    }

    [Fact]
    public void ObserveWindow_InvalidatedWhenWindowCloses()
    {
        var winInfo = new FakeWindowInfoProvider();
        var observer = new FakeObserver(winInfo);

        ObservationSnapshot snapshot1 = observer.ObserveWindow(new IntPtr(100));
        Assert.True(snapshot1.IsValid);

        // Window closes
        winInfo.WindowExists = false;

        ObservationSnapshot snapshot2 = observer.ObserveWindow(new IntPtr(100));
        Assert.False(snapshot2.IsValid);
    }

    [Fact]
    public void LayoutHash_ChangesWhenElementsOrBoundsChange()
    {
        var elements1 = new[]
        {
            new AccessibleControlInfo("btn1", "Send", "Button", new ScreenRegion(10, 10, 50, 20), true, false, true, false, new[] { "Invoke" })
        };

        var elements2 = new[]
        {
            // Moved to a different position
            new AccessibleControlInfo("btn1", "Send", "Button", new ScreenRegion(150, 200, 50, 20), true, false, true, false, new[] { "Invoke" })
        };

        var elements3 = new[]
        {
            // Text or id changed
            new AccessibleControlInfo("btn2", "Cancel", "Button", new ScreenRegion(10, 10, 50, 20), true, false, true, false, new[] { "Invoke" })
        };

        string hash1 = UiaAccessibilityInspector.ComputeLayoutHash(elements1);
        string hash2 = UiaAccessibilityInspector.ComputeLayoutHash(elements2);
        string hash3 = UiaAccessibilityInspector.ComputeLayoutHash(elements3);

        Assert.NotEmpty(hash1);
        Assert.NotEqual(hash1, hash2);
        Assert.NotEqual(hash1, hash3);
    }

    [Fact]
    public async Task WaitForStateChange_SucceedsWhenConditionMet()
    {
        var winInfo = new FakeWindowInfoProvider();
        var observer = new FakeObserver(winInfo) { CurrentHash = "hash_initial" };
        var executor = new DesktopExecutor(observer: observer);

        IntPtr hwnd = new IntPtr(100);
        ObservationSnapshot initial = observer.ObserveWindow(hwnd);

        // Run wait task
        Task<ActionResult> waitTask = executor.WaitForStateChangeAsync(
            hwnd, initial, predicate: snap => snap.LayoutHash == "hash_target", timeout: TimeSpan.FromMilliseconds(500));

        // State changes after 50ms
        await Task.Delay(50);
        observer.CurrentHash = "hash_target";

        ActionResult result = await waitTask;
        Assert.Equal(ActionStatus.Completed, result.Status);
        Assert.Contains("Expected state change observed", result.Detail);
    }

    [Fact]
    public async Task WaitForStateChange_OneReobservationOnLayoutChange_ThenReportsObstacle()
    {
        var winInfo = new FakeWindowInfoProvider();
        var observer = new FakeObserver(winInfo) { CurrentHash = "hash_initial" };
        var executor = new DesktopExecutor(observer: observer);

        IntPtr hwnd = new IntPtr(100);
        ObservationSnapshot initial = observer.ObserveWindow(hwnd);

        // Layout shifts unexpectedly to an unknown layout
        observer.CurrentHash = "hash_unexpected_shift";

        ActionResult result = await executor.WaitForStateChangeAsync(
            hwnd, initial, predicate: snap => snap.LayoutHash == "hash_target", timeout: TimeSpan.FromMilliseconds(500));

        // Layout changed, condition was not satisfied upon reobservation -> reports obstacle immediately
        Assert.Equal(ActionStatus.Unresolved, result.Status);
        Assert.Contains("Obstacle: window layout changed unexpectedly", result.Detail);
        Assert.Contains("hash_initial", result.Detail);
        Assert.Contains("hash_unexpected_shift", result.Detail);
    }

    [Fact]
    public async Task WaitForStateChange_TimesOutWhenDeadlineExpires()
    {
        var winInfo = new FakeWindowInfoProvider();
        var observer = new FakeObserver(winInfo) { CurrentHash = "hash_stable" };
        var executor = new DesktopExecutor(observer: observer);

        IntPtr hwnd = new IntPtr(100);
        ObservationSnapshot initial = observer.ObserveWindow(hwnd);

        ActionResult result = await executor.WaitForStateChangeAsync(
            hwnd, initial, predicate: _ => false, timeout: TimeSpan.FromMilliseconds(150));

        Assert.Equal(ActionStatus.Unresolved, result.Status);
        Assert.Contains("expired without meaningful state change", result.Detail);
    }

    [Fact]
    public async Task WaitForStateChange_StopsWhenWindowCloses()
    {
        var winInfo = new FakeWindowInfoProvider();
        var observer = new FakeObserver(winInfo);
        var executor = new DesktopExecutor(observer: observer);

        IntPtr hwnd = new IntPtr(100);
        ObservationSnapshot initial = observer.ObserveWindow(hwnd);

        Task<ActionResult> waitTask = executor.WaitForStateChangeAsync(
            hwnd, initial, predicate: _ => false, timeout: TimeSpan.FromMilliseconds(500));

        await Task.Delay(50);
        winInfo.WindowExists = false;

        ActionResult result = await waitTask;
        Assert.Equal(ActionStatus.Stopped, result.Status);
        Assert.Contains("closed or became invalid", result.Detail);
    }
}
