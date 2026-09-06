namespace Optimus.Core.Tests;

using System;
using Optimus.Shell.Controls;
using Optimus.Shell.Models;
using Xunit;

public class OrbStateReducerTests
{
    [Fact]
    public void NotListeningWhenPaused()
    {
        var frame = OrbStateReducer.Reduce(
            WidgetState.Listening,
            micEnergy: 0.8,
            playbackEnergy: 0.5,
            isSpeakingReview: true,
            isSessionOpen: true,
            isPaused: true,
            isMuted: false,
            breathPhase: 0);

        Assert.Equal(1.0, frame.RadiusScale);
        Assert.Equal(0.35, frame.CoreOpacity);
        Assert.False(frame.MicEligible);
        Assert.False(frame.PlaybackActive);
        Assert.Equal("Not listening", frame.Label);
    }

    [Fact]
    public void InterruptedStateHasContractedScaleAndPromptLabel()
    {
        var frame = OrbStateReducer.Reduce(
            WidgetState.Interrupted,
            micEnergy: 0.0,
            playbackEnergy: 0.0,
            isSpeakingReview: false,
            isSessionOpen: true,
            isPaused: false,
            isMuted: false,
            breathPhase: 0);

        Assert.Equal(0.82, frame.RadiusScale, precision: 2);
        Assert.Equal(0.9, frame.CoreOpacity);
        Assert.Equal(0.0, frame.HaloOpacity);
        Assert.Equal("Interrupted", frame.Label);
        Assert.Equal("#8C9EFF", frame.CenterColor);
        Assert.True(frame.MicEligible);
    }

    [Fact]
    public void InterruptedScalesWithMicEnergy()
    {
        var frame0 = OrbStateReducer.Reduce(WidgetState.Interrupted, 0.0, 0.0, false, true, false, false, 0);
        var frameActive = OrbStateReducer.Reduce(WidgetState.Interrupted, 1.0, 0.0, false, true, false, false, 0);

        Assert.True(frameActive.RadiusScale > frame0.RadiusScale);
        Assert.Equal(0.82 * 1.15, frameActive.RadiusScale, precision: 2);
    }

    [Fact]
    public void AwaitingApprovalHasHaloGlow()
    {
        var frame = OrbStateReducer.Reduce(
            WidgetState.AwaitingApproval,
            micEnergy: 0.0,
            playbackEnergy: 0.0,
            isSpeakingReview: false,
            isSessionOpen: true,
            isPaused: false,
            isMuted: false,
            breathPhase: 0);

        Assert.Equal(0.4, frame.HaloOpacity);
        Assert.Equal("Confirm?", frame.Label);
        Assert.True(frame.MicEligible);
    }

    [Fact]
    public void ConfirmHasHaloGlowAndReviewLabel()
    {
        var frame = OrbStateReducer.Reduce(
            WidgetState.Confirm,
            micEnergy: 0.0,
            playbackEnergy: 0.0,
            isSpeakingReview: false,
            isSessionOpen: true,
            isPaused: false,
            isMuted: false,
            breathPhase: 0);

        Assert.Equal(0.4, frame.HaloOpacity);
        Assert.Equal("Review", frame.Label);
    }

    [Fact]
    public void ListeningExpandsWithMicrophoneEnergy()
    {
        var baseFrame = OrbStateReducer.Reduce(WidgetState.Listening, 0.0, 0.0, false, false, false, false, 0);
        var activeFrame = OrbStateReducer.Reduce(WidgetState.Listening, 0.8, 0.0, false, false, false, false, 0);

        Assert.Equal(1.0, baseFrame.RadiusScale);
        Assert.Equal(1.0 + 0.15 * 0.8, activeFrame.RadiusScale, precision: 3);
        Assert.Equal("Listening", baseFrame.Label);
    }

    [Fact]
    public void SpeakingPulsesWithPlaybackEnergy()
    {
        var frame = OrbStateReducer.Reduce(
            WidgetState.ReadingDraft,
            micEnergy: 0.0,
            playbackEnergy: 0.5,
            isSpeakingReview: true,
            isSessionOpen: false,
            isPaused: false,
            isMuted: false,
            breathPhase: 0);

        Assert.Equal(1.0 + 0.08 * 0.5, frame.RadiusScale, precision: 3);
        Assert.True(frame.PlaybackActive);
        Assert.Equal("Speaking", frame.Label);
    }

    [Fact]
    public void MutedSuppressesPlaybackActive()
    {
        var frame = OrbStateReducer.Reduce(
            WidgetState.ReadingDraft,
            micEnergy: 0.0,
            playbackEnergy: 0.5,
            isSpeakingReview: true,
            isSessionOpen: false,
            isPaused: false,
            isMuted: true,
            breathPhase: 0);

        Assert.False(frame.PlaybackActive);
    }

    [Fact]
    public void IdleBreathingPulses()
    {
        var frame0 = OrbStateReducer.Reduce(WidgetState.Idle, 0.0, 0.0, false, false, false, false, 0);
        var framePeak = OrbStateReducer.Reduce(WidgetState.Idle, 0.0, 0.0, false, false, false, false, Math.PI / 2);

        Assert.Equal(1.0, frame0.RadiusScale, precision: 3);
        Assert.Equal(1.03, framePeak.RadiusScale, precision: 3);
    }

    [Fact]
    public void ProcessingHasRotationSpeed()
    {
        var frame = OrbStateReducer.Reduce(
            WidgetState.Processing,
            micEnergy: 0.0,
            playbackEnergy: 0.0,
            isSpeakingReview: false,
            isSessionOpen: false,
            isPaused: false,
            isMuted: false,
            breathPhase: 0);

        Assert.Equal(120, frame.RotationSpeed);
        Assert.Equal("Processing", frame.Label);
        Assert.False(frame.MicEligible);
    }
}
