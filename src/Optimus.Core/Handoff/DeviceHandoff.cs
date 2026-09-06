namespace Optimus.Core.Handoff;

using System.Text.RegularExpressions;
using System.Net.NetworkInformation;
using Optimus.Core.Memory;

public sealed record HandoffSnapshot(
    string SourceDevice,
    string TargetDevice,
    IReadOnlyList<ConversationTurnRecord> RecentTurns,
    string? ActiveObjective,
    DateTimeOffset CreatedAtUtc);

public interface IDevicePresence
{
    Task<bool> IsPresentAsync(string device, CancellationToken cancellationToken = default);
}

/// <summary>Optional LAN presence probe for a manually configured Pixel/PC address.</summary>
public sealed class NetworkDevicePresence : IDevicePresence
{
    private readonly IReadOnlyDictionary<string, string> _addresses;

    public NetworkDevicePresence(IReadOnlyDictionary<string, string> addresses)
    {
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
    }

    public async Task<bool> IsPresentAsync(string device, CancellationToken cancellationToken = default)
    {
        if (!_addresses.TryGetValue(DeviceHandoff.NormalizeDevice(device), out string? address) || string.IsNullOrWhiteSpace(address)) return false;
        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync(address.Trim(), 500).WaitAsync(cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (PingException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

/// <summary>
/// Keeps PC and Pixel continuity in the local conversation_history database. The MVP uses a
/// shared MemoryStore; a later transport can replicate the same snapshot without changing callers.
/// </summary>
public sealed class DeviceHandoff
{
    private static readonly Regex ContinuePattern = new(
        @"\bcontinue\s+(?:this\s+)?(?:on|from)\s+(?:my\s+)?(?<device>phone|pixel|pc|computer|desktop)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly MemoryStore _memory;
    private readonly IDevicePresence? _presence;

    public DeviceHandoff(MemoryStore memoryStore, IDevicePresence? presence = null)
    {
        _memory = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _presence = presence;
    }

    public HandoffSnapshot SyncRecentTurns(string sourceDevice, string targetDevice, int count = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDevice);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDevice);
        IReadOnlyList<ConversationTurnRecord> turns = _memory.GetRecentTurns(Math.Max(1, count));
        string? objective = turns.Select(turn => turn.Objective)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return new(sourceDevice.Trim(), targetDevice.Trim(), turns, objective, DateTimeOffset.UtcNow);
    }

    public Task<HandoffSnapshot> SyncAsync(
        string sourceDevice,
        string targetDevice,
        int count = 20,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SyncRecentTurns(sourceDevice, targetDevice, count));
    }

    public static bool IsExplicitRequest(string transcript, out string? targetDevice)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        Match match = ContinuePattern.Match(transcript);
        targetDevice = match.Success ? NormalizeDevice(match.Groups["device"].Value) : null;
        return targetDevice != null;
    }

    public async Task<bool> ShouldHandoffAsync(
        string currentDevice,
        string otherDevice,
        string? transcript = null,
        CancellationToken cancellationToken = default)
    {
        if (transcript != null && IsExplicitRequest(transcript, out string? requested) &&
            string.Equals(requested, NormalizeDevice(otherDevice), StringComparison.OrdinalIgnoreCase))
            return true;

        return _presence != null && await _presence.IsPresentAsync(otherDevice, cancellationToken).ConfigureAwait(false);
    }

    public static string NormalizeDevice(string device) => device.Trim().ToLowerInvariant() switch
    {
        "phone" or "pixel" => "pixel",
        "pc" or "computer" or "desktop" => "pc",
        _ => device.Trim().ToLowerInvariant()
    };
}
