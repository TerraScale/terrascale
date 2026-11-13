using Turso.Data.Sqlite;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    private readonly SemaphoreSlim _migration = new(1, 1);
    private bool _migrated;

    private async Task MigrateStorageAsync(CancellationToken cancellationToken)
    {
        await _migration.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_migrated)
            {
                return;
            }

            var journal = Path.Combine(_dataDirectory, MigrationFile);
            var from = DirectoryVersion(_dataDirectory, _ownership);
            if (from != StorageFormat.Version || File.Exists(journal))
            {
                if (!File.Exists(journal))
                {
                    var temporary = journal + ".tmp";
                    RejectLink(temporary);
                    using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        file.Write(EncodingBytes($"migrating:{from}->{StorageFormat.Version}"));
                        file.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, journal, overwrite: true);
                    SyncDirectory(_dataDirectory);
                }

                WriteFormat(_ownership, $"migrating:{from}->{StorageFormat.Version}");
                await MigrateFileAsync(SystemName, LocateSystem(), cancellationToken).ConfigureAwait(false);
                await MigrateFileAsync(SystemName, Path.Combine(_dataDirectory, IntentFile), cancellationToken).ConfigureAwait(false);
                await foreach (var database in ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
                {
                    var (_, path) = Locate(database);
                    await MigrateFileAsync(database.DatabaseId!, path, cancellationToken).ConfigureAwait(false);
                }

                WriteFormat(_ownership, StorageFormat.Id);
                File.Delete(journal);
                SyncDirectory(_dataDirectory);
            }

            Volatile.Write(ref _migrated, true);
        }
        finally
        {
            _migration.Release();
        }
    }

    private async Task MigrateFileAsync(string name, string path, CancellationToken cancellationToken)
    {
        RejectDatabaseLinks(path);
        if (!File.Exists(path))
        {
            return;
        }

        var connectionString = await Encryption.ExistingAsync(path, Io, cancellationToken).ConfigureAwait(false);
        await Io.RunAsync(() =>
        {
            using var connection = new SqliteConnection(connectionString);
            TursoSession.OpenExisting(connection, path);
            StorageMigrations.Apply(name, connection, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
        _logger.ZLogInformation($"Storage migration completed for {path:@Database}, format {StorageFormat.Version:@Format}");
    }
}
