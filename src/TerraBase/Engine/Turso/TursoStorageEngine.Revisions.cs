using TerraBase.Engine.Revisions;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine : IDatabaseRevisions
{
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
