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

    public Dictionary<ReplicaId, bool> TrackProgress(DatabasePlacement? placement)
    {
        lock (_gate)
        {
            Dictionary<ReplicaId, bool> progress = [];
            if (_nodes is not null)
            {
                foreach (var (region, identity) in _nodes.Values)
                {
                    if ((placement is null || placement.Includes(region)) && identity is { } node)
                    {
                        _ = progress.TryAdd(node, false);
                    }
                }
            }

            return progress;
        }
    }

    public bool HoldsMissingProgress(DatabasePlacement? placement, IReadOnlyDictionary<ReplicaId, bool> progress)
    {
        lock (_gate)
        {
            return _nodes is null || _nodes.Values.Any(known => (placement is null || placement.Includes(known.Region))
                && (known.Node is not { } node || !progress.GetValueOrDefault(node)));
        }
    }

    /// <summary>
    /// Every listed machine's current identity, once this process has heard each one; null before that, or while nothing is
    /// listed. A peer identity outside it belongs to no listed machine any more: a replaced data directory or a departed machine.
    /// </summary>
    public IReadOnlyList<ReplicaId>? Current()
    {
        lock (_gate)
        {
            return _nodes is { Count: > 0 } && _nodes.Values.All(known => known.Node is not null)
                ? _nodes.Values.Select(known => known.Node!.Value).ToList()
                : null;
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
