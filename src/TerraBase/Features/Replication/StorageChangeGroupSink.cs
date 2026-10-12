using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>
/// Joins received groups into the local copy of a database the catalog places in this region, or into the system database,
/// which every region holds (sections 9 and 15 of the design):
/// whole groups per transaction, at most one transaction's worth of documents each, every transaction synced to disk
/// before the next, so an ack means the groups survive a power loss here.
/// </summary>
internal sealed class StorageChangeGroupSink(IStorageEngine engine, ITenantDirectory tenants) : IChangeGroupSink
{
    public async ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (await OpenAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return null;
        }

        var applied = default(Hlc);
        var next = 0;
        while (next < groups.Count)
        {
            // A group never splits; a single group holds at most one transaction's documents.
            var end = next;
            for (var deltas = 0; end < groups.Count && (end == next || deltas + groups[end].Deltas.Count <= ChangeGroups.MaxDeltasOf(database)); end++)
            {
                deltas += groups[end].Deltas.Count;
            }

            await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
            for (; next < end; next++)
            {
                foreach (var delta in groups[next].Deltas)
                {
                    _ = groups[next].Stale
                        ? await transaction.JoinStaleAsync(delta.Collection, delta.Id, delta.State, new EntryVersion(delta.Stamp, origin), cancellationToken).ConfigureAwait(false)
                        : await transaction.JoinAsync(delta.Collection, delta.Id, delta.State, new EntryVersion(delta.Stamp, origin), cancellationToken).ConfigureAwait(false);
                    applied = applied.Value < delta.Stamp.Value ? delta.Stamp : applied;
                }
            }

            await transaction.RecordAppliedAsync(origin, applied, cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return applied;
    }

    public async ValueTask RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, CancellationToken cancellationToken)
    {
        // The catalog records frontiers like any database; it is never collected (TursoChangeGroupSource).
        var store = database == ChangeGroups.System
            ? await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)
            : await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        if (store is null)
        {
            return;
        }

        await using var transaction = await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
        await transaction.RecordAppliedAsync(origin, frontier, cancellationToken).ConfigureAwait(false);
        _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int?> RepairAsync(TenantContext database, IReadOnlyList<RepairState> states, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (await OpenAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return null;
        }

        var changed = 0;
        foreach (var chunk in states.Chunk(ChangeGroups.MaxDeltasOf(database)))
        {
            await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var state in chunk.Where(state => state.State.MaxStamp.Value != 0))
            {
                // Origin 0: a repair (section 9), so the tailer never ships it and it moves no origin's applied stamp.
                changed += await transaction.JoinAsync(state.Collection, state.Id, state.State, new EntryVersion(state.State.MaxStamp, default), cancellationToken)
                    .ConfigureAwait(false) ? 1 : 0;
            }

            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    // The system database, which every region holds, or a client database the catalog places here (null otherwise).
    private async ValueTask<IDatabaseStore?> OpenAsync(TenantContext database, CancellationToken cancellationToken) =>
        database == ChangeGroups.System ? await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)
        : await tenants.RegisteredAsync(database, cancellationToken).ConfigureAwait(false) is true
            && await tenants.EnsureLocalDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is Done
            ? await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false)
            : null;
}
