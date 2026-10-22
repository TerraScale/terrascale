using TerraBase.Engine.Lattices;

namespace TerraBase.Engine;

internal sealed class PeerPresence
{
    private readonly Lock _gate = new();
    private Dictionary<string, ReplicaId?>? _nodes;
    private long _changed;

    public void Heard(string machine, ReplicaId node)
    {
        lock (_gate)
        {
            if (_nodes?.ContainsKey(machine) == true)
            {
                _nodes[machine] = node;
            }
        }
    }

    public void Listed(IEnumerable<string> machines, long nowMilliseconds)
    {
        lock (_gate)
        {
            var current = machines.ToHashSet(StringComparer.Ordinal);
            if (_nodes is null || !current.SetEquals(_nodes.Keys))
            {
                // One conservative grace period avoids retaining an unbounded history of departed machines.
                _changed = nowMilliseconds;
                _nodes = current.ToDictionary(machine => machine,
                    machine => _nodes?.GetValueOrDefault(machine), StringComparer.Ordinal);
            }
        }
    }

    public bool HoldsUnheard()
    {
        lock (_gate)
        {
            return _nodes is null || _nodes.Values.Any(known => known is null);
        }
    }

    public bool Absent(ReplicaId node, long sinceMilliseconds)
    {
        lock (_gate)
        {
            return _nodes is not null && _changed < sinceMilliseconds
                && _nodes.Values.All(known => known is not null && known != node);
        }
    }
}
