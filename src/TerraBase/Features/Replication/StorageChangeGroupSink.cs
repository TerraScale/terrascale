using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Features.Webhooks;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>
/// Joins received groups into the local copy of a database the catalog places in this region, or into the system database,
/// which every region holds (sections 9 and 15 of the design):
/// whole groups per transaction, at most one transaction's worth of documents each, every transaction synced to disk
/// before the next, so an ack means the groups survive a power loss here.
/// </summary>
internal sealed class StorageChangeGroupSink(IStorageEngine engine, ITenantDirectory tenants, DatabaseLifecycleCheckpoint checkpoint,
    IWebhookReplicaAdmission webhooks, ReplicationTimings timings, IClosureCancellation cancellations) : IChangeGroupSink
{
    public async ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, Hlc expected, CancellationToken cancellationToken, ApplyProgress? progress = null)
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
            await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
            var started = timings.Now;
            progress?.Attach(transaction);
            await transaction.RequireAppliedAsync(origin, expected, cancellationToken).ConfigureAwait(false);
            step = timings.Record(ReplicationTimings.ReceiveBegin, step);
            List<(string, string, DocumentState, EntryVersion)>? deferred = null;
            HashSet<(string Collection, string Id)> closureChanges = [];
            List<(string Id, DocumentState State, EntryVersion At)> closureJobs = [];
            // Check between whole groups; deferred admission and closure observation still finish before each commit.
            for (var deltas = 0; next < groups.Count && ReplicationWriteBatch.CanAdd(deltas, groups[next].Deltas.Count, timings.Elapsed(started)); next++)
            {
                deltas += groups[next].Deltas.Count;
                foreach (var delta in groups[next].Deltas)
                {
                    var at = new EntryVersion(delta.Stamp, origin);
                    TrackClosure(delta.Collection, delta.Id, closureChanges);
                    if (database != ChangeGroups.System && delta.Collection == ClosureCancellation.Jobs)
                    {
                        closureJobs.Add((delta.Id, delta.State, at));
                    }
                    else if (database == ChangeGroups.System && webhooks.Decides(delta.Collection))
                    {
                        (deferred ??= []).Add((delta.Collection, delta.Id, delta.State, at));
                    }
                    else
                    {
                        _ = await transaction.JoinAsync(delta.Collection, delta.Id, delta.State, at, cancellationToken).ConfigureAwait(false);
                    }

                    // A refused state still counts as applied: the origin's stream moves on and never resends it.
                    applied = applied.Value < delta.Stamp.Value ? delta.Stamp : applied;
                }
            }

            _ = await JoinAdmittedAsync(transaction, deferred, cancellationToken).ConfigureAwait(false);
            _ = await JoinClosureJobsAsync(transaction, closureJobs, cancellationToken).ConfigureAwait(false);
            await cancellations.ObserveAsync(transaction, closureChanges, cancellationToken).ConfigureAwait(false);
            await transaction.RecordAppliedAsync(origin, applied, cancellationToken).ConfigureAwait(false);
            step = timings.Record(ReplicationTimings.ReceiveJoin, step);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            step = timings.Record(ReplicationTimings.ReceiveCommit, step);
            progress?.Commit();
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
        var next = 0;
        while (next < states.Count)
        {
            await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
            var started = timings.Now;
            if (await transaction.GetIncarnationAsync(cancellationToken).ConfigureAwait(false) != incarnation)
            {
                return null;
            }

            changed += await RepairPageAsync(transaction, database, Page(), baseline: false, cancellationToken).ConfigureAwait(false);

            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            // The shared join/admission path also serves atomic snapshot replay; bound only this repair's input.
            IEnumerable<RepairState> Page()
            {
                var start = next;
                while (next < states.Count && ReplicationWriteBatch.CanAdd(next - start, 1, timings.Elapsed(started)))
                {
                    yield return states[next++];
                }
            }
        }

        return changed;
    }

    internal async ValueTask<int> RepairPageAsync(IStorageTransaction transaction, TenantContext database,
        IEnumerable<RepairState> states, bool baseline, CancellationToken cancellationToken)
    {
        var changed = 0;
        List<(string, string, DocumentState, EntryVersion)>? deferred = null;
        HashSet<(string Collection, string Id)> closureChanges = [];
        List<(string Id, DocumentState State, EntryVersion At)> closureJobs = [];
        foreach (var state in states.Where(state => state.State.MaxStamp.Value != 0))
        {
            // Origin 0: a repair (section 9), so the tailer never ships it and it moves no origin's applied stamp.
            var at = new EntryVersion(state.State.MaxStamp, default);
            TrackClosure(state.Collection, state.Id, closureChanges);
            if (database != ChangeGroups.System && state.Collection == ClosureCancellation.Jobs)
            {
                closureJobs.Add((state.Id, state.State, at));
            }
            else if (database == ChangeGroups.System && webhooks.Decides(state.Collection))
            {
                (deferred ??= []).Add((state.Collection, state.Id, state.State, at));
            }
            else
            {
                changed += (baseline ? await transaction.JoinBaselineAsync(state.Collection, state.Id, state.State, at, cancellationToken).ConfigureAwait(false)
                    : await transaction.JoinAsync(state.Collection, state.Id, state.State, at, cancellationToken).ConfigureAwait(false)) ? 1 : 0;
            }
        }

        changed += await JoinAdmittedAsync(transaction, deferred, cancellationToken, baseline).ConfigureAwait(false);
        changed += await JoinClosureJobsAsync(transaction, closureJobs, cancellationToken, baseline).ConfigureAwait(false);
        await cancellations.ObserveAsync(transaction, closureChanges, cancellationToken).ConfigureAwait(false);

        return changed;
    }

    private async ValueTask<int> JoinClosureJobsAsync(IStorageTransaction transaction,
        List<(string Id, DocumentState State, EntryVersion At)> jobs, CancellationToken cancellationToken, bool baseline = false)
    {
        var changed = 0;
        foreach (var (id, state, at) in jobs)
        {
            if (await cancellations.AdmitAsync(transaction, state, cancellationToken).ConfigureAwait(false))
            {
                changed += (baseline ? await transaction.JoinBaselineAsync(ClosureCancellation.Jobs, id, state, at, cancellationToken).ConfigureAwait(false)
                    : await transaction.JoinAsync(ClosureCancellation.Jobs, id, state, at, cancellationToken).ConfigureAwait(false)) ? 1 : 0;
            }
        }

        return changed;
    }

    private static void TrackClosure(string collection, string id, HashSet<(string Collection, string Id)> changes)
    {
        if (collection is ClosureCancellation.Jobs or ClosureCancellation.Fences or ClosureCancellation.Commits)
        {
            changes.Add((collection, id));
        }
    }

    // Joins are commutative per key, so webhook state joins last: deletion authority in the same transaction dominates it.
    private async ValueTask<int> JoinAdmittedAsync(IStorageTransaction transaction, List<(string Collection, string Id, DocumentState State, EntryVersion At)>? deferred,
        CancellationToken cancellationToken, bool baseline = false)
    {
        var changed = 0;
        foreach (var (collection, id, state, at) in deferred ?? [])
        {
            if (await webhooks.AdmitAsync(transaction, collection, id, state, cancellationToken).ConfigureAwait(false))
            {
                changed += (baseline ? await transaction.JoinBaselineAsync(collection, id, state, at, cancellationToken).ConfigureAwait(false)
                    : await transaction.JoinAsync(collection, id, state, at, cancellationToken).ConfigureAwait(false)) ? 1 : 0;
            }
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
