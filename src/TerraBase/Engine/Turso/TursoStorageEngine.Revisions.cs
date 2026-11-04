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
