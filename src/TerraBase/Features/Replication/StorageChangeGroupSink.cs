using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>
/// Joins received groups into the local copy of a database the catalog places in this region (section 9 of the design):
/// whole groups per transaction, at most one transaction's worth of documents each, every transaction synced to disk
/// before the next, so an ack means the groups survive a power loss here.
/// </summary>
internal sealed class StorageChangeGroupSink(IStorageEngine engine, ITenantDirectory tenants) : IChangeGroupSink
{
    public async ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (await tenants.RegisteredAsync(database, cancellationToken).ConfigureAwait(false) is not true
            || await tenants.EnsureLocalDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not Done
            || await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return null;
        }

        var applied = default(Hlc);
        var next = 0;
        while (next < groups.Count)
        {
            // A group never splits; a single group holds at most one transaction's documents.
            var end = next;
            for (var deltas = 0; end < groups.Count && (end == next || deltas + groups[end].Deltas.Count <= ChangeGroups.MaxDeltas); end++)
            {
                deltas += groups[end].Deltas.Count;
            }

            await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
            for (; next < end; next++)
            {
                foreach (var delta in groups[next].Deltas)
                {
                    _ = await transaction.JoinAsync(delta.Collection, delta.Id, delta.State, new EntryVersion(delta.Stamp, origin), cancellationToken).ConfigureAwait(false);
                    applied = applied.Value < delta.Stamp.Value ? delta.Stamp : applied;
                }
            }

            await transaction.RecordAppliedAsync(origin, applied, cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return applied;
    }
}
