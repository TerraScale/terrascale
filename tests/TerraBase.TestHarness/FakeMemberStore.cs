using System.Collections.Immutable;
using TerraBase.Engine.Cluster;

namespace TerraBase.Testing;

/// <summary>
/// The member directory for the machines of a test, in memory: it lists every started machine of every region. A server that
/// registers it as its <see cref="IMemberDirectory"/> is listed once it listens and dropped once it stops (the harness does
/// both). <see cref="Partition"/> makes a member's own lookups fail, as a machine unable to read its peer inventory.
/// </summary>
internal sealed class FakeMemberStore : IMemberDirectory
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ClusterMember> _members = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _partitioned = [with(StringComparer.Ordinal)];

    /// <summary>Lists <paramref name="member"/> from now on.</summary>
    public void Join(ClusterMember member)
    {
        lock (_gate)
        {
            _members[member.Name] = member;
        }
    }

    /// <summary>Stops listing the member named <paramref name="name"/>.</summary>
    public void Leave(string name)
    {
        lock (_gate)
        {
            _ = _members.Remove(name);
        }
    }

    /// <summary>Every lookup of the member named <paramref name="node"/> fails from now on.</summary>
    public void Partition(string node)
    {
        lock (_gate)
        {
            _partitioned.Add(node);
        }
    }

    /// <summary>
    /// Rewrites each other member as the member named in the first argument sees it: an e2e harness gives every machine
    /// its own route to each peer (a proxy it can cut), so the machines' views of the directory differ.
    /// </summary>
    public Func<string, ClusterMember, ClusterMember>? Map { get; set; }

    /// <summary>The directory as the member named <paramref name="node"/> reads it.</summary>
    public IMemberDirectory For(string node) => new Reader(this, node);

    public Task<ImmutableArray<ClusterMember>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<ImmutableArray<ClusterMember>>([.. _members.Values]);
        }
    }

    private sealed class Reader(FakeMemberStore store, string node) : IMemberDirectory
    {
        public Task<ImmutableArray<ClusterMember>> ListAsync(CancellationToken cancellationToken)
        {
            bool partitioned;
            lock (store._gate)
            {
                partitioned = store._partitioned.Contains(node);
            }

            if (partitioned)
            {
                return Task.FromException<ImmutableArray<ClusterMember>>(new IOException("the member is cut off from the DNS"));
            }

            ImmutableArray<ClusterMember> members;
            lock (store._gate)
            {
                members = [.. store._members.Values];
            }

            return Task.FromResult(store.Map is { } map ? [.. members.Select(member => member.Name == node ? member : map(node, member))] : members);
        }
    }
}
