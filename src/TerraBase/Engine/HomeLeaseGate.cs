using System.Collections.Concurrent;

namespace TerraBase.Engine;

internal sealed class HomeLeaseGate(TimeProvider time)
{
    internal TimeProvider Time => time;

    private readonly ConcurrentDictionary<string, bool> _leased = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _fenced = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _retired = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _proven = new(StringComparer.Ordinal);
    private long _until;

    internal Func<bool>? RegionalWriter { get; set; }

    internal Func<string, bool>? RegionalServing { get; set; }

    internal Func<string, bool>? RegionalSource { get; set; }

    public IReadOnlyCollection<string> Leased => [.. _leased.Keys];

    public void Lease(string database, bool held) => _leased[database] = held;

    public void Unlease(string database) => _leased.TryRemove(database, out _);

    public void Fence(string database) => _fenced[database] = true;

    public void Unfence(string database) => _fenced.TryRemove(database, out _);

    public void Retire(string database)
    {
        _retired[database] = true;
        _fenced.TryRemove(database, out _);
    }

    public void BeginActivation()
    {
        _retired.Clear();
        _proven.Clear();
    }

    public void Prove(string database) => _proven[database] = true;

    public bool Proven(string database) => _proven.ContainsKey(database);

    public bool Fenced(string database) => _fenced.ContainsKey(database) || _retired.ContainsKey(database);

    public bool Serves(string database) => !Fenced(database) && (RegionalServing?.Invoke(database) ?? true);

    public bool Sources(string database) => !Fenced(database) && (RegionalSource?.Invoke(database) ?? true) && !Fenced(database);

    public void Renewed(long until) => Volatile.Write(ref _until, until);

    public bool Holds(string database) =>
        (RegionalWriter?.Invoke() ?? true)
        && (!_leased.TryGetValue(database, out var held) || (held && time.GetTimestamp() < Volatile.Read(ref _until)));

    public bool Writes(string database) => Holds(database) && !Fenced(database) && (RegionalServing?.Invoke(database) ?? true);

    public void ThrowIfFenced(string database)
    {
        if (Fenced(database))
        {
            throw new DatabaseFencedException();
        }
    }

    public void ThrowIfUnserved(string database)
    {
        if (!Serves(database))
        {
            throw new DatabaseFencedException();
        }
    }

    public void ThrowIfUnleased(string database)
    {
        var held = Holds(database);
        ThrowIfFenced(database);
        if (!held || !(RegionalServing?.Invoke(database) ?? true))
        {
            throw new StorageBusyException();
        }
        Prove(database);
    }
}
