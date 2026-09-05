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
using Xunit;

/// <summary>
/// Exercises the real endpoint over a real loopback socket: framing, the message set, and
/// disconnect/reconnect.
/// </summary>
public class PhoneEndpointTests : IDisposable
{
    private readonly PhoneEndpoint _endpoint;
    private readonly int _port;

    public PhoneEndpointTests()
    {
        _port = FreePort();
        _endpoint = new PhoneEndpoint(_port, IPAddress.Loopback);
        _endpoint.Start();
    }

    public void Dispose()
    {
        _endpoint.Dispose();
        GC.SuppressFinalize(this);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public void Framing_RoundTripsThroughEncodeAndRead()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"t\":\"hello\"}");
        byte[] frame = PhoneFraming.Encode(PhoneFrameKind.Json, payload);

        // 1 byte kind + 4 byte big-endian length + payload.
        Assert.Equal(PhoneFraming.HeaderBytes + payload.Length, frame.Length);
        Assert.Equal((byte)PhoneFrameKind.Json, frame[0]);
        Assert.Equal(0, frame[1]);
        Assert.Equal(0, frame[2]);
        Assert.Equal(0, frame[3]);
        Assert.Equal(payload.Length, frame[4]);
    }

    [Fact]
    public void Framing_RejectsOversizePayload()
    {
        Assert.Throws<ArgumentException>(() =>
            PhoneFraming.Encode(PhoneFrameKind.Audio, new byte[PhoneFraming.MaxPayloadBytes + 1]));
    }

    [Fact]
    public async Task Phone_ConnectsAndIsReportedConnected()
    {
        var connected = new TaskCompletionSource<bool>();
        _endpoint.ConnectionChanged += (_, e) =>
        {
            if (e.Connected)
            {
                connected.TrySetResult(true);
            }
        };

        using TcpClient phone = await DialAsync();

        Assert.True(await WithTimeout(connected.Task), "Endpoint never reported the phone as connected.");
        Assert.True(_endpoint.IsConnected);
    }

    /// <summary>The full utterance shape: start, audio frames, stop.</summary>
    [Fact]
    public async Task Phone_StartAudioStop_DeliversEveryByteInOrder()
    {
        var started = new TaskCompletionSource<bool>();
        var stopped = new TaskCompletionSource<bool>();
        var received = new List<byte>();

        _endpoint.CaptureStarted += (_, _) => started.TrySetResult(true);
        _endpoint.CaptureStopped += (_, _) => stopped.TrySetResult(true);
        _endpoint.AudioReceived += (_, e) =>
        {
            lock (received)
            {
                received.AddRange(e.Pcm);
            }
        };

        using TcpClient phone = await DialAsync();
        NetworkStream stream = phone.GetStream();

        await SendJsonAsync(stream, "{\"t\":\"startCapture\"}");
        Assert.True(await WithTimeout(started.Task), "startCapture was not observed.");

        // Three 20 ms frames of distinguishable PCM.
        var expected = new List<byte>();
        for (int i = 0; i < 3; i++)
        {
            byte[] chunk = new byte[640];
            Array.Fill(chunk, (byte)(i + 1));
            expected.AddRange(chunk);
            await SendFrameAsync(stream, PhoneFrameKind.Audio, chunk);
        }

        await SendJsonAsync(stream, "{\"t\":\"stopCapture\"}");
        Assert.True(await WithTimeout(stopped.Task), "stopCapture was not observed.");

        lock (received)
        {
            Assert.Equal(expected.Count, received.Count);
            Assert.Equal(expected, received);
        }
    }

    [Fact]
    public async Task Pc_PushesStatusAndDraftToThePhone()
    {
        var connected = new TaskCompletionSource<bool>();
        _endpoint.ConnectionChanged += (_, e) =>
        {
            if (e.Connected)
            {
                connected.TrySetResult(true);
            }
        };

        using TcpClient phone = await DialAsync();
        NetworkStream stream = phone.GetStream();
        Assert.True(await WithTimeout(connected.Task));

        _endpoint.SendStatus("listening", "Listening...");
        _endpoint.SendDraft("raw words", "Cleaned words.", "audio 1.0s");

        JsonElement status = await ReadJsonAsync(stream);
        Assert.Equal("status", status.GetProperty("t").GetString());
        Assert.Equal("listening", status.GetProperty("state").GetString());
        Assert.Equal("Listening...", status.GetProperty("line").GetString());

        JsonElement draft = await ReadJsonAsync(stream);
        Assert.Equal("draft", draft.GetProperty("t").GetString());
        Assert.Equal("raw words", draft.GetProperty("raw").GetString());
        Assert.Equal("Cleaned words.", draft.GetProperty("clean").GetString());
        Assert.Equal("audio 1.0s", draft.GetProperty("timings").GetString());
    }

    /// <summary>Reconnect is just dialling again; no resume handshake exists or is needed.</summary>
    [Fact]
    public async Task Phone_CanDisconnectAndReconnect()
    {
        var events = new List<bool>();
        var secondConnect = new TaskCompletionSource<bool>();

        _endpoint.ConnectionChanged += (_, e) =>
        {
            lock (events)
            {
                events.Add(e.Connected);
                if (e.Connected && events.FindAll(x => x).Count == 2)
                {
                    secondConnect.TrySetResult(true);
                }
            }
        };

        TcpClient first = await DialAsync();
        await Task.Delay(200);
        first.Close();
        first.Dispose();
        await Task.Delay(300);

        using TcpClient second = await DialAsync();
        Assert.True(await WithTimeout(secondConnect.Task), "Endpoint did not accept a reconnect.");
        Assert.True(_endpoint.IsConnected);

        // And the reconnected phone still works.
        var started = new TaskCompletionSource<bool>();
        _endpoint.CaptureStarted += (_, _) => started.TrySetResult(true);
        await SendJsonAsync(second.GetStream(), "{\"t\":\"startCapture\"}");
        Assert.True(await WithTimeout(started.Task), "Reconnected phone could not start capture.");
    }

    /// <summary>An unknown message must be ignored, not crash the connection.</summary>
    [Fact]
    public async Task UnknownMessage_IsIgnoredAndConnectionSurvives()
    {
        using TcpClient phone = await DialAsync();
        NetworkStream stream = phone.GetStream();

        await SendJsonAsync(stream, "{\"t\":\"somethingElse\",\"x\":1}");
        await SendJsonAsync(stream, "not json at all");

        var started = new TaskCompletionSource<bool>();
        _endpoint.CaptureStarted += (_, _) => started.TrySetResult(true);
        await SendJsonAsync(stream, "{\"t\":\"startCapture\"}");

        Assert.True(await WithTimeout(started.Task), "Connection did not survive unknown input.");
    }

    [Fact]
    public async Task EditDraft_RaisesDraftEditedEvent()
    {
        using TcpClient phone = await DialAsync();
        NetworkStream stream = phone.GetStream();

        var edited = new TaskCompletionSource<string>();
        _endpoint.DraftEdited += (_, text) => edited.TrySetResult(text);

        await SendJsonAsync(stream, "{\"t\":\"editDraft\",\"text\":\"updated draft from phone\"}");

        Assert.Equal("updated draft from phone", await edited.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void NextPlaybackGeneration_IncrementsMonotonically()
    {
        long gen1 = _endpoint.NextPlaybackGeneration();
        long gen2 = _endpoint.NextPlaybackGeneration();
        long gen3 = _endpoint.NextPlaybackGeneration();

        Assert.True(gen1 < gen2);
        Assert.True(gen2 < gen3);
        Assert.Equal(gen3, _endpoint.CurrentPlaybackGeneration);
    }

    private async Task<TcpClient> DialAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        await Task.Delay(150); // Let the accept loop attach.
        return client;
    }

    private static Task SendJsonAsync(NetworkStream stream, string json) =>
        SendFrameAsync(stream, PhoneFrameKind.Json, Encoding.UTF8.GetBytes(json));

    private static async Task SendFrameAsync(NetworkStream stream, PhoneFrameKind kind, byte[] payload)
    {
        byte[] frame = PhoneFraming.Encode(kind, payload);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
        await Task.Delay(40);
    }

    private static async Task<JsonElement> ReadJsonAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (PhoneFrameKind Kind, byte[] Payload)? frame = await PhoneFraming.ReadAsync(stream, cts.Token);

        Assert.NotNull(frame);
        Assert.Equal(PhoneFrameKind.Json, frame!.Value.Kind);

        using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(frame.Value.Payload));
        return document.RootElement.Clone();
    }

    private static async Task<bool> WithTimeout(Task<bool> task)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        return completed == task && task.Result;
    }
}
