namespace Optimus.Core.Tests.Monitoring;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Memory;
using Optimus.Core.Monitoring;
using Optimus.Providers.Desktop;
using Optimus.Providers.Windows;
using Xunit;

public sealed class ResponseMonitorTests : IDisposable
{
    private readonly MemoryStore _memoryStore;

    public ResponseMonitorTests()
    {
        _memoryStore = MemoryStore.CreateInMemory();
    }

    public void Dispose()
    {
        _memoryStore.Dispose();
    }

    private sealed class FakeDesktopObserver : IDesktopObserver
    {
        private readonly Queue<IReadOnlyList<VisibleTextNode>> _frames = new();
        private IReadOnlyList<VisibleTextNode> _current = Array.Empty<VisibleTextNode>();

        public void EnqueueFrame(params string[] texts)
        {
            var nodes = new List<VisibleTextNode>();
            for (int i = 0; i < texts.Length; i++)
            {
                nodes.Add(new VisibleTextNode($"k{i}", texts[i]));
            }
            _frames.Enqueue(nodes);
        }

        public IReadOnlyList<VisibleTextNode> ReadContent(IntPtr hwnd)
        {
            if (_frames.Count > 0)
            {
                _current = _frames.Dequeue();
            }
            return _current;
        }

        public CapturedScreen CaptureWindow(IntPtr hwnd, ScreenRegion? region = null, double scaleFactor = 1.0) =>
            throw new NotImplementedException();

        public WindowAccessibilitySnapshot InspectAccessibility(IntPtr hwnd, int maxDepth = 6, int maxElements = 150, string? filterControlType = null) =>
            throw new NotImplementedException();

        public IReadOnlyList<WindowInfo> ListWindows(string? filterProcessName = null, bool includeEmptyTitles = false) =>
            throw new NotImplementedException();

        public ObservationSnapshot ObserveWindow(IntPtr hwnd) =>
            throw new NotImplementedException();
    }

    [Fact]
    public async Task MonitorResponse_DetectsResponse_StoresInHistory_WhenStable()
    {
        var fakeObserver = new FakeDesktopObserver();
        // Baseline: user's prompt visible
        fakeObserver.EnqueueFrame("User: What is the status of Slice 9?");
        // Frame 1: Assistant begins typing
        fakeObserver.EnqueueFrame("User: What is the status of Slice 9?", "Assistant: Working on it...");
        // Frame 2: Assistant finishes typing
        fakeObserver.EnqueueFrame("User: What is the status of Slice 9?", "Assistant: Slice 9 memory store is complete and verified.");

        _memoryStore.SaveTurn("turn-42", "pc", "What is the status of Slice 9?");

        var options = new ResponseMonitorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            StabilityDuration = TimeSpan.FromMilliseconds(100),
            Timeout = TimeSpan.FromSeconds(2)
        };

        var monitor = new ResponseMonitor(fakeObserver, _memoryStore, options: options);

        string? response = await monitor.MonitorResponseAsync(new IntPtr(1234), "turn-42");

        Assert.NotNull(response);
        Assert.Equal("Assistant: Slice 9 memory store is complete and verified.", response);

        // Verify stored in conversation_history
        IReadOnlyList<ConversationTurnRecord> recent = _memoryStore.GetRecentTurns(1);
        Assert.Single(recent);
        Assert.Equal("turn-42", recent[0].TurnId);
        Assert.Equal("Assistant: Slice 9 memory store is complete and verified.", recent[0].Response);
    }

    [Fact]
    public async Task MonitorResponse_TriggersNarration_WhenNarrateResponsesIsTrue()
    {
        var fakeObserver = new FakeDesktopObserver();
        fakeObserver.EnqueueFrame("Prompt");
        fakeObserver.EnqueueFrame("Prompt", "Response: Tests passed.");

        _memoryStore.SaveTurn("turn-narrate", "pc", "run tests");
        _memoryStore.SetPreference("narrate_responses", "true");

        string? spokenText = null;
        using var narrationScheduler = new NarrationScheduler(
            customPlayer: (text, ct) =>
            {
                spokenText = text;
                return Task.CompletedTask;
            });

        var options = new ResponseMonitorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            StabilityDuration = TimeSpan.FromMilliseconds(60),
            Timeout = TimeSpan.FromSeconds(2)
        };

        var monitor = new ResponseMonitor(fakeObserver, _memoryStore, narrationScheduler, options);

        string? response = await monitor.MonitorResponseAsync(new IntPtr(1234), "turn-narrate");

        Assert.NotNull(response);

        // Allow queue processor to dispatch
        for (int i = 0; i < 20 && spokenText == null; i++)
        {
            await Task.Delay(25);
        }

        Assert.NotNull(spokenText);
        Assert.Contains("Response: Tests passed.", spokenText);
    }

    [Fact]
    public async Task MonitorResponse_DoesNotTriggerNarration_WhenNarrateResponsesIsFalse()
    {
        var fakeObserver = new FakeDesktopObserver();
        fakeObserver.EnqueueFrame("Prompt");
        fakeObserver.EnqueueFrame("Prompt", "Response: Tests passed.");

        _memoryStore.SaveTurn("turn-silent", "pc", "run tests");
        _memoryStore.SetPreference("narrate_responses", "false");

        string? spokenText = null;
        using var narrationScheduler = new NarrationScheduler(
            customPlayer: (text, ct) =>
            {
                spokenText = text;
                return Task.CompletedTask;
            });

        var options = new ResponseMonitorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            StabilityDuration = TimeSpan.FromMilliseconds(60),
            Timeout = TimeSpan.FromSeconds(2)
        };

        var monitor = new ResponseMonitor(fakeObserver, _memoryStore, narrationScheduler, options);

        await monitor.MonitorResponseAsync(new IntPtr(1234), "turn-silent");

        await Task.Delay(100);
        Assert.Null(spokenText);
    }

    [Fact]
    public void ExtractNewText_PrefixMatch_ReturnsDifference()
    {
        string baseline = "Line 1\nLine 2";
        string current = "Line 1\nLine 2\nLine 3 - New Response";

        string extracted = ResponseMonitor.ExtractNewText(baseline, current);

        Assert.Equal("Line 3 - New Response", extracted);
    }
}
