using System.Runtime.CompilerServices;
using TerraBase.Features.Replication;
using TerraBase.Engine.Lattices;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoChangeGroupSource
{
    public async IAsyncEnumerable<ReplicaSnapshotPage> SnapshotAsync(
        TenantContext database, Ulid incarnation, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A tenant copy for a rebuild reads the whole file: bulk work. The catalog's revocation snapshot gates readiness.
        using var bulk = database == ChangeGroups.System ? null : StorageWork.Bulk();
        if (await BeginAsync(database, incarnation, write: false, cancellationToken).ConfigureAwait(false) is not (_, var transaction))
        {
            yield break;
        }

        await using (transaction.ConfigureAwait(false))
        {
            // First read pins the native snapshot. Collection and concurrent writes cannot change the states or vector
            // subsequently exported by this transaction. A rewrite drains this reader before replacing its file.
            var collected = await transaction.GetCollectedBeforeAsync(cancellationToken).ConfigureAwait(false);
            var applied = new Dictionary<ReplicaId, Hlc>(await transaction.GetAppliedAsync(cancellationToken).ConfigureAwait(false));
            var local = await transaction.GetLocalSnapshotCutAsync(_engine.ReplicaId, cancellationToken).ConfigureAwait(false);
            // A row received for this replica is no proof of what it committed: only its own local cut is.
            _ = applied.Remove(_engine.ReplicaId);
            if (local.Value > 0)
            {
                applied[_engine.ReplicaId] = local;
            }
            if (applied.Count > 1024)
            {
                throw new InvalidOperationException("the snapshot has too many origin cuts");
            }

            int[] buckets = [.. Enumerable.Range(0, 4096)];
            BucketCursor? after = null;
            do
            {
                var (states, next) = await transaction.ReadBucketStatesAsync(buckets, after, ChangeGroups.MaxDeltas,
                    ChangeGroups.MaxBatchBytes, cancellationToken).ConfigureAwait(false);
                after = next;
                if (states.Count > 0)
                {
                    yield return new ReplicaSnapshotPage(states, CollectedBefore: collected);
                }
            }
            while (after is not null);

            yield return new ReplicaSnapshotPage([], applied, collected);
        }
    }
}
