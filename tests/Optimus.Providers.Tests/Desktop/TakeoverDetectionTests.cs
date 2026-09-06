namespace Optimus.Providers.Tests.Desktop;

using System;
using Optimus.Providers.Desktop;
using Xunit;

public sealed class TakeoverDetectionTests
{
    [Fact]
    public void PhysicalInput_TriggersTakeoverAndRecordsTimestamp()
    {
        var detector = new ManualTakeoverDetector();
        bool eventFired = false;
        detector.TakeoverTriggered += (s, e) =>
        {
            eventFired = true;
            Assert.Equal("PhysicalMouse", e.InputType);
        };

        Assert.False(detector.IsTakeoverDetected);
        Assert.Null(detector.LastTakeoverTimeUtc);

        DateTimeOffset before = DateTimeOffset.UtcNow;
        detector.RecordPhysicalInput("PhysicalMouse");

        Assert.True(detector.IsTakeoverDetected);
        Assert.True(eventFired);
        Assert.NotNull(detector.LastTakeoverTimeUtc);
        Assert.True(detector.LastTakeoverTimeUtc >= before);
        Assert.Contains("PhysicalMouse", detector.LastTakeoverReason);
    }

    [Fact]
    public void SyntheticInputWithOptimusSignature_DoesNotTriggerTakeover()
    {
        var detector = new ManualTakeoverDetector();

        // Pass synthetic signature
        detector.ProcessRawInputEvent(
            extraInfo: ManualTakeoverDetector.SyntheticInputSignature,
            isInjectedFlag: false,
            inputType: "Keyboard");

        Assert.False(detector.IsTakeoverDetected);

        // Pass injected flag
        detector.ProcessRawInputEvent(
            extraInfo: IntPtr.Zero,
            isInjectedFlag: true,
            inputType: "Mouse");

        Assert.False(detector.IsTakeoverDetected);
    }

    [Fact]
    public void RealHardwareInput_TriggersTakeoverInRawEventProcessor()
    {
        var detector = new ManualTakeoverDetector();

        // Hardware input: no extra info, no injected flag
        detector.ProcessRawInputEvent(
            extraInfo: IntPtr.Zero,
            isInjectedFlag: false,
            inputType: "PhysicalKeyboard");

        Assert.True(detector.IsTakeoverDetected);
        Assert.Contains("PhysicalKeyboard", detector.LastTakeoverReason);
    }

    [Fact]
    public void Resuming_RequiresFreshObservationAfterTakeover()
    {
        var detector = new ManualTakeoverDetector();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        // An observation taken before the takeover
        DateTimeOffset staleObservation = t0;

        // Takeover happens later
        detector.RecordPhysicalInput("PhysicalMouse");
        DateTimeOffset takeoverTime = detector.LastTakeoverTimeUtc!.Value;

        // Resuming with stale observation must fail
        Assert.False(detector.CanResume(staleObservation));
        Assert.Throws<InvalidOperationException>(() => detector.ResumeWithFreshObservation(staleObservation));

        // Resuming with an observation from the exact takeover moment must fail
        Assert.False(detector.CanResume(takeoverTime));

        // Resuming with fresh observation taken after takeover must succeed
        DateTimeOffset freshObservation = takeoverTime.AddMilliseconds(50);
        Assert.True(detector.CanResume(freshObservation));

        detector.ResumeWithFreshObservation(freshObservation);
        Assert.False(detector.IsTakeoverDetected);
    }

    [Fact]
    public void DesktopExecutor_PausesWhenTakeoverIsDetected()
    {
        var detector = new ManualTakeoverDetector();
        var executor = new DesktopExecutor(takeoverDetector: detector);

        detector.RecordPhysicalInput("UserMove");

        // Attempting to focus or click without a fresh observation returns Paused
        ActionResult result = executor.ClickCoordinates(100, 100);

        Assert.Equal(ActionStatus.Paused, result.Status);
        Assert.Contains("Manual user takeover detected", result.Detail);
    }

    [Fact]
    public void DesktopExecutor_AllowsExecutionWithFreshObservationAfterTakeover()
    {
        var detector = new ManualTakeoverDetector();
        var executor = new DesktopExecutor(takeoverDetector: detector);

        detector.RecordPhysicalInput("UserKey");
        DateTimeOffset freshTime = detector.LastTakeoverTimeUtc!.Value.AddSeconds(1);

        // Supplying a fresh observation timestamp clears the takeover and executes
        ActionResult result = executor.ClickCoordinates(100, 100, observationTimestamp: freshTime);

        Assert.NotEqual(ActionStatus.Paused, result.Status);
        Assert.False(detector.IsTakeoverDetected);
    }
}
