using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{

    private async Task ThrowIfPurgeFencedAsync(string databaseId, CancellationToken token)
    {
        if (!Ulid.TryParse(databaseId, out _))
        {
            return;
        }

        if (_system is not { } system)
        {
            if (SystemDatabaseExists)
            {
                throw new IOException("database publication requires its deletion authority to be readable");
            }
            return;
        }

        _ = await Io.WithAdmissionAsync(async () =>
        {
            await using var transaction = await system.BeginTransactionAsync(write: false, token).ConfigureAwait(false);
            if (await transaction.GetAsync(DatabaseErasure.Collection, databaseId, token).ConfigureAwait(false) is not null)
            {
                throw new IOException("a permanently deleted database cannot be published");
            }
            return true;
        }).ConfigureAwait(false);
    }

    public async ValueTask<bool> VerifyDatabaseErasureAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var (key, path) = Locate(database);
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RejectDatabaseLinks(path);
            if (_initializing.ContainsKey(key) || _stores.ContainsKey(key) || Pool.HasOpenHandles(path))
            {
                return false;
            }

            return await Io.RunAsync(() => !EnumerateDatabaseFiles(cancellationToken).Any(file =>
                file.Database is { } candidate && candidate.OrganizationId == database.OrganizationId
                    && candidate.ProjectId == database.ProjectId && candidate.DatabaseId == database.DatabaseId), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitCreation();
        }
    }
}
