using System.Collections.Concurrent;

namespace TerraBase.Engine;

internal sealed class HomeLeaseGate
{
    private readonly ConcurrentDictionary<string, bool> _leased = new(StringComparer.Ordinal);
    private long _until;

    internal Func<bool>? RegionalWriter { get; set; }

    public IReadOnlyCollection<string> Leased => [.. _leased.Keys];

    public void Lease(string database, bool held) => _leased[database] = held;

    public void Unlease(string database) => _leased.TryRemove(database, out _);

    public void Renewed(long until) => Volatile.Write(ref _until, until);

    public bool Holds(string database) =>
        (RegionalWriter?.Invoke() ?? true)
        && (!_leased.TryGetValue(database, out var held) || (held && TimeProvider.System.GetTimestamp() < Volatile.Read(ref _until)));

    public void ThrowIfUnleased(string database)
    {
        if (!Holds(database))
        {
            throw new StorageBusyException();
        }
    }
}
