using System.Collections.Immutable;

namespace TerraBase.Engine.Cluster;

/// <summary>A member of the cell: its machine, its internal listener and where it runs.</summary>
/// <param name="Name">The member's machine ID (<c>FLY_MACHINE_ID</c>).</param>
/// <param name="PeerAddress">Its internal listener (<c>http://host:port</c>), or empty when it has none.</param>
/// <param name="Region">Its region.</param>
/// <param name="Zone">Its zone within the region, or empty.</param>
internal sealed record ClusterMember(string Name, string PeerAddress, string Region, string Zone);

/// <summary>The members of this member's region it sees, in name order, and the membership revision it read them at.</summary>
/// <param name="Revision">Grows with every membership change this member saw; constant for a static cell.</param>
/// <param name="Members">The live members of this region, in name order.</param>
/// <param name="Elsewhere">The live members of the other regions, in name order.</param>
internal sealed record ClusterView(ulong Revision, ImmutableArray<ClusterMember> Members, ImmutableArray<ClusterMember> Elsewhere = default)
{
    /// <summary>Whether <paramref name="name"/> is a live member.</summary>
    public bool Contains(string name) => Members.Any(member => member.Name == name);
}

/// <summary>
/// The members of the cell and this member's lease (docs/design/anna-core.md §5): from Fly's internal DNS on Fly, or this
/// member alone (and the topologies tests register) elsewhere.
/// </summary>
internal interface IMembership
{
    /// <summary>This member.</summary>
    ClusterMember Self { get; }

    /// <summary>Whether members come and go at runtime (Fly); a static cell never changes.</summary>
    bool Dynamic { get; }

    /// <summary>
    /// Whether this member holds its lease now: it read the members recently enough, and saw itself among them, that no
    /// other member can have seen it gone. A member without its lease serves no owner operation and ships no backup.
    /// </summary>
    bool HoldsLease { get; }

    /// <summary>How long after its last successful read this member stops acting on its lease; zero for a static cell.</summary>
    TimeSpan Fence { get; }

    /// <summary>Reads the members again when due, then returns the members it sees.</summary>
    ValueTask<ClusterView> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ends this member's lease, so it stops acting on it before its machine leaves the others' view. Called at shutdown,
    /// after the work the lease guards stopped; it never reads the members again.
    /// </summary>
    ValueTask LeaveAsync(CancellationToken cancellationToken);
}

/// <summary>Where members are listed: every started machine of the app, in every region.</summary>
internal interface IMemberDirectory
{
    /// <summary>The started machines of every region.</summary>
    Task<ImmutableArray<ClusterMember>> ListAsync(CancellationToken cancellationToken);
}
