using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    private readonly Lock _salvageGate = new();
    private readonly Dictionary<string, int> _salvageOperations = [];

    private async ValueTask<T> RunSalvageAsync<T>(string path, Func<SalvageOperation, Task<T>> call, CancellationToken token)
    {
        var parts = Path.GetRelativePath(Path.GetFullPath(_dataDirectory), Path.GetFullPath(path)).Split(Path.DirectorySeparatorChar);
        if (parts is not [var organization, var project, var name] || !IsId(organization) || !IsId(project)
            || name.Split('.') is not ["", var id, var incarnation, "rebuild", .. var suffix]
            || !IsId(id) || !IsId(incarnation) || !(suffix.Length == 0 || suffix is ["salvage"]))
        {
            throw new IOException("a salvage operation requires an owned database side copy");
        }

        var database = new TenantContext(organization, PlanTier.Hobby, project, id);
        var (key, mainPath) = Locate(database);
        SalvageOperation operation;
        await EnterCreationAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ThrowIfPurgeFencedAsync(id, token).ConfigureAwait(false);
            ThrowIfIntentPending(key);
            RejectDatabaseLinks(path);
            lock (_salvageGate)
            {
                _salvageOperations[mainPath] = _salvageOperations.GetValueOrDefault(mainPath) + 1;
            }
            operation = new SalvageOperation(this, mainPath, database);
        }
        finally
        {
            ExitCreation();
        }

        using (operation)
        {
            Io.Admit();
            try
            {
                return await call(operation).ConfigureAwait(false);
            }
            finally
            {
                Io.Release();
            }
        }
    }

    private ValueTask<T> ReadSalvageAsync<T>(string path, Func<global::Turso.Data.Sqlite.SqliteConnection, T> read, CancellationToken token) =>
        RunSalvageAsync(path, async operation =>
        {
            var connectionString = await Encryption.ExistingAsync(path, Io, token).ConfigureAwait(false);
            return await Io.RunAsync(() =>
            {
                var connection = Working(path, connectionString, operation);
                try
                {
                    _checkpoint("rebuild.salvage_reader", operation.Database);
                    return read(connection);
                }
                finally
                {
                    CloseSalvage(connection, operation);
                }
            }, token).ConfigureAwait(false);
        }, token);

    private static void CloseSalvage(global::Turso.Data.Sqlite.SqliteConnection connection, SalvageOperation operation)
    {
        try
        {
            connection.Dispose();
        }
        catch
        {
            // A failed native close cannot establish erasure, even after the path is unlinked.
            operation.CloseFailed = true;
            throw;
        }
    }

    private sealed class SalvageOperation(TursoStorageEngine engine, string path, TenantContext database) : IDisposable
    {
        internal TenantContext Database { get; } = database;
        internal bool CloseFailed { get; set; }

        public void Dispose()
        {
            if (CloseFailed)
            {
                return;
            }
            lock (engine._salvageGate)
            {
                var remaining = engine._salvageOperations[path] - 1;
                if (remaining == 0)
                {
                    _ = engine._salvageOperations.Remove(path);
                }
                else
                {
                    engine._salvageOperations[path] = remaining;
                }
            }
        }
    }

    // The caller holds creation. Never recursively open SYSTEM under that lock.
    private async Task ThrowIfPurgeFencedAsync(string databaseId, CancellationToken token)
    {
        if (!Ulid.TryParse(databaseId, out _))
        {
            return; // SYSTEM initialization is not customer database publication.
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
            lock (_salvageGate)
            {
                if (_salvageOperations.ContainsKey(path))
                {
                    return false;
                }
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
