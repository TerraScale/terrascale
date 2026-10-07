using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace TerraBase.Engine.Cluster;

/// <summary>A member of the cell: its node name, its peer listener and where it runs.</summary>
/// <param name="Name">The member's <c>node-name</c>.</param>
/// <param name="PeerAddress">Its peer listener (<c>https://host:port</c>), or empty when it has none.</param>
/// <param name="Region">Its region.</param>
/// <param name="Zone">Its zone within the region, or empty.</param>
internal sealed record ClusterMember(string Name, string PeerAddress, string Region, string Zone);

/// <summary>The members one member sees, in name order, and the membership revision it read them at.</summary>
/// <param name="Revision">Grows with every membership change this member saw; constant for a static cell.</param>
/// <param name="Members">The live members, in name order.</param>
internal sealed record ClusterView(ulong Revision, ImmutableArray<ClusterMember> Members)
{
    /// <summary>Whether <paramref name="name"/> is a live member.</summary>
    public bool Contains(string name) => Members.Any(member => member.Name == name);
}

/// <summary>
/// The members of the cell and this member's lease (docs/design/anna-core.md §5): from NATS (ts-orchestrator's JetStream
/// KV) in a cluster, or the static <c>peers</c> list for tests and single-node runs.
/// </summary>
internal interface IMembership
{
    /// <summary>This member.</summary>
    ClusterMember Self { get; }

    /// <summary>Whether members come and go at runtime (NATS); a static cell never changes.</summary>
    bool Dynamic { get; }

    /// <summary>
    /// Whether this member holds its lease now: it renewed it recently enough that no other member can have seen it expire.
    /// A member without its lease serves no owner operation and ships no backup.
    /// </summary>
    bool HoldsLease { get; }

    /// <summary>How long after its last renewal this member stops acting on its lease; zero for a static cell.</summary>
    TimeSpan Fence { get; }

    /// <summary>Renews this member's lease and reads the members again when due, then returns the members it sees.</summary>
    ValueTask<ClusterView> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ends this member's lease and removes it from the view, so the others take its work over at once instead of
    /// waiting for its key to expire. Called at shutdown, after the work the lease guards stopped; it never renews again.
    /// </summary>
    ValueTask LeaveAsync(CancellationToken cancellationToken);
}

/// <summary>Where members publish themselves: keys that expire <see cref="Ttl"/> after their last write.</summary>
internal interface IMemberStore
{
    /// <summary>How long a member stays after its last write.</summary>
    TimeSpan Ttl { get; }

    /// <summary>Writes (renews) <paramref name="member"/>.</summary>
    Task PutAsync(ClusterMember member, CancellationToken cancellationToken);

    /// <summary>The live members and the store's revision.</summary>
    Task<ClusterView> ListAsync(CancellationToken cancellationToken);

    /// <summary>Removes the member named <paramref name="name"/>.</summary>
    Task RemoveAsync(string name, CancellationToken cancellationToken);
}

[JsonSerializable(typeof(ClusterMember))]
internal sealed partial class ClusterJsonContext : JsonSerializerContext;
