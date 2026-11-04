using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine : IDatabaseRewind
{
    public async ValueTask<bool> RewindAsync(TenantContext database, TenantContext source, CancellationToken cancellationToken)
    {
        if (await OpenDatabaseAsync(source, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore from
            || await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore store)
        {
            return false;
        }

        Io.Admit();
        try
        {
            return await Io.WithAdmissionAsync(async () =>
            {
                var connection = await from.ConnectionStringAsync(Io, cancellationToken).ConfigureAwait(false);
                await store.RewindFromAsync(connection, from.FilePath, cancellationToken).ConfigureAwait(false);
                await store.StartIncarnationAsync(CancellationToken.None).ConfigureAwait(false);
                RaiseRevised(database);
                return true;
            }).ConfigureAwait(false);
        }
        finally
        {
            Io.Release();
        }
    }
}
