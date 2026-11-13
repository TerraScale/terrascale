using System.Collections.Concurrent;

namespace TerraBase.Engine;

internal sealed class HomeLeaseGate(TimeProvider time)
{
    internal TimeProvider Time => time;

    private readonly ConcurrentDictionary<string, bool> _leased = new(StringComparer.Ordinal);
    private long _until;

    internal Func<bool>? RegionalWriter { get; set; }
    internal Func<long>? RegionalEpoch { get; set; }

    internal long Epoch => RegionalEpoch?.Invoke() ?? 0;

    internal void ThrowIfRegionalWriterChanged(long epoch)
    {
        if (!(RegionalWriter?.Invoke() ?? true) || Epoch != epoch)
        {
            throw new StorageBusyException();
        }
    }

    public IReadOnlyCollection<string> Leased => [.. _leased.Keys];

    public void Lease(string database, bool held) => _leased[database] = held;

    public void Unlease(string database) => _leased.TryRemove(database, out _);

    public void Renewed(long until) => Volatile.Write(ref _until, until);

    public bool Holds(string database) =>
        (RegionalWriter?.Invoke() ?? true)
        && (!_leased.TryGetValue(database, out var held) || (held && time.GetTimestamp() < Volatile.Read(ref _until)));

    public void ThrowIfUnleased(string database)
    {
        if (!Holds(database))
        {
            throw new StorageBusyException();
        }
    }
}
