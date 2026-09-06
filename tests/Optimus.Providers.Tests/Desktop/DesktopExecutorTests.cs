namespace Optimus.Providers.Tests.Desktop;

using System;
using System.Collections.Generic;
using Optimus.Providers.Desktop;
using Optimus.Providers.Windows;
using Xunit;

public sealed class DesktopExecutorTests
{
    private sealed class FakeInputSimulator : IInputSimulator
    {
        public List<(int X, int Y, ClickType ClickType)> Clicks { get; } = new();
        public List<(int DeltaY, int DeltaX, int? X, int? Y)> Scrolls { get; } = new();
        public List<IReadOnlyList<ushort>> Shortcuts { get; } = new();
        public List<string> SentTexts { get; } = new();

        public bool SendMouseClick(int screenX, int screenY, ClickType clickType)
        {
            Clicks.Add((screenX, screenY, clickType));
            return true;
        }

        public bool SendMouseScroll(int deltaY, int deltaX = 0, int? screenX = null, int? screenY = null)
        {
            Scrolls.Add((deltaY, deltaX, screenX, screenY));
            return true;
        }

        public bool SendKeyboardShortcut(IReadOnlyList<ushort> virtualKeys)
        {
            Shortcuts.Add(virtualKeys);
            return true;
        }

        public bool SendUnicodeText(string text)
        {
            SentTexts.Add(text);
            return true;
        }
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public string? CurrentText { get; set; } = "user_original_clipboard";
        public string? LastRestoredText { get; private set; }

        public string? GetText() => CurrentText;

        public void SetText(string text)
        {
            CurrentText = text;
        }

        public void Restore(string? previousText)
        {
            LastRestoredText = previousText;
            CurrentText = previousText;
        }
    }

    private sealed class FakeAccessibleInvoker : IUiAccessibleInvoker
    {
        public bool InvocationSupported { get; set; } = true;
        public string? LastInvokedId { get; private set; }
        public bool AccessibleTextSupported { get; set; } = true;
        public string? ExistingText { get; set; } = string.Empty;
        public string? ResultingText { get; private set; }

        public bool TryInvokeElement(IntPtr hwnd, string? elementId, string? elementName, string? controlType, out string detail)
        {
            if (!InvocationSupported)
            {
                detail = "Invoke pattern not supported.";
                return false;
            }

            LastInvokedId = elementId ?? elementName;
            detail = "Invoked via InvokePattern.";
            return true;
        }

        public bool TrySetAccessibleText(IntPtr hwnd, string? elementId, string newText, bool preserveExisting, out string actualText, out string detail)
        {
            if (!AccessibleTextSupported)
            {
                actualText = string.Empty;
                detail = "ValuePattern not supported.";
                return false;
            }

            string combined = newText;
            if (preserveExisting && !string.IsNullOrWhiteSpace(ExistingText))
            {
                combined = ExistingText + "\n" + newText;
            }

            ResultingText = combined;
            actualText = combined;
            detail = "ValuePattern set.";
            return true;
        }
    }

    private sealed class StubObserver : IDesktopObserver
    {
        public List<AccessibleControlInfo> Controls { get; } = new();

        public IReadOnlyList<WindowInfo> ListWindows(string? filterProcessName = null, bool includeEmptyTitles = false) => Array.Empty<WindowInfo>();

        public WindowAccessibilitySnapshot InspectAccessibility(IntPtr hwnd, int maxDepth = 6, int maxElements = 150, string? filterControlType = null) =>
            new(hwnd, 1, "Stub", Controls, "hash", DateTimeOffset.UtcNow);

        public CapturedScreen CaptureWindow(IntPtr hwnd, ScreenRegion? region = null, double scaleFactor = 1.0) =>
            new(Array.Empty<byte>(), 0, 0, 0, 0, 1.0, DateTimeOffset.UtcNow);

        public IReadOnlyList<VisibleTextNode> ReadContent(IntPtr hwnd) => Array.Empty<VisibleTextNode>();

        public ObservationSnapshot ObserveWindow(IntPtr hwnd) =>
            new(new WindowInfo(hwnd, 1, "stub", "Stub", "Stub", new ScreenRegion(0, 0, 100, 100), true, true, false),
                InspectAccessibility(hwnd), DateTimeOffset.UtcNow, true);
    }

    [Fact]
    public void InvokeElement_PrefersAccessiblePatternOverCoordinates()
    {
        var invoker = new FakeAccessibleInvoker { InvocationSupported = true };
        var inputSim = new FakeInputSimulator();
        var executor = new DesktopExecutor(
            accessibleInvoker: invoker,
            inputSimulator: inputSim);

        ActionResult result = executor.InvokeAccessibleElement(new IntPtr(42), elementId: "btn_submit");

        Assert.Equal(ActionStatus.Completed, result.Status);
        Assert.Equal("btn_submit", invoker.LastInvokedId);
        Assert.Empty(inputSim.Clicks); // Must NOT click coordinates when accessible pattern succeeds
    }

    [Fact]
    public void InvokeElement_FallsBackToCoordinatesWhenPatternMissing()
    {
        var invoker = new FakeAccessibleInvoker { InvocationSupported = false };
        var inputSim = new FakeInputSimulator();
        var observer = new StubObserver();
        observer.Controls.Add(new AccessibleControlInfo(
            "btn_canvas", "Click Me", "Button", new ScreenRegion(200, 300, 100, 40), true, false, true, false, Array.Empty<string>()));

        var executor = new DesktopExecutor(
            observer: observer,
            accessibleInvoker: invoker,
            inputSimulator: inputSim);

        ActionResult result = executor.InvokeAccessibleElement(new IntPtr(42), elementId: "btn_canvas");

        Assert.Equal(ActionStatus.Completed, result.Status);
        Assert.Contains("clicked center coordinates", result.Detail);
        Assert.Single(inputSim.Clicks);
        Assert.Equal(250, inputSim.Clicks[0].X); // 200 + 100/2
        Assert.Equal(320, inputSim.Clicks[0].Y); // 300 + 40/2
    }

    [Fact]
    public void EnterText_PreservesUnsentContentAndAvoidsOverwriting()
    {
        var invoker = new FakeAccessibleInvoker
        {
            AccessibleTextSupported = true,
            ExistingText = "Draft: investigate tapping bug"
        };
        var executor = new DesktopExecutor(accessibleInvoker: invoker);

        ActionResult result = executor.EnterText(
            "Mention holding fails too.",
            new IntPtr(42),
            preserveExisting: true,
            mode: TextEntryMode.Accessible);

        Assert.Equal(ActionStatus.Completed, result.Status);
        Assert.Equal("Draft: investigate tapping bug\nMention holding fails too.", invoker.ResultingText);
    }

    [Fact]
    public void EnterText_UsesClipboardWithOriginalClipboardRestoration()
    {
        var invoker = new FakeAccessibleInvoker { AccessibleTextSupported = false };
        var clipboard = new FakeClipboardService { CurrentText = "original_copied_code" };
        var inputSim = new FakeInputSimulator();

        var executor = new DesktopExecutor(
            accessibleInvoker: invoker,
            clipboardService: clipboard,
            inputSimulator: inputSim);

        ActionResult result = executor.EnterText(
            "New prompt to enter into box\nwith newline",
            new IntPtr(42),
            mode: TextEntryMode.Clipboard);

        Assert.Equal(ActionStatus.Completed, result.Status);
        Assert.Contains("clipboard paste with original clipboard restored", result.Detail);
        Assert.Single(inputSim.Shortcuts); // Sent Ctrl+V
        Assert.Equal("original_copied_code", clipboard.LastRestoredText);
        Assert.Equal("original_copied_code", clipboard.CurrentText); // Original clipboard restored!
    }

    [Fact]
    public void PressShortcut_ParsesAndDispatchesModifierCombinations()
    {
        var inputSim = new FakeInputSimulator();
        var executor = new DesktopExecutor(inputSimulator: inputSim);

        ActionResult res1 = executor.PressShortcut("Ctrl+Shift+P", new IntPtr(42));
        Assert.Equal(ActionStatus.Completed, res1.Status);
        Assert.Single(inputSim.Shortcuts);
        IReadOnlyList<ushort> keys = inputSim.Shortcuts[0];
        Assert.Contains(NativeWindowApi.VK_CONTROL, keys);
        Assert.Contains(NativeWindowApi.VK_SHIFT, keys);
        Assert.Contains((ushort)'P', keys);

        ActionResult res2 = executor.PressShortcut("Alt+F4", new IntPtr(42));
        Assert.Equal(ActionStatus.Completed, res2.Status);
        Assert.Equal(2, inputSim.Shortcuts.Count);
        IReadOnlyList<ushort> keys2 = inputSim.Shortcuts[1];
        Assert.Contains(NativeWindowApi.VK_MENU, keys2);
        Assert.Contains((ushort)0x73, keys2); // VK_F4 = 0x70 + 3
    }
}
