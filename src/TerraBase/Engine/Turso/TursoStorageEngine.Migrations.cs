using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Turso.Data.Sqlite;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    private bool _migrated;
    private readonly Counter<long> _migrationFailures;
    private readonly ConcurrentDictionary<string, byte> _oldFormatPaths = new(StringComparer.Ordinal);

    private async Task PrepareStorageMigrationAsync(CancellationToken cancellationToken)
    {
        var journal = Path.Combine(_dataDirectory, MigrationFile);
        var from = DirectoryVersion(_dataDirectory, _ownership);
        if (from == StorageFormat.Version && !File.Exists(journal))
        {
            return;
        }

        var required = new[] { (SystemName, LocateSystem()), (IntentName, Path.Combine(_dataDirectory, IntentFile)) };
        foreach (var (name, path) in required)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var staged = path + ".migrating";
            RejectDatabaseLinks(staged);
            DeleteFiles(staged);
            var connectionString = await Encryption.ExistingAsync(path, Io, cancellationToken).ConfigureAwait(false);
            await Io.RunAsync(() =>
            {
                using var connection = new SqliteConnection(connectionString);
                TursoSession.OpenExisting(connection, path);
                using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using var result = checkpoint.ExecuteReader();
                if (!result.Read() || result.GetInt64(0) != 0)
                {
                    throw new IOException($"database '{name}' could not be checkpointed before migration");
                }
            }, cancellationToken).ConfigureAwait(false);
            File.Copy(path, staged);
            File.Copy(path + DatabaseKeys.Suffix, staged + DatabaseKeys.Suffix);
            await MigrateFileAsync(name, staged, cancellationToken).ConfigureAwait(false);
            await Io.RunAsync(() =>
            {
                using var file = File.OpenHandle(staged, FileMode.Open, FileAccess.ReadWrite);
                RandomAccess.FlushToDisk(file);
            }, cancellationToken).ConfigureAwait(false);
        }

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
        foreach (var (_, path) in required)
        {
            if (File.Exists(path + ".migrating"))
            {
                File.Move(path + ".migrating", path, overwrite: true);
                File.Delete(path + ".migrating" + DatabaseKeys.Suffix);
            }
        }
        SyncDirectory(_dataDirectory);
    }

    public async ValueTask MigrateDatabasesAsync(CancellationToken cancellationToken)
    {
        await foreach (var database in ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            var (_, path) = Locate(database);
            try
            {
                await MigrateFileAsync(database.DatabaseId!, path, cancellationToken).ConfigureAwait(false);
                _ = _oldFormatPaths.TryRemove(path, out _);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _oldFormatPaths.TryAdd(path, 0);
                _migrationFailures.Add(1);
                _logger.ZLogError(error, $"Storage migration failed for {database.DatabaseId:@Database}; this tenant remains unavailable until its file is repaired");
            }
        }

        WriteFormat(_ownership, StorageFormat.Id);
        File.Delete(Path.Combine(_dataDirectory, MigrationFile));
        SyncDirectory(_dataDirectory);
        Volatile.Write(ref _migrated, true);
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
            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var result = checkpoint.ExecuteReader();
            if (!result.Read() || result.GetInt64(0) != 0)
            {
                throw new IOException($"database '{name}' could not be checkpointed after migration");
            }
        }, cancellationToken).ConfigureAwait(false);
    }
}
