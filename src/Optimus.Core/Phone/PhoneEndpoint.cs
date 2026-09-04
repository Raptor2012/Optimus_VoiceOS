namespace Optimus.Core.Phone;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public sealed class PhoneConnectionEventArgs : EventArgs
{
    public PhoneConnectionEventArgs(bool connected, string remote)
    {
        Connected = connected;
        Remote = remote;
    }

    public bool Connected { get; }

    public string Remote { get; }
}

/// <summary>The phone asked to send the exact text it was showing.</summary>
public sealed class PhoneConfirmEventArgs : EventArgs
{
    public PhoneConfirmEventArgs(string destinationId, string text)
    {
        DestinationId = destinationId;
        Text = text;
    }

    public string DestinationId { get; }

    /// <summary>
    /// The draft as displayed on the phone at the moment the user confirmed.
    /// </summary>
    /// <remarks>
    /// The PC sends this text and nothing else. It deliberately does not reuse its own copy of
    /// the draft: the phone may have edited it, and the product rule is that what was visible is
    /// what gets sent.
    /// </remarks>
    public string Text { get; }
}

public sealed class PhoneAudioEventArgs : EventArgs
{
    public PhoneAudioEventArgs(byte[] pcm) => Pcm = pcm;

    /// <summary>16 kHz mono PCM16, the same format the desktop capture produces.</summary>
    public byte[] Pcm { get; }
}

public sealed class PhonePlaybackDrainedEventArgs : EventArgs
{
    public PhonePlaybackDrainedEventArgs(long generation) => Generation = generation;
    public long Generation { get; }
}

public sealed class PhoneNarrationSettingsEventArgs(string mode, bool narrateToolsAndSkills) : EventArgs
{
    public string Mode { get; } = mode;
    public bool NarrateToolsAndSkills { get; } = narrateToolsAndSkills;
}

/// <summary>
/// One direct TCP endpoint the phone connects to over LAN or Tailscale.
/// </summary>
/// <remarks>
/// Deliberately small: one client at a time, all state in memory, and a fresh connection after
/// any drop. There is no pairing, no authentication and no transport security in this slice, so
/// it must only ever be reachable on a private network — it binds a configured address and is
/// never intended to be port-forwarded.
/// </remarks>
public sealed class PhoneEndpoint : IDisposable
{
    public const int DefaultPort = 8770;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _lock = new();
    private readonly IPAddress _bindAddress;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public PhoneEndpoint(int port = DefaultPort, IPAddress? bindAddress = null)
    {
        Port = port;
        _bindAddress = bindAddress ?? IPAddress.Any;
    }

    public int Port { get; }

    public bool IsListening { get; private set; }

    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _client is { Connected: true };
            }
        }
    }

    public event EventHandler<PhoneConnectionEventArgs>? ConnectionChanged;

    /// <summary>The phone pressed push-to-talk.</summary>
    public event EventHandler? CaptureStarted;

    /// <summary>The phone released push-to-talk; the utterance is complete.</summary>
    public event EventHandler? CaptureStopped;

    public event EventHandler<PhoneAudioEventArgs>? AudioReceived;

    /// <summary>The phone wants the current destination list.</summary>
    public event EventHandler? DestinationsRequested;

    /// <summary>The phone confirmed a draft for sending.</summary>
    public event EventHandler<PhoneConfirmEventArgs>? ConfirmRequested;

    /// <summary>The phone discarded the draft.</summary>
    public event EventHandler? CancelRequested;

    /// <summary>The phone has played every PCM frame for this generation.</summary>
    public event EventHandler<PhonePlaybackDrainedEventArgs>? PlaybackDrained;

    public event EventHandler<PhoneNarrationSettingsEventArgs>? NarrationSettingsChanged;

    /// <summary>The phone selected a destination.</summary>
    public event EventHandler<string>? DestinationSelected;

    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (IsListening)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _listener = new TcpListener(_bindAddress, Port);
            _listener.Start();
            IsListening = true;
        }

        _ = Task.Run(() => AcceptLoopAsync(_cts!.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // One phone at a time. A new connection replaces the old one, which is what makes
            // reconnect work without any resume handshake: the phone just dials again.
            CloseCurrentClient();

            string remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

            lock (_lock)
            {
                _client = client;
                _stream = client.GetStream();
            }

            client.NoDelay = true;
            ConnectionChanged?.Invoke(this, new PhoneConnectionEventArgs(true, remote));

            await ReceiveLoopAsync(remote, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(string remote, CancellationToken cancellationToken)
    {
        NetworkStream? stream;
        lock (_lock)
        {
            stream = _stream;
        }

        if (stream == null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (PhoneFrameKind Kind, byte[] Payload)? frame =
                    await PhoneFraming.ReadAsync(stream, cancellationToken).ConfigureAwait(false);

                if (frame == null)
                {
                    break; // Phone closed.
                }

                if (frame.Value.Kind == PhoneFrameKind.Audio)
                {
                    AudioReceived?.Invoke(this, new PhoneAudioEventArgs(frame.Value.Payload));
                    continue;
                }

                HandleJson(Encoding.UTF8.GetString(frame.Value.Payload));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // Phone vanished; treat as a normal disconnect and wait for the next dial.
        }
        catch (InvalidDataException)
        {
            // Malformed frame: drop the connection rather than guess at the stream position.
        }
        catch (SocketException)
        {
        }
        finally
        {
            CloseCurrentClient();
            ConnectionChanged?.Invoke(this, new PhoneConnectionEventArgs(false, remote));
        }
    }

    private void HandleJson(string json)
    {
        string? type;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            type = document.RootElement.TryGetProperty("t", out JsonElement t) ? t.GetString() : null;
        }
        catch (JsonException)
        {
            return; // Ignore anything unparseable; there is nothing to negotiate.
        }

        switch (type)
        {
            case "startCapture":
                CaptureStarted?.Invoke(this, EventArgs.Empty);
                break;

            case "stopCapture":
                CaptureStopped?.Invoke(this, EventArgs.Empty);
                break;

            case "hello":
                // Nothing to do; the connection itself is the state.
                break;

            case "refreshDestinations":
                DestinationsRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "confirm":
                RaiseConfirm(json);
                break;

            case "cancel":
                CancelRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "playbackDrained":
                RaisePlaybackDrained(json);
                break;

            case "narrationSettings":
                RaiseNarrationSettings(json);
                break;

            case "selectDestination":
                RaiseSelectDestination(json);
                break;

            default:
                break;
        }
    }

    private void RaisePlaybackDrained(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("generation", out JsonElement value) &&
                value.TryGetInt64(out long generation) && generation >= 0)
            {
                PlaybackDrained?.Invoke(this, new PhonePlaybackDrainedEventArgs(generation));
            }
        }
        catch (JsonException)
        {
        }
    }

    private void RaiseNarrationSettings(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string mode = root.TryGetProperty("mode", out JsonElement modeValue)
                ? modeValue.GetString() ?? "concise"
                : "concise";
            bool tools = root.TryGetProperty("narrateToolsAndSkills", out JsonElement toolsValue) &&
                         toolsValue.ValueKind == JsonValueKind.True;
            NarrationSettingsChanged?.Invoke(this, new PhoneNarrationSettingsEventArgs(mode, tools));
        }
        catch (JsonException) { }
    }

    private void RaiseSelectDestination(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("destinationId", out JsonElement val))
            {
                string? dest = val.GetString();
                if (!string.IsNullOrWhiteSpace(dest))
                {
                    DestinationSelected?.Invoke(this, dest);
                }
            }
        }
        catch (JsonException) { }
    }

    /// <summary>
    /// Parses a confirm message. A missing destination or empty text is dropped rather than
    /// guessed at, because both are required to send anything.
    /// </summary>
    private void RaiseConfirm(string json)
    {
        string? destinationId;
        string? text;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            destinationId = document.RootElement.TryGetProperty("destinationId", out JsonElement d) ? d.GetString() : null;
            text = document.RootElement.TryGetProperty("text", out JsonElement t) ? t.GetString() : null;
        }
        catch (JsonException)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(destinationId) || string.IsNullOrWhiteSpace(text))
        {
            SendError("Confirm needs both a destination and text.");
            return;
        }

        ConfirmRequested?.Invoke(this, new PhoneConfirmEventArgs(destinationId, text));
    }

    /// <summary>Pushes the destination list and each one's readiness.</summary>
    public void SendDestinations(IReadOnlyList<PhoneDestination> destinations) =>
        SendJson(new { t = "destinations", destinations });

    /// <summary>Reports the outcome of a send. This is the phone's final summary.</summary>
    public void SendSendResult(bool ok, string destinationName, string detail) =>
        SendJson(new { t = "sendResult", ok, destination = destinationName, detail });

    /// <summary>Pushes a status line to the phone. Silently no-ops when nothing is connected.</summary>
    public void SendStatus(string state, string line) =>
        SendJson(new { t = "status", state, line });

    /// <summary>Pushes the finished transcript and cleaned draft, and optionally the chosen destination.</summary>
    public void SendDraft(string raw, string clean, string timings, string? destinationId = null) =>
        SendJson(destinationId != null
            ? new { t = "draft", raw, clean, timings, destinationId }
            : new { t = "draft", raw, clean, timings });

    public void SendSelectedDestination(string destinationId) =>
        SendJson(new { t = "destinationSelected", destinationId });

    public void SendStartApprovalCapture() =>
        SendJson(new { t = "startApprovalCapture" });

    public void SendStopApprovalCapture() =>
        SendJson(new { t = "stopApprovalCapture" });

    public void SendStartRedictationCapture() =>
        SendJson(new { t = "startRedictationCapture" });

    public void SendError(string message) =>
        SendJson(new { t = "error", message });

    public void SendPlaybackStart(long generation) =>
        SendJson(new { t = "playback", generation, state = "start" });

    public void SendPlaybackEnd(long generation) =>
        SendJson(new { t = "playback", generation, state = "end" });

    public void CancelPlayback(long generation) =>
        SendJson(new { t = "playback", generation, state = "cancel" });

    public void SendPlaybackChime(long generation) =>
        SendJson(new { t = "chime", generation });

    public void SendTtsAudio(PhoneTtsAudioSegment segment) =>
        Send(PhoneFrameKind.TtsAudio, PhoneTtsAudio.Encode(segment));

    private void SendJson(object payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        Send(PhoneFrameKind.Json, json);
    }

    private void Send(PhoneFrameKind kind, byte[] payload)
    {
        NetworkStream? stream;
        lock (_lock)
        {
            stream = _stream;
        }

        if (stream == null)
        {
            return;
        }

        try
        {
            byte[] frame = PhoneFraming.Encode(kind, payload);
            lock (_lock)
            {
                stream.Write(frame, 0, frame.Length);
            }
        }
        catch (IOException)
        {
            // The receive loop will notice and report the disconnect.
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private void CloseCurrentClient()
    {
        TcpClient? client;
        lock (_lock)
        {
            client = _client;
            _client = null;
            _stream = null;
        }

        try
        {
            client?.Close();
            client?.Dispose();
        }
        catch (SocketException)
        {
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!IsListening)
            {
                return;
            }

            IsListening = false;
        }

        _cts?.Cancel();
        CloseCurrentClient();

        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
        }

        _listener = null;
    }

    /// <summary>Every address the phone could dial, for display in the widget.</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        var addresses = new List<string>();

        foreach (System.Net.NetworkInformation.NetworkInterface nic in
                 System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
            {
                continue;
            }

            foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ip in
                     nic.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ip.Address))
                {
                    addresses.Add(ip.Address.ToString());
                }
            }
        }

        return addresses;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _cts?.Dispose();
        _cts = null;
    }
}
