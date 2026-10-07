using TerraBase.Engine.Cluster;

namespace TerraBase.Testing;

/// <summary>
/// The member store with NATS KV semantics, in memory, shared by the members of a test: a member's key expires
/// <see cref="Ttl"/> after its last write by <see cref="TimeProvider"/>, and every write raises the revision.
/// <see cref="Partition"/> cuts a member off, as a lost NATS connection would.
/// </summary>
internal sealed class FakeMemberStore(TimeProvider time) : IMemberStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (ClusterMember Member, ulong Revision, DateTimeOffset Written)> _members = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _partitioned = [with(StringComparer.Ordinal)];
    private ulong _revision;

    public TimeSpan Ttl { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Every write of the member named <paramref name="node"/> fails from now on.</summary>
    public void Partition(string node)
    {
        lock (_gate)
        {
            _partitioned.Add(node);
        }
    }

    public Task PutAsync(ClusterMember member, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_partitioned.Contains(member.Name))
            {
                throw new IOException("the member is cut off from the store");
            }

            _members[member.Name] = (member, ++_revision, time.GetUtcNow());
            return Task.CompletedTask;
        }
    }

    public Task RemoveAsync(string name, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _ = _members.Remove(name);
            return Task.CompletedTask;
        }
    }

    public Task<ClusterView> ListAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var live = _members.Values.Where(entry => now - entry.Written < Ttl).ToList();
            return Task.FromResult(new ClusterView(
                live.Count == 0 ? 0 : live.Max(entry => entry.Revision),
                [.. live.Select(entry => entry.Member).OrderBy(member => member.Name, StringComparer.Ordinal)]));
        }
    }
}
