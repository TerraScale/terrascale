using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>
/// Joins received groups into the local copy of a database the catalog places in this region, or into the system database,
/// which every region holds (sections 9 and 15 of the design):
/// whole groups per transaction, at most one transaction's worth of documents each, every transaction synced to disk
/// before the next, so an ack means the groups survive a power loss here.
/// </summary>
internal sealed class StorageChangeGroupSink(IStorageEngine engine, ITenantDirectory tenants, DatabaseLifecycleCheckpoint checkpoint, ReplicationTimings timings) : IChangeGroupSink
{
    public async ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, Hlc expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var step = timings.Now;
        if (await OpenAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return null;
        }

        step = timings.Record(ReplicationTimings.ReceiveOpen, step);

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
            await transaction.RequireAppliedAsync(origin, expected, cancellationToken).ConfigureAwait(false);
            step = timings.Record(ReplicationTimings.ReceiveBegin, step);
            for (; next < end; next++)
            {
                foreach (var delta in groups[next].Deltas)
                {
                    _ = await transaction.JoinAsync(delta.Collection, delta.Id, delta.State, new EntryVersion(delta.Stamp, origin), cancellationToken).ConfigureAwait(false);
                    applied = applied.Value < delta.Stamp.Value ? delta.Stamp : applied;
                }
            }

            await transaction.RecordAppliedAsync(origin, applied, cancellationToken).ConfigureAwait(false);
            step = timings.Record(ReplicationTimings.ReceiveJoin, step);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            step = timings.Record(ReplicationTimings.ReceiveCommit, step);
            expected = new Hlc(Math.Max(expected.Value, applied.Value));
            checkpoint("replication.apply.committed", database);
        }

        return applied;
    }

    public async ValueTask<Ulid?> RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, Hlc expected, Ulid? incarnation, CancellationToken cancellationToken)
    {
        if (await OpenAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return null;
        }

        await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await transaction.GetIncarnationAsync(cancellationToken).ConfigureAwait(false);
        if (incarnation is { } pinned && current != pinned)
        {
            return null;
        }

        await transaction.RequireAppliedAsync(origin, expected, cancellationToken).ConfigureAwait(false);
        await transaction.RecordAppliedAsync(origin, frontier, cancellationToken).ConfigureAwait(false);
        _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current;
    }

    public async ValueTask<int?> RepairAsync(TenantContext database, Ulid incarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (incarnation == Ulid.Empty)
        {
            throw new ArgumentException("an incarnation is named", nameof(incarnation));
        }

        if (await OpenAsync(database, cancellationToken).ConfigureAwait(false) is not { } store || store.Incarnation != incarnation)
        {
            return null;
        }

        var changed = 0;
        foreach (var chunk in states.Chunk(ChangeGroups.MaxDeltasOf(database)))
        {
            await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (await transaction.GetIncarnationAsync(cancellationToken).ConfigureAwait(false) != incarnation)
            {
                return null;
            }

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
