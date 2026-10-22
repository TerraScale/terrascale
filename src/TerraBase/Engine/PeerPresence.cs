using TerraBase.Engine.Lattices;
using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal sealed class PeerPresence
{
    private readonly Lock _gate = new();
    private Dictionary<string, (string Region, ReplicaId? Node)>? _nodes;
    private long _changed;

    public void Heard(string machine, ReplicaId node, long nowMilliseconds)
    {
        lock (_gate)
        {
            if (_nodes?.TryGetValue(machine, out var known) == true && known.Node != node)
            {
                _changed = nowMilliseconds;
                _nodes[machine] = (known.Region, node);
            }
        }
    }

    public void Listed(IEnumerable<(string Name, string Region)> machines, long nowMilliseconds)
    {
        lock (_gate)
        {
            var current = machines.ToDictionary(machine => machine.Name, machine => machine.Region, StringComparer.Ordinal);
            if (_nodes is null || current.Count != _nodes.Count
                || current.Any(machine => !_nodes.TryGetValue(machine.Key, out var known) || known.Region != machine.Value))
            {
                // One conservative grace period avoids retaining an unbounded history of departed machines.
                _changed = nowMilliseconds;
                _nodes = current.ToDictionary(machine => machine.Key,
                    machine => (machine.Value, _nodes?.GetValueOrDefault(machine.Key).Node), StringComparer.Ordinal);
            }
        }
    }

    public bool HoldsMissingProgress(DatabasePlacement? placement, IReadOnlySet<ReplicaId> progressed)
    {
        lock (_gate)
        {
            return _nodes is null || _nodes.Values.Any(known => (placement is null || placement.Includes(known.Region))
                && (known.Node is not { } node || !progressed.Contains(node)));
        }
    }

    public bool Absent(ReplicaId node, long sinceMilliseconds)
    {
        lock (_gate)
        {
            return _nodes is not null && _changed < sinceMilliseconds
                && _nodes.Values.All(known => known.Node is not null && known.Node != node);
        }
    }
}
