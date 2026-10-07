using Microsoft.Data.Sqlite;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Sqlite;

/// <summary>Continuous backup of the engine's files (TS-633): log taps, roll-forward and import of restored files.</summary>
internal sealed partial class SqliteStorageEngine
{
    public async ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken)
    {
        SqliteDatabaseStore? store;
        if (database is null)
        {
            store = (SqliteDatabaseStore)await OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var (key, path) = Locate(database);
            store = File.Exists(path) ? await OpenStoreAsync(key, path, cancellationToken).ConfigureAwait(false) : null;
        }

        if (store is null)
        {
            return null;
        }

        Io.Admit();
        try
        {
            return new SqliteWalTap(store, Io, await store.RentSessionAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            Io.Release();
        }
    }

    public async ValueTask<bool> RollForwardAsync(string file, IAsyncEnumerable<Stream> segments, CancellationToken cancellationToken)
    {
        if (!await SqliteWalTap.ApplyAsync(file, segments, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        Io.Admit();
        try
        {
            return await Io.RunAsync(() =>
            {
                try
                {
                    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = file,
                        Mode = SqliteOpenMode.ReadWrite,
                        Pooling = false,
                    }.ToString());
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "PRAGMA integrity_check;";
                    return command.ExecuteScalar() as string == "ok";
                }
                catch (SqliteException)
                {
                    // Not a database, or corrupt beyond what integrity_check reports.
                    return false;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Io.Release();
        }
    }

    public async ValueTask ImportDatabaseAsync(TenantContext database, string file, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        var (key, path) = Locate(database);
        Task<SqliteDatabaseStore> initialization;
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfIntentPending(key);
            RejectDatabaseLinks(path);
            if (File.Exists(path))
            {
                throw new InvalidOperationException($"database '{key.Database}' already exists");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await PublishAsync(key.Database, path, async temporary =>
            {
                File.Move(file, temporary);
                await SqliteDatabaseStore.InitializeAsync(key.Database, temporary, Io, _pragmas, SqliteDatabaseStore.CopyReset, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            _checkpoint("create.published", database);
            initialization = GetInitializationLocked(key, path, cancellationToken, out _);
        }
        finally
        {
            ExitCreation();
        }

        await initialization.ConfigureAwait(false);
    }
}
