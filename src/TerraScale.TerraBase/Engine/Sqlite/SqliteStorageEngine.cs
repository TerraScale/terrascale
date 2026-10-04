using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace TerraScale.TerraBase.Engine.Sqlite;

/// <summary>
/// One SQLite file per database (<c>&lt;data-directory&gt;/&lt;name&gt;.db</c>), WAL journal,
/// connections pooled by Microsoft.Data.Sqlite.
/// </summary>
/// <remarks>
/// ponytail: Microsoft.Data.Sqlite runs async calls synchronously on the caller's thread;
/// move disk work to a dedicated scheduler when request latency under write load matters.
/// </remarks>
internal sealed class SqliteStorageEngine(string dataDirectory) : IStorageEngine
{
    private const string FileExtension = ".db";

    // Opened (and migrated) stores; a database is migrated once per process.
    private readonly ConcurrentDictionary<DatabaseName, SqliteDatabaseStore> _stores = new();

    // ponytail: one lock for every creation (rare, and the data directory belongs to one process);
    // per-name locks if creation throughput ever matters.
    private readonly SemaphoreSlim _creation = new(1, 1);

    public string Name => "sqlite";

    public async ValueTask<bool> CreateDatabaseAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = PathOf(name);
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                await PublishAsync(name, path, temporary => SqliteDatabaseStore.InitializeAsync(name, temporary, cancellationToken)).ConfigureAwait(false);
                await OpenStoreAsync(name, path, cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
        finally
        {
            _creation.Release();
        }

        await OpenStoreAsync(name, path, cancellationToken).ConfigureAwait(false);
        return false;
    }

    public async ValueTask<bool> CopyDatabaseAsync(DatabaseName source, DatabaseName target, CancellationToken cancellationToken)
    {
        if (await OpenDatabaseAsync(source, cancellationToken).ConfigureAwait(false) is not SqliteDatabaseStore store)
        {
            return false;
        }

        var path = PathOf(target);
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                throw new InvalidOperationException($"database '{target}' already exists");
            }

            await PublishAsync(target, path, temporary => store.CopyToAsync(temporary, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            _creation.Release();
        }

        await OpenStoreAsync(target, path, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <remarks>
    /// ponytail: a transaction already running on the deleted database finishes on the unlinked file (Linux)
    /// and new ones fail; add per-database reference counting if deletes must wait for in-flight work.
    /// </remarks>
    public async ValueTask<bool> DeleteDatabaseAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        var path = PathOf(name);
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (_stores.TryRemove(name, out var store))
            {
                store.ClearPool();
            }

            DeleteFiles(path);
            return true;
        }
        finally
        {
            _creation.Release();
        }
    }

    // Initializes a private temporary file and then moves it into place, so a database is visible
    // only once fully initialized and a failed or cancelled creation leaves nothing behind.
    private async Task PublishAsync(DatabaseName name, string path, Func<string, Task> initialize)
    {
        var temporary = Path.Combine(dataDirectory, $".{name.Value}.{Guid.NewGuid():N}.creating");
        try
        {
            await initialize(temporary).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            DeleteFiles(temporary);
        }
    }

    private static void DeleteFiles(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            File.Delete(file);
        }
    }

    public async ValueTask<IDatabaseStore?> OpenDatabaseAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        var path = PathOf(name);
        return File.Exists(path) ? await OpenStoreAsync(name, path, cancellationToken).ConfigureAwait(false) : null;
    }

    public async IAsyncEnumerable<DatabaseName> ListDatabasesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(dataDirectory))
        {
            yield break;
        }

        var names = Directory.EnumerateFiles(dataDirectory, "*" + FileExtension)
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal);
        foreach (var file in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DatabaseName.TryParse(file, out var name))
            {
                yield return name;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _creation.Dispose();
        // Pooled connections keep files open; release them so the data directory can be removed.
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private string PathOf(DatabaseName name) => Path.Combine(dataDirectory, name.Value + FileExtension);

    private async ValueTask<SqliteDatabaseStore> OpenStoreAsync(DatabaseName name, string path, CancellationToken cancellationToken)
    {
        if (_stores.TryGetValue(name, out var store))
        {
            return store;
        }

        // Concurrent first opens may both migrate; the migration is transactional and idempotent.
        store = new SqliteDatabaseStore(name, path);
        await store.MigrateAsync(cancellationToken).ConfigureAwait(false);
        return _stores.GetOrAdd(name, store);
    }
}
