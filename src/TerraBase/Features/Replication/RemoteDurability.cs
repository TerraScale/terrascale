using System.Collections.Frozen;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>
/// Whether a client write is confirmed beyond this machine's region, and by which regions (decision 0013). A machine of a
/// cell with a member directory requires a durable acknowledgement from another placed region before confirming a write,
/// even when the directory currently lists only this node; without an eligible remote region, writes fail closed.
/// A node without a member directory (local development or single-machine tests) answers after its local durable commit.
/// </summary>
public interface IRemoteDurability
{
    /// <summary>Whether this machine's writes need a durable copy in another region: it belongs to a cell.</summary>
    bool Confirms { get; }

    /// <summary>The serving regions other than this machine's, ordered: a new database's default replica regions.</summary>
    ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken);

    /// <summary>The current database placement, or null when it cannot be established.</summary>
    ValueTask<DatabasePlacement?> PlacementAsync(TenantContext database, CancellationToken cancellationToken) =>
        ValueTask.FromResult<DatabasePlacement?>(null);

    /// <summary>
    /// Admits one write attempt of <paramref name="database"/> before it executes (decision 0013a section 2): refused with
    /// <c>remote_region_unavailable</c> while no machine of another placed region has a connected stream, with
    /// <c>confirmation_busy</c> past the waiter bounds, and with the conflict <c>placement_needs_two_regions</c> while the
    /// database is placed in its home region alone (no retry can succeed until a placement update adds a region). Dispose the lease once the attempt is answered.
    /// </summary>
    ValueTask<RemoteAdmission> AdmitAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// After the attempt committed and released its writer: whether an eligible ack of another placed region covers
    /// <paramref name="marker"/> within 10 s. False once the bound passes or no eligible link remains: outcome unknown.
    /// </summary>
    ValueTask<bool> WaitAsync(TenantContext database, CommitMarker marker, CancellationToken cancellationToken);

    /// <summary>
    /// The exact replica ids trusted as origins held in another placed region of <paramref name="database"/> (FR-25): each
    /// the node a connected link's machine welcomed it with, whose region the membership names and the current placement
    /// includes. A commit whose origin is one of them was committed first by that machine; this does not check that machine's
    /// copy was not since replaced. Empty in a cell of one, when none is known, and past the bound; never inferred from
    /// replica id bits.
    /// </summary>
    ValueTask<IReadOnlySet<ReplicaId>> HeldOriginsAsync(TenantContext database, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlySet<ReplicaId>>(FrozenSet<ReplicaId>.Empty);
}

/// <summary>An admitted write attempt, or why it was refused before execution.</summary>
public readonly union RemoteAdmission(RemoteLease, Unavailable, Conflict);

/// <summary>One admitted write attempt's waiter slot; disposing it releases the slot once.</summary>
public sealed class RemoteLease(Action? release) : IDisposable
{
    private Action? _release = release;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>A cell of one: a write answers after its local durable commit, and a database may be placed in one region.</summary>
internal sealed class LocalDurability : IRemoteDurability
{
    public bool Confirms => false;

    public ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>([]);

    public ValueTask<RemoteAdmission> AdmitAsync(TenantContext database, CancellationToken cancellationToken) =>
        ValueTask.FromResult<RemoteAdmission>(new RemoteLease(null));

    public ValueTask<bool> WaitAsync(TenantContext database, CommitMarker marker, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}
