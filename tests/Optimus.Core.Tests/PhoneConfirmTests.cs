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
using Optimus.Providers;
using Optimus.Providers.Windows;
using Optimus.Shell;
using Xunit;

/// <summary>
/// The S005 rule: a phone-originated prompt reaches the exact visible destination, carries the
/// exact visible text, and only after an explicit confirm.
/// </summary>
public class PhoneConfirmTests : IDisposable
{
    private readonly PhoneEndpoint _endpoint;
    private readonly int _port;

    public PhoneConfirmTests()
    {
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        _endpoint = new PhoneEndpoint(_port, IPAddress.Loopback);
        _endpoint.Start();
    }

    public void Dispose()
    {
        _endpoint.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The whole point: the adapter receives the phone's text verbatim.</summary>
    [Fact]
    public async Task Confirm_SendsTheExactVisibleTextToTheChosenDestination()
    {
        var claude = new RecordingAdapter("claude", "Claude", ready: true);
        var codex = new RecordingAdapter("codex", "Codex", ready: true);
        var registry = new DestinationRegistry(new IDestinationAdapter[] { claude, codex });

        using var session = new PhoneSession(_endpoint, pipeline: null, destinations: registry);
        using TcpClient phone = await DialAsync();
        NetworkStream stream = phone.GetStream();

        await DrainAsync(stream, 2); // status + destinations on connect

        const string edited = "Commit and push to the main branch, then run the tests.";
        await SendAsync(stream, $"{{\"t\":\"confirm\",\"destinationId\":\"codex\",\"text\":{JsonSerializer.Serialize(edited)}}}");

        await WaitUntilAsync(() => codex.Sent.Count == 1);

        Assert.Equal(new[] { edited }, codex.Sent);
        Assert.Empty(claude.Sent); // never the other destination
    }

    /// <summary>Nothing is sent without a confirm message.</summary>
    [Fact]
    public async Task NoSendHappensWithoutConfirm()
    {
        var claude = new RecordingAdapter("claude", "Claude", ready: true);
        var registry = new DestinationRegistry(new IDestinationAdapter[] { claude });

        using var session = new PhoneSession(_endpoint, pipeline: null, destinations: registry);
        using TcpClient phone = await DialAsync();
        NetworkStream stream = phone.GetStream();

        await SendAsync(stream, "{\"t\":\"startCapture\"}");
        await SendAsync(stream, "{\"t\":\"stopCapture\"}");
        await SendAsync(stream, "{\"t\":\"refreshDestinations\"}");
        await Task.Delay(400);

        Assert.Empty(claude.Sent);
    }

    /// <summary>An unbound destination fails and never falls back to a ready one.</summary>
    [Fact]
    public async Task Confirm_ToAnUnreadyDestinationSendsNothingAnywhere()
    {
        var claude = new RecordingAdapter("claude", "Claude", ready: true);
        var codex = new RecordingAdapter("codex", "Codex", ready: false);
        var registry = new DestinationRegistry(new IDestinationAdapter[] { claude, codex });

        using var session = new PhoneSession(_endpoint, pipeline: null, destinations: registry);
        using TcpClient phone = await DialAsync();
        NetworkStream stream = phone.GetStream();
        await DrainAsync(stream, 2);

        await SendAsync(stream, "{\"t\":\"confirm\",\"destinationId\":\"codex\",\"text\":\"hello\"}");

        JsonElement result = await ReadUntilAsync(stream, "sendResult");
        Assert.False(result.GetProperty("ok").GetBoolean());

        Assert.Empty(codex.Sent);
        Assert.Empty(claude.Sent);
    }

    /// <summary>An unknown destination id is refused, not resolved to something close.</summary>
    [Fact]
    public async Task Confirm_ToAnUnknownDestinationIsRefused()
    {
        var claude = new RecordingAdapter("claude", "Claude", ready: true);
        var registry = new DestinationRegistry(new IDestinationAdapter[] { claude });

        using var session = new PhoneSession(_endpoint, pipeline: null, destinations: registry);
        using TcpClient phone = await DialAsync();
        NetworkStream stream = phone.GetStream();
        await DrainAsync(stream, 2);

        await SendAsync(stream, "{\"t\":\"confirm\",\"destinationId\":\"not-a-real-target\",\"text\":\"hello\"}");

        JsonElement result = await ReadUntilAsync(stream, "sendResult");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Empty(claude.Sent);
    }

    /// <summary>Sent is reported only when the adapter actually succeeded.</summary>
    [Fact]
    public async Task SendResult_ReportsFailureWhenTheAdapterFails()
    {
        var claude = new RecordingAdapter("claude", "Claude", ready: true) { Succeeds = false };
        var registry = new DestinationRegistry(new IDestinationAdapter[] { claude });

        using var session = new PhoneSession(_endpoint, pipeline: null, destinations: registry);
        using TcpClient phone = await DialAsync();
        NetworkStream stream = phone.GetStream();
        await DrainAsync(stream, 2);

        await SendAsync(stream, "{\"t\":\"confirm\",\"destinationId\":\"claude\",\"text\":\"hello\"}");

        JsonElement result = await ReadUntilAsync(stream, "sendResult");
        Assert.False(result.GetProperty("ok").GetBoolean());
    }

    /// <summary>The phone is told each destination's live readiness so it can show the truth.</summary>
    [Fact]
    public async Task DestinationsArePushedOnConnectWithReadiness()
    {
        var claude = new RecordingAdapter("claude", "Claude", ready: true);
        var codex = new RecordingAdapter("codex", "Codex", ready: false);
        var registry = new DestinationRegistry(new IDestinationAdapter[] { claude, codex });

        using var session = new PhoneSession(_endpoint, pipeline: null, destinations: registry);
        using TcpClient phone = await DialAsync();

        JsonElement message = await ReadUntilAsync(phone.GetStream(), "destinations");
        JsonElement list = message.GetProperty("destinations");

        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal("claude", list[0].GetProperty("id").GetString());
        Assert.True(list[0].GetProperty("ready").GetBoolean());
        Assert.Equal("codex", list[1].GetProperty("id").GetString());
        Assert.False(list[1].GetProperty("ready").GetBoolean());
    }

    /// <summary>A confirm missing its text sends nothing.</summary>
    [Fact]
    public async Task Confirm_WithoutTextSendsNothing()
    {
        var claude = new RecordingAdapter("claude", "Claude", ready: true);
        var registry = new DestinationRegistry(new IDestinationAdapter[] { claude });

        using var session = new PhoneSession(_endpoint, pipeline: null, destinations: registry);
        using TcpClient phone = await DialAsync();
        NetworkStream stream = phone.GetStream();
        await DrainAsync(stream, 2);

        await SendAsync(stream, "{\"t\":\"confirm\",\"destinationId\":\"claude\"}");
        await Task.Delay(400);

        Assert.Empty(claude.Sent);
    }

    private async Task<TcpClient> DialAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        await Task.Delay(200);
        return client;
    }

    private static async Task SendAsync(NetworkStream stream, string json)
    {
        byte[] frame = PhoneFraming.Encode(PhoneFrameKind.Json, Encoding.UTF8.GetBytes(json));
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
        await Task.Delay(60);
    }

    private static async Task DrainAsync(NetworkStream stream, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (int i = 0; i < count; i++)
        {
            await PhoneFraming.ReadAsync(stream, cts.Token);
        }
    }

    private static async Task<JsonElement> ReadUntilAsync(NetworkStream stream, string type)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        while (true)
        {
            (PhoneFrameKind Kind, byte[] Payload)? frame = await PhoneFraming.ReadAsync(stream, cts.Token);
            Assert.NotNull(frame);

            using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(frame!.Value.Payload));
            if (document.RootElement.GetProperty("t").GetString() == type)
            {
                return document.RootElement.Clone();
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Condition was not met in time.");
    }

    /// <summary>Records what it was asked to send, without touching a real window.</summary>
    private sealed class RecordingAdapter : IDestinationAdapter
    {
        private readonly bool _ready;

        public RecordingAdapter(string id, string name, bool ready)
        {
            DestinationId = id;
            DisplayName = name;
            ProcessName = id;
            _ready = ready;
        }

        public string DestinationId { get; }

        public string DisplayName { get; }

        public string ProcessName { get; }

        public WindowCandidate? BoundWindow => null;

        public bool Succeeds { get; init; } = true;

        public List<string> Sent { get; } = new();

        public DestinationStatus Probe() => new(
            _ready ? DestinationReadiness.Ready : DestinationReadiness.NotBound,
            Array.Empty<WindowCandidate>(),
            null,
            _ready ? "bound" : "not bound");

        public void Bind(WindowCandidate candidate)
        {
        }

        public void Unbind()
        {
        }

        public Task<SendResult> SendAsync(ConfirmedDraft draft, CancellationToken cancellationToken = default)
        {
            if (!Succeeds)
            {
                return Task.FromResult(new SendResult(SendStatus.FocusFailed, "focus failed", 1));
            }

            lock (Sent)
            {
                Sent.Add(draft.Text);
            }

            return Task.FromResult(new SendResult(SendStatus.Sent, "delivered", 1));
        }
    }
}
