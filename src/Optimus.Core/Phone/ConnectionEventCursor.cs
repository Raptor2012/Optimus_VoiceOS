namespace Optimus.Core.Phone;

using System.Threading;

/// <summary>Monotonic cursor used to discard callbacks from a socket replaced by a reconnect.</summary>
public sealed class ConnectionEventCursor
{
    private long _value;

    public long Advance() => Interlocked.Increment(ref _value);
    public long Current => Volatile.Read(ref _value);
    public bool IsCurrent(long cursor) => cursor == Current;
}
