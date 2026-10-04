using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace TerraScale.TerraBase.Engine.Sqlite;

/// <summary>
/// One SQLite file per database (<c>&lt;data-directory&gt;/&lt;name&gt;.db</c>), WAL journal,
/// connections and their prepared statements pooled per database by <see cref="SqliteDatabaseStore"/>.
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

    // Serializes every change to the databases and the open stores: create, copy, delete, first open
    // and shutdown. A first open can then neither publish a store for a file a delete removed (its idle
    // session would keep the unlinked file) nor publish one after shutdown closed the stores.
    // ponytail: one lock for all of them (rare, and a database is first opened once per process);
    // per-name locks if creation or startup throughput ever matters.
    private readonly SemaphoreSlim _creation = new(1, 1);


    // Guarded by _creation.
    private bool _disposed;

    public string Name => "sqlite";

    public async ValueTask<bool> CreateDatabaseAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        var path = PathOf(name);
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Directory.CreateDirectory(dataDirectory);
            if (!File.Exists(path))
            {
                await PublishAsync(name, path, temporary => SqliteDatabaseStore.InitializeAsync(name, temporary, cancellationToken)).ConfigureAwait(false);
                // Published: finish opening even if the caller gives up now.
                await OpenStoreLockedAsync(name, path, CancellationToken.None).ConfigureAwait(false);
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
        var path = PathOf(target);
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Resolved under the lock, so a source deleted concurrently is reported as missing.
            if (await OpenStoreLockedAsync(source, PathOf(source), cancellationToken).ConfigureAwait(false) is not { } store)
            {
                return false;
            }

            if (File.Exists(path))
            {
                throw new InvalidOperationException($"database '{target}' already exists");
            }

            await PublishAsync(target, path, temporary => store.CopyToAsync(temporary, cancellationToken)).ConfigureAwait(false);
            await OpenStoreLockedAsync(target, path, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _creation.Release();
        }
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
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!File.Exists(path))
            {
                return false;
            }

            if (_stores.TryRemove(name, out var store))
            {
                store.Dispose();
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
        // Unlocked fast check; the locked one in OpenStoreLockedAsync decides races with shutdown.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
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

    public async ValueTask DisposeAsync()
    {
        // Under the lock, so no store is published after the stores are closed, and a second call returns
        // only once they are. It waits for a running create, copy or first open to finish. The semaphore is
        // not disposed: a late caller fails with ObjectDisposedException instead of waiting forever.
        await _creation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // Idle sessions keep files open; close them so the data directory can be removed.
            foreach (var store in _stores.Values)
            {
                store.Dispose();
            }

            // No cached store outlives shutdown: opening any database now reaches the disposal check.
            _stores.Clear();
        }
        finally
        {
            _creation.Release();
        }
    }

    /// <summary>
    /// Idle connections of every database: enough for every core to work on one database, and four
    /// times that in total.
    /// </summary>
    internal SqliteSessionPool Pool { get; } = new(maxIdle: 4 * Environment.ProcessorCount, maxIdlePerDatabase: Environment.ProcessorCount);

    private string PathOf(DatabaseName name) => Path.Combine(dataDirectory, name.Value + FileExtension);

    // Returns the open store, or null when the file is gone (a delete won the lock).
    private async ValueTask<SqliteDatabaseStore?> OpenStoreAsync(DatabaseName name, string path, CancellationToken cancellationToken)
    {
        if (_stores.TryGetValue(name, out var store))
        {
            return store;
        }

        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await OpenStoreLockedAsync(name, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _creation.Release();
        }
    }

    // The caller holds _creation.
    private async ValueTask<SqliteDatabaseStore?> OpenStoreLockedAsync(DatabaseName name, string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stores.TryGetValue(name, out var store))
        {
            return store;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        store = new SqliteDatabaseStore(name, path, Pool);
        try
        {
            await store.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Not published, so nothing else would close it: release its sessions and file handles now.
            store.Dispose();
            throw;
        }

        _stores[name] = store;
        return store;
    }
}
