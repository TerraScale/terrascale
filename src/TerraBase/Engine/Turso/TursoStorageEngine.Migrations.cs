using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;
using Turso.Raw.Public;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    private const string StagedSuffix = ".migrating";
    private static readonly string[] JournalSidecars = ["-wal", "-shm"];

    private bool _recovered;
    private readonly Counter<long> _migrationFailures;
    private readonly ConcurrentDictionary<string, byte> _oldFormatPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Organization, string Project, string Database), Task> _migrating = [];

    public void CompleteRecovery() => Volatile.Write(ref _recovered, true);

    private async Task PrepareStorageMigrationAsync(CancellationToken cancellationToken)
    {
        var directory = ReadDirectoryFormat(_ownership);
        if (!directory.NeedsMigration)
        {
            return;
        }

        (string Name, string Path)[] required = [(SystemName, LocateSystem()), (IntentName, Path.Combine(_dataDirectory, IntentFile))];
        foreach (var (name, path) in required)
        {
            var staged = path + StagedSuffix;
            RejectDatabaseLinks(staged);
            DeleteFiles(staged);
            if (!File.Exists(path))
            {
                continue;
            }

            var connectionString = await Encryption.ExistingAsync(path, Io, cancellationToken).ConfigureAwait(false);
            await Io.RunAsync(() =>
            {
                using var connection = new SqliteConnection(connectionString);
                TursoSession.OpenExisting(connection, path);
                Checkpoint(name, connection);
            }, cancellationToken).ConfigureAwait(false);
            File.Copy(path, staged);
            File.Copy(path + DatabaseKeys.Suffix, staged + DatabaseKeys.Suffix);
            _ = await MigrateFileAsync(name, staged, cancellationToken).ConfigureAwait(false);
            await Io.RunAsync(() =>
            {
                using var file = File.OpenHandle(staged, FileMode.Open, FileAccess.ReadWrite);
                RandomAccess.FlushToDisk(file);
            }, cancellationToken).ConfigureAwait(false);
        }

        WriteFormat(_ownership, directory.Journal);
        foreach (var (_, path) in required)
        {
            var staged = path + StagedSuffix;
            if (!File.Exists(staged))
            {
                continue;
            }

            foreach (var sidecar in JournalSidecars)
            {
                File.Delete(path + sidecar);
                File.Delete(staged + sidecar);
            }
            SyncDirectory(_dataDirectory);
            File.Move(staged, path, overwrite: true);
            File.Delete(staged + DatabaseKeys.Suffix);
            SyncDirectory(_dataDirectory);
        }
    }

    public async ValueTask MigrateDatabasesAsync(CancellationToken cancellationToken)
    {
        if (!ReadDirectoryFormat(_ownership).NeedsMigration)
        {
            return;
        }

        var started = Time.GetTimestamp();
        var migrated = 0;
        var failed = 0;
        await foreach (var database in ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            var (_, path) = Locate(database);
            try
            {
                if (await MigrateTenantAsync(database, path, cancellationToken).ConfigureAwait(false))
                {
                    migrated++;
                }
                _ = _oldFormatPaths.TryRemove(path, out _);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failed++;
                _oldFormatPaths.TryAdd(path, 0);
                _migrationFailures.Add(1);
                _logger.ZLogError(error, $"Storage migration failed for {database.DatabaseId:@Database}; it is migrated when it is next opened");
            }
        }

        WriteFormat(_ownership, StorageFormat.Id);
        SyncDirectory(_dataDirectory);
        _logger.ZLogInformation(
            $"Storage migration to format {StorageFormat.Id:@Format} finished: {migrated:@Migrated} files migrated, {failed:@Failed} left for their next open, in {Time.GetElapsedTime(started).TotalMilliseconds:@ElapsedMs} ms");
    }

    private async Task<bool> MigrateTenantAsync(TenantContext database, string path, CancellationToken cancellationToken)
    {
        await using var read = await Ledger.BeginReadAsync(database, cancellationToken).ConfigureAwait(false);
        cancellationToken = read.Token;
        var key = Key(database);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stores.ContainsKey(key) || _initializing.ContainsKey(key))
            {
                return false;
            }

            _migrating.Add(key, finished.Task);
        }
        finally
        {
            ExitCreation();
        }

        try
        {
            var result = await MigrateFileAsync(database.DatabaseId!, path, cancellationToken).ConfigureAwait(false);
            await Ledger.ThrowIfUnavailableAsync(database, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            try
            {
                await EnterCreationAsync(CancellationToken.None, admitted: true).ConfigureAwait(false);
                try
                {
                    _ = _migrating.Remove(key);
                }
                finally
                {
                    ExitCreation(admitted: true);
                }
            }
            finally
            {
                finished.SetResult();
            }
        }
    }

    private async Task<bool> MigrateFileAsync(string name, string path, CancellationToken cancellationToken)
    {
        RejectDatabaseLinks(path);
        if (!File.Exists(path))
        {
            return false;
        }

        var connectionString = await Encryption.ExistingAsync(path, Io, cancellationToken).ConfigureAwait(false);
        return await Io.RunAsync(() =>
        {
            using var connection = new SqliteConnection(connectionString)
            {
                CloseRefusal = () => cancellationToken.IsCancellationRequested ? "the migration copy lease was refused" : null,
            };
            cancellationToken.ThrowIfCancellationRequested();
            TursoSession.OpenExisting(connection, path);
            using var interrupted = cancellationToken.Register(() => TursoBindings.Interrupt(TursoSession.Handle(connection)));
            var before = StorageMigrations.FileFormat(connection);
            if (StorageMigrations.Apply(name, connection, cancellationToken) == before)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Checkpoint(name, connection);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void Checkpoint(string name, SqliteConnection connection)
    {
        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var result = checkpoint.ExecuteReader();
        if (!result.Read() || result.GetInt64(0) != 0)
        {
            throw new IOException($"database '{name}' could not be checkpointed");
        }
    }
}
