namespace Optimus.Core.Tests;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Phone;
using Optimus.Core.Speech;
using Optimus.Inference;
using Xunit;

public sealed class PhoneSpokenReviewTests
{
    [Fact]
    public async Task SpeakReviewAsync_WhenPhoneDisconnected_FailsImmediatelyWithoutSpeaking()
    {
        int port = FreePort();
        using var endpoint = new PhoneEndpoint(port, IPAddress.Loopback);
        // Not started and not connected

        bool synthCalled = false;
        using var review = new PhoneSpokenReview(
            (text, token) =>
            {
                synthCalled = true;
                return Task.FromResult(new SpeechSegment(text, new byte[100], 22050, 10, 20));
            },
            endpoint);

        SpokenReviewResult result = await review.SpeakReviewAsync("Hello world", "Claude");

        Assert.Equal(SpokenReviewOutcome.Failed, result.Outcome);
        Assert.False(result.ApprovalMayBegin);
        Assert.Contains("disconnected", result.FailureDetail, StringComparison.OrdinalIgnoreCase);
        Assert.False(synthCalled);
    }

    [Fact]
    public async Task SpeakReviewAsync_StreamsTtsSegmentsAndAwaitsPlaybackDrained()
    {
        int port = FreePort();
        using var endpoint = new PhoneEndpoint(port, IPAddress.Loopback);
        endpoint.Start();

        using var phone = new TcpClient();
        await phone.ConnectAsync(IPAddress.Loopback, port);
        await WaitUntilAsync(() => endpoint.IsConnected);

        var synthesizedLines = new List<string>();
        using var review = new PhoneSpokenReview(
            (text, token) =>
            {
                synthesizedLines.Add(text);
                return Task.FromResult(new SpeechSegment(text, new byte[] { 1, 2, 3, 4 }, 22050, 15, 30));
            },
            endpoint);

        var speakTask = review.SpeakReviewAsync("Short draft.", "Claude");

        // Read frames on phone client
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        NetworkStream stream = phone.GetStream();

        var startFrame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        Assert.Equal("start", JsonState(startFrame));

        // Wait for review lines to be synthesized and sent
        for (int i = 0; i < 3; i++)
        {
            var audioFrame = await PhoneFraming.ReadAsync(stream, timeout.Token);
            Assert.NotNull(audioFrame);
            Assert.Equal(PhoneFrameKind.TtsAudio, audioFrame.Value.Kind);
        }

        var chimeFrame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        Assert.Equal("chime", JsonType(chimeFrame));

        var endFrame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        Assert.Equal("end", JsonState(endFrame));

        // Verify task is still waiting for drain
        await Task.Delay(50);
        Assert.False(speakTask.IsCompleted);

        // Send playbackDrained from phone
        byte[] drainJson = Encoding.UTF8.GetBytes("{\"t\":\"playbackDrained\",\"generation\":1}");
        byte[] drainFrame = PhoneFraming.Encode(PhoneFrameKind.Json, drainJson);
        await stream.WriteAsync(drainFrame, timeout.Token);

        SpokenReviewResult result = await speakTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SpokenReviewOutcome.Completed, result.Outcome);
        Assert.True(result.ApprovalMayBegin);
        Assert.NotNull(result.Timings);
        Assert.Equal(15, result.Timings.TimeToFirstAudioMs);
    }

    [Fact]
    public async Task SpeakReviewAsync_WhenCancelled_SendsCancelPlayback()
    {
        int port = FreePort();
        using var endpoint = new PhoneEndpoint(port, IPAddress.Loopback);
        endpoint.Start();

        using var phone = new TcpClient();
        await phone.ConnectAsync(IPAddress.Loopback, port);
        await WaitUntilAsync(() => endpoint.IsConnected);

        var pauseTcs = new TaskCompletionSource<bool>();
        using var review = new PhoneSpokenReview(
            async (text, token) =>
            {
                await pauseTcs.Task.WaitAsync(token);
                return new SpeechSegment(text, new byte[10], 22050, 10, 20);
            },
            endpoint);

        using var cts = new CancellationTokenSource();
        var speakTask = review.SpeakReviewAsync("Draft text to cancel.", "Claude", cts.Token);

        // Let it start and begin synthesis
        await Task.Delay(50);
        cts.Cancel();

        SpokenReviewResult result = await speakTask;
        Assert.Equal(SpokenReviewOutcome.Cancelled, result.Outcome);

        // Verify phone client receives cancel frame
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        NetworkStream stream = phone.GetStream();
        var startFrame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        Assert.Equal("start", JsonState(startFrame));

        var cancelFrame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        Assert.Equal("cancel", JsonState(cancelFrame));
    }

    [Fact]
    public async Task RepeatedReview_FollowedByNarration_UsesStrictlyIncreasingGenerations()
    {
        int port = FreePort();
        using var endpoint = new PhoneEndpoint(port, IPAddress.Loopback);
        endpoint.Start();

        using var phone = new TcpClient();
        await phone.ConnectAsync(IPAddress.Loopback, port);
        await WaitUntilAsync(() => endpoint.IsConnected);

        using var review = new PhoneSpokenReview(
            (text, token) => Task.FromResult(new SpeechSegment(text, new byte[4], 22050, 5, 10)),
            endpoint);

        var firstSpeak = review.SpeakReviewAsync("First draft", "Claude");
        long firstGen = endpoint.CurrentPlaybackGeneration;

        NetworkStream stream = phone.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        while (frame != null && (frame.Value.Kind != PhoneFrameKind.Json || JsonType(frame) != "playback" || JsonState(frame) != "end"))
        {
            frame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        }
        byte[] drainJson = Encoding.UTF8.GetBytes($"{{\"t\":\"playbackDrained\",\"generation\":{firstGen}}}");
        await stream.WriteAsync(PhoneFraming.Encode(PhoneFrameKind.Json, drainJson), timeout.Token);
        await firstSpeak;

        // Second review
        var secondSpeak = review.SpeakReviewAsync("Second draft", "Claude");
        long secondGen = endpoint.CurrentPlaybackGeneration;
        Assert.True(secondGen > firstGen);

        frame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        while (frame != null && (frame.Value.Kind != PhoneFrameKind.Json || JsonType(frame) != "playback" || JsonState(frame) != "end"))
        {
            frame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        }
        drainJson = Encoding.UTF8.GetBytes($"{{\"t\":\"playbackDrained\",\"generation\":{secondGen}}}");
        await stream.WriteAsync(PhoneFraming.Encode(PhoneFrameKind.Json, drainJson), timeout.Token);
        await secondSpeak;

        // Next generation used for narration
        long narrationGen = endpoint.NextPlaybackGeneration();
        Assert.True(narrationGen > secondGen);
    }

    private static string JsonState((PhoneFrameKind Kind, byte[] Payload)? frame)
    {
        Assert.NotNull(frame);
        Assert.Equal(PhoneFrameKind.Json, frame!.Value.Kind);
        using JsonDocument document = JsonDocument.Parse(frame.Value.Payload);
        return document.RootElement.GetProperty("state").GetString()!;
    }

    private static string JsonType((PhoneFrameKind Kind, byte[] Payload)? frame)
    {
        Assert.NotNull(frame);
        using JsonDocument document = JsonDocument.Parse(frame!.Value.Payload);
        return document.RootElement.GetProperty("t").GetString()!;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
