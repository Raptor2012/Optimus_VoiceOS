namespace Optimus.Core.Tests;

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Phone;
using Xunit;

public sealed class PhoneTtsTransportTests
{
    [Fact]
    public async Task Endpoint_sends_markers_and_audio_in_call_order()
    {
        int port = FreePort();
        using var endpoint = new PhoneEndpoint(port, IPAddress.Loopback);
        endpoint.Start();
        using var phone = new TcpClient();
        await phone.ConnectAsync(IPAddress.Loopback, port);
        await WaitUntilAsync(() => endpoint.IsConnected);

        endpoint.SendPlaybackStart(12);
        endpoint.SendTtsAudio(new PhoneTtsAudioSegment(12, 0, 22050, 1,
            PhonePcmEncoding.Pcm16LittleEndian, new byte[] { 1, 2 }));
        endpoint.SendPlaybackChime(12);
        endpoint.SendPlaybackEnd(12);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var start = await PhoneFraming.ReadAsync(phone.GetStream(), timeout.Token);
        var audio = await PhoneFraming.ReadAsync(phone.GetStream(), timeout.Token);
        var chime = await PhoneFraming.ReadAsync(phone.GetStream(), timeout.Token);
        var end = await PhoneFraming.ReadAsync(phone.GetStream(), timeout.Token);

        Assert.Equal("start", JsonState(start));
        Assert.Equal(PhoneFrameKind.TtsAudio, audio!.Value.Kind);
        Assert.Equal(22050, PhoneTtsAudio.Decode(audio.Value.Payload).SampleRate);
        Assert.Equal("chime", JsonType(chime));
        Assert.Equal("end", JsonState(end));
    }

    [Fact]
    public async Task Endpoint_reports_phone_playback_drain_generation()
    {
        int port = FreePort();
        using var endpoint = new PhoneEndpoint(port, IPAddress.Loopback);
        var drained = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        endpoint.PlaybackDrained += (_, e) => drained.TrySetResult(e.Generation);
        endpoint.Start();
        using var phone = new TcpClient();
        await phone.ConnectAsync(IPAddress.Loopback, port);
        await WaitUntilAsync(() => endpoint.IsConnected);

        byte[] json = Encoding.UTF8.GetBytes("{\"t\":\"playbackDrained\",\"generation\":27}");
        byte[] frame = PhoneFraming.Encode(PhoneFrameKind.Json, json);
        await phone.GetStream().WriteAsync(frame);

        Assert.Equal(27, await drained.Task.WaitAsync(TimeSpan.FromSeconds(5)));
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
