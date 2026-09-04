namespace Optimus.Core.Tests;

using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Phone;
using Optimus.Core.Voice;
using Xunit;

public sealed class PhoneApprovalListenerTests
{
    [Fact]
    public async Task ListenForApprovalAsync_WhenDisconnected_ReturnsEmptyAudio()
    {
        int port = FreePort();
        using var endpoint = new PhoneEndpoint(port, IPAddress.Loopback);
        using var listener = new PhoneApprovalListener(endpoint);

        byte[] result = await listener.ListenForApprovalAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task ListenForApprovalAsync_SendsStartAndStop_AndCapturesAudioUntilSilence()
    {
        int port = FreePort();
        using var endpoint = new PhoneEndpoint(port, IPAddress.Loopback);
        endpoint.Start();

        using var phone = new TcpClient();
        await phone.ConnectAsync(IPAddress.Loopback, port);
        await WaitUntilAsync(() => endpoint.IsConnected);

        using var listener = new PhoneApprovalListener(
            endpoint,
            speechThreshold: 0.02f,
            approvalSilenceDuration: TimeSpan.FromMilliseconds(100),
            approvalInitialTimeout: TimeSpan.FromMilliseconds(5000),
            approvalMaxDuration: TimeSpan.FromMilliseconds(10000));

        var listenTask = listener.ListenForApprovalAsync();

        // Phone receives startApprovalCapture
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        NetworkStream stream = phone.GetStream();
        var startFrame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        Assert.Equal("startApprovalCapture", JsonType(startFrame));

        // Send loud audio (speech)
        byte[] loudPcm = new byte[3200]; // 100ms of 16kHz PCM16
        for (int i = 0; i < loudPcm.Length; i += 2)
        {
            // Set samples to ~0.5 amplitude
            short sample = 16000;
            loudPcm[i] = (byte)(sample & 0xFF);
            loudPcm[i + 1] = (byte)((sample >> 8) & 0xFF);
        }

        byte[] audioFrame = PhoneFraming.Encode(PhoneFrameKind.Audio, loudPcm);
        await stream.WriteAsync(audioFrame, timeout.Token);

        // Wait a small moment, then send silence for 150ms to exceed silence duration (100ms)
        await Task.Delay(50);
        byte[] silentPcm = new byte[3200];
        byte[] silenceFrame = PhoneFraming.Encode(PhoneFrameKind.Audio, silentPcm);
        await stream.WriteAsync(silenceFrame, timeout.Token);

        // Phone receives stopApprovalCapture
        var stopFrame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        Assert.Equal("stopApprovalCapture", JsonType(stopFrame));

        byte[] recorded = await listenTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(recorded.Length >= loudPcm.Length);
    }

    [Fact]
    public async Task ListenForApprovalAsync_WhenPhoneDisconnects_ReturnsEmptyAudio()
    {
        int port = FreePort();
        using var endpoint = new PhoneEndpoint(port, IPAddress.Loopback);
        endpoint.Start();

        using var phone = new TcpClient();
        await phone.ConnectAsync(IPAddress.Loopback, port);
        await WaitUntilAsync(() => endpoint.IsConnected);

        using var listener = new PhoneApprovalListener(endpoint);
        var listenTask = listener.ListenForApprovalAsync();

        // Phone receives startApprovalCapture
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        NetworkStream stream = phone.GetStream();
        var startFrame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        Assert.Equal("startApprovalCapture", JsonType(startFrame));

        // Abrupt disconnect
        phone.Close();

        byte[] recorded = await listenTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(recorded);
    }

    [Fact]
    public async Task Cancel_SendsStopApprovalCapture()
    {
        int port = FreePort();
        using var endpoint = new PhoneEndpoint(port, IPAddress.Loopback);
        endpoint.Start();

        using var phone = new TcpClient();
        await phone.ConnectAsync(IPAddress.Loopback, port);
        await WaitUntilAsync(() => endpoint.IsConnected);

        using var listener = new PhoneApprovalListener(endpoint);
        var listenTask = listener.ListenForApprovalAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        NetworkStream stream = phone.GetStream();
        var startFrame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        Assert.Equal("startApprovalCapture", JsonType(startFrame));

        listener.Cancel();

        var stopFrame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        Assert.Equal("stopApprovalCapture", JsonType(stopFrame));

        byte[] recorded = await listenTask;
        Assert.Empty(recorded);
    }

    [Fact]
    public async Task ListenForReplacementDictationAsync_SendsStartRedictationCapture()
    {
        int port = FreePort();
        using var endpoint = new PhoneEndpoint(port, IPAddress.Loopback);
        endpoint.Start();

        using var phone = new TcpClient();
        await phone.ConnectAsync(IPAddress.Loopback, port);
        await WaitUntilAsync(() => endpoint.IsConnected);

        using var listener = new PhoneApprovalListener(
            endpoint,
            dictationInitialTimeout: TimeSpan.FromMilliseconds(100));

        var listenTask = listener.ListenForReplacementDictationAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        NetworkStream stream = phone.GetStream();
        var startFrame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        Assert.Equal("startRedictationCapture", JsonType(startFrame));

        // Wait for initial timeout
        byte[] recorded = await listenTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(recorded);

        var stopFrame = await PhoneFraming.ReadAsync(stream, timeout.Token);
        Assert.Equal("stopApprovalCapture", JsonType(stopFrame));
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
