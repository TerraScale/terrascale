using TerraBase.Features.Replication;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

/// <summary>A whole pinned source snapshot becomes one durable receiver transaction; incomplete streams roll back.</summary>
internal sealed class TursoReplicaSnapshotImporter(TursoStorageEngine engine, StorageChangeGroupSink sink, StampingStorageEngine stamps) : IReplicaSnapshotImporter
{
    public async Task<bool> ImportAsync(TenantContext database, Ulid incarnation, IAsyncEnumerable<ReplicaSnapshotPage> pages,
        IReadOnlySet<ulong> origins, CancellationToken cancellationToken)
    {
        var store = database == ChangeGroups.System ? (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)
            : (TursoDatabaseStore?)await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        if (store is null)
        {
            return false;
        }

        stamps.InvalidateAll();
        try
        {
            return await ImportHeldAsync(store, database, incarnation, pages, origins, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Imported states and forgotten tombstones bypass the public transaction wrapper.
            stamps.InvalidateAll();
        }
    }

    private async Task<bool> ImportHeldAsync(TursoDatabaseStore store, TenantContext database, Ulid incarnation,
        IAsyncEnumerable<ReplicaSnapshotPage> pages, IReadOnlySet<ulong> origins, CancellationToken cancellationToken)
    {
        await using var transaction = (TursoStorageTransaction)await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await transaction.GetIncarnationAsync(cancellationToken).ConfigureAwait(false) != incarnation)
        {
            return false;
        }

        int[] buckets = [.. Enumerable.Range(0, 4096)];
        BucketCursor? after = null;
        ReplicaSnapshotPage? terminal = null;
        Engine.Lattices.Hlc? floor = null;
        await foreach (var page in pages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (terminal is not null || floor is { } previous && previous != page.CollectedBefore
                || page.Applied is not null && page.States.Count != 0)
            {
                throw new InvalidDataException("inconsistent snapshot pages");
            }

            floor = page.CollectedBefore;
            TursoStorageTransaction.ValidateRefillPage(database, buckets, after, page.States);
            if (floor.Value.Value > 0)
            {
                var cursor = after;
                do
                {
                    // A source's collected absence cannot delete a live local survivor. Refuse the whole import when
                    // deletion evidence is missing; retained tombstones can be forgotten safely under this writer.
                    if (await transaction.ReconcileRefillAsync(buckets, cursor, page.States, floor.Value, cancellationToken).ConfigureAwait(false) is not { } result)
                    {
                        return false;
                    }

                    cursor = result.Next;
                }
                while (cursor is not null);
            }

            _ = await sink.RepairPageAsync(transaction, database, page.States, baseline: true, cancellationToken).ConfigureAwait(false);
            if (page.States.Count > 0)
            {
                var last = page.States[^1];
                after = new BucketCursor(TursoStorageTransaction.Bucket(last.Collection, last.Id), last.Collection, last.Id);
            }

            if (page.Applied is not null)
            {
                terminal = page;
            }
        }

        // Reaching clean EOF is part of the proof. Trailers may fail after the terminal data frame.
        if (terminal is null)
        {
            return false;
        }

        foreach (var cut in terminal.Applied!.Where(cut => origins.Contains(cut.Key.Value)))
        {
            await transaction.RecordAppliedAsync(cut.Key, cut.Value, cancellationToken).ConfigureAwait(false);
        }

        await transaction.InheritCollectedBeforeAsync(floor!.Value, cancellationToken).ConfigureAwait(false);
        _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
