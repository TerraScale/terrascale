using TerraBase.Engine.Lattices;

namespace TerraBase.Engine;

/// <summary>
/// Which machines this process has seen in the machine directory, and which replica id each one streams under (from its
/// stream's hello or welcome): what garbage collection needs to tell a peer that left the directory from one that is only
/// cut off (docs/terrabase/multi-region-replication.md section 11). A running machine is listed even while no stream
/// reaches it, so it is never evicted however long a partition lasts.
/// </summary>
internal sealed class PeerPresence
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ReplicaId> _nodes = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, long> _listed = [with(StringComparer.Ordinal)];
    private HashSet<string>? _current;

    /// <summary>Records that the machine named <paramref name="machine"/> streams as <paramref name="node"/>.</summary>
    public void Heard(string machine, ReplicaId node)
    {
        lock (_gate)
        {
            _nodes[machine] = node;
        }
    }

    /// <summary>Records the other machines the directory lists at <paramref name="nowMilliseconds"/> (Unix ms, the member's clock).</summary>
    public void Listed(IEnumerable<string> machines, long nowMilliseconds)
    {
        lock (_gate)
        {
            _current = [.. machines];
            foreach (var machine in _current)
            {
                _listed[machine] = nowMilliseconds;
            }
        }
    }

    /// <summary>Whether the latest listing holds the machine named <paramref name="machine"/>.</summary>
    public bool Lists(string machine)
    {
        lock (_gate)
        {
            return _current?.Contains(machine) == true;
        }
    }

    /// <summary>
    /// Whether no machine that could stream as <paramref name="node"/> is listed now or was listed at or after
    /// <paramref name="sinceMilliseconds"/>. A machine whose replica id is not known yet could be any; before the first
    /// listing nothing is absent.
    /// </summary>
    public bool Absent(ReplicaId node, long sinceMilliseconds)
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return false;
            }

            bool Could(string machine)
            {
                return !_nodes.TryGetValue(machine, out var known) || known == node;
            }

            return !_current.Any(Could) && !_listed.Any(listed => listed.Value >= sinceMilliseconds && Could(listed.Key));
        }
    }
}
