using TerraBase.Engine.Revisions;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine : IDatabaseRevisions
{
    public event Action<TenantContext>? Revised;

    private void RaiseRevised(TenantContext database)
    {
        foreach (var subscriber in Delegate.EnumerateInvocationList(Revised))
        {
            try
            {
                subscriber(database);
            }
            catch (Exception error)
            {
                _logger.ZLogError(error, $"A revision notification failed; the commit itself is durable");
            }
        }
    }

    public async ValueTask<RevisionPullResult?> PullAsync(TenantContext database, string? clientRevision, IRevisionPullSink sink, CancellationToken cancellationToken)
    {
        if (await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore store)
        {
            return null;
        }

        Io.Admit();
        try
        {
            return await Io.WithAdmissionAsync<RevisionPullResult?>(async () => await store.PullRevisionAsync(clientRevision, sink, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Io.Release();
        }
    }

    public async ValueTask<RevisionChecksums?> ChecksumAsync(TenantContext database, DatabaseRevision revision, ChecksumCursor? cursor, CancellationToken cancellationToken)
    {
        if (await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore store)
        {
            return null;
        }

        using var tenant = QosContext.Enter(database);
        using var bulk = StorageWork.Bulk();
        Io.Admit();
        try
        {
            return await Io.WithAdmissionAsync<RevisionChecksums?>(async () => await store.ChecksumAsync(revision, cursor, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Io.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ReplicaPullObservation>> ReplicaStatusAsync(TenantContext database, IReadOnlyList<string> regions, CancellationToken cancellationToken)
    {
        if (await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore store)
        {
            return [.. regions.Select(region => new ReplicaPullObservation(region, null, null, null))];
        }

        return await store.ReplicaStatusAsync(regions, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RetainReplicaCdcAsync(TenantContext database, DatabasePlacement placement, string? region, string? revision, CancellationToken cancellationToken)
    {
        if (await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is TursoDatabaseStore store)
        {
            await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
            var retention = (ICdcReplicaRetentionTransaction)transaction;
            await retention.SetReplicaCdcRegionsAsync(placement.Replicas, placement.Version, cancellationToken).ConfigureAwait(false);
            if (region is not null)
            {
                await retention.AcknowledgeReplicaCdcAsync(region, store.Revisions.AcknowledgedReplicaCdc(region, revision), cancellationToken).ConfigureAwait(false);
            }

            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<DatabaseRevision?> CurrentRevisionAsync(TenantContext database, CancellationToken cancellationToken) =>
        await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is TursoDatabaseStore store
            ? await store.CurrentRevisionAsync(cancellationToken).ConfigureAwait(false)
            : null;

    public async ValueTask RecordReplicaPullAsync(TenantContext database, string region, RevisionPullResult offered, CancellationToken cancellationToken)
    {
        if (await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is TursoDatabaseStore store)
        {
            store.Revisions.RecordReplicaPull(region, offered);
        }
    }

    public async ValueTask<bool> StartIncarnationAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore store)
        {
            return false;
        }

        Io.Admit();
        try
        {
            await Io.WithAdmissionAsync(async () =>
            {
                await store.StartIncarnationAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            return true;
        }
        finally
        {
            Io.Release();
        }
    }
}
