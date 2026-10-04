using System.Collections.Concurrent;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Engine.Sqlite;

/// <summary>
/// One SQLite file per database in one directory per tenant,
/// <c>&lt;data-directory&gt;/&lt;organization&gt;/&lt;project&gt;/&lt;database&gt;.db</c>, plus TerraBase's own
/// <c>&lt;data-directory&gt;/_system.db</c>. WAL journal; connections and their prepared statements are pooled per
/// database, so per tenant and database, by <see cref="SqliteDatabaseStore"/>.
/// </summary>
/// <remarks>
/// ponytail: Microsoft.Data.Sqlite runs async calls synchronously on the caller's thread;
/// move disk work to a dedicated scheduler when request latency under write load matters.
/// </remarks>
/// <param name="dataDirectory">The data directory this engine owns.</param>
/// <param name="shutdownGrace">
/// How long shutdown waits for transactions to return their sessions. One still running after that keeps the data
/// directory owned until it ends (or the process exits); nothing is closed under it.
/// </param>
/// <param name="logger">Reports a shutdown that stopped waiting with connections still open.</param>
internal sealed partial class SqliteStorageEngine(string dataDirectory, TimeSpan shutdownGrace, ILogger<SqliteStorageEngine> logger) : IStorageEngine
{
    private const string FileExtension = ".db";
    internal const string SystemName = "_system";
    // Recorded in the lock file: the layout of this build, one directory per tenant.
    private const byte LayoutVersion = 1;
    private const string PreTenancyLayout = "data directory uses the pre-tenancy layout; start with an empty data directory (no automatic migration)";

    /// <summary>The file whose exclusive lock makes a process the only owner of a data directory.</summary>
    internal const string LockFileName = ".terrabase.lock";
    private static readonly string[] Sidecars = ["-wal", "-shm", "-journal"];

    // Opened (and migrated) stores, keyed by tenant and database; a database is migrated once per process.
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), SqliteDatabaseStore> _stores = new();

    // Serializes every change to the databases and the open stores: create, copy, delete, first open
    // and shutdown. A first open can then neither publish a store for a file a delete removed (its idle
    // session would keep the unlinked file) nor publish one after shutdown closed the stores.
    // ponytail: one lock for all of them (rare, and a database is first opened once per process);
    // per-database locks if creation or startup throughput ever matters.
    private readonly SemaphoreSlim _creation = new(1, 1);

    // Held from construction (at startup, before any store opens) until the last connection closes after disposal:
    // another TerraBase process cannot open, create or delete databases in this data directory meanwhile.
    private readonly FileStream _ownership = Own(dataDirectory);

    // Guarded by _creation.
    private bool _disposed;

    // Published under _creation.
    private SqliteDatabaseStore? _system;

    public string Name => "sqlite";

    /// <summary>
    /// Idle connections of every database: enough for every core to work on one database, and four
    /// times that in total.
    /// </summary>
    internal SqliteSessionPool Pool { get; } = new(maxIdle: 4 * Environment.ProcessorCount, maxIdlePerDatabase: Environment.ProcessorCount);

    public async ValueTask<bool> CreateDatabaseAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var (key, path) = Locate(database);
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (await CreateFileAsync(key.Database, path, cancellationToken).ConfigureAwait(false))
            {
                // Published: finish opening even if the caller gives up now.
                await OpenStoreLockedAsync(key, path, CancellationToken.None).ConfigureAwait(false);
                return true;
            }
        }
        finally
        {
            _creation.Release();
        }

        await OpenStoreAsync(key, path, cancellationToken).ConfigureAwait(false);
        return false;
    }

    public async ValueTask<bool> CopyDatabaseAsync(TenantContext source, TenantContext target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (!string.Equals(source.OrganizationId, target.OrganizationId, StringComparison.Ordinal))
        {
            throw new ArgumentException("a database is copied only within its organization", nameof(target));
        }

        var (sourceKey, sourcePath) = Locate(source);
        var (key, path) = Locate(target);
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Resolved under the lock, so a source deleted concurrently is reported as missing.
            if (await OpenStoreLockedAsync(sourceKey, sourcePath, cancellationToken).ConfigureAwait(false) is not { } store)
            {
                return false;
            }

            if (File.Exists(path))
            {
                throw new InvalidOperationException($"database '{key.Database}' already exists");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await PublishAsync(key.Database, path, temporary => store.CopyToAsync(temporary, cancellationToken)).ConfigureAwait(false);
            await OpenStoreLockedAsync(key, path, CancellationToken.None).ConfigureAwait(false);
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
    public async ValueTask<bool> DeleteDatabaseAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var (key, path) = Locate(database);
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!File.Exists(path))
            {
                return false;
            }

            if (_stores.TryRemove(key, out var store))
            {
                store.Dispose();
            }

            DeleteFiles(path);
            // A tenant's directories go with its last database.
            DeleteIfEmpty(Path.GetDirectoryName(path)!);
            DeleteIfEmpty(Path.Combine(dataDirectory, key.Organization));
            return true;
        }
        finally
        {
            _creation.Release();
        }
    }

    public async ValueTask<IDatabaseStore?> OpenDatabaseAsync(TenantContext database, CancellationToken cancellationToken)
    {
        // Unlocked fast check; the locked one in OpenStoreLockedAsync decides races with shutdown.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        var (key, path) = Locate(database);
        return File.Exists(path) ? await OpenStoreAsync(key, path, cancellationToken).ConfigureAwait(false) : null;
    }

    public async ValueTask<IDatabaseStore> OpenSystemDatabaseAsync(CancellationToken cancellationToken)
    {
        // Like every other store: none is handed out after shutdown, even a cached one.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (Volatile.Read(ref _system) is { } system)
        {
            return system;
        }

        var path = Path.Combine(dataDirectory, SystemName + FileExtension);
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_system is null)
            {
                await CreateFileAsync(SystemName, path, cancellationToken).ConfigureAwait(false);
                system = new SqliteDatabaseStore(SystemName, path, Pool);
                await system.MigrateAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _system, system);
            }

            return _system;
        }
        finally
        {
            _creation.Release();
        }
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
            Task closed;
            try
            {
                // No store rents a session from here on; closing them closes their idle sessions.
                foreach (var store in _stores.Values)
                {
                    store.Dispose();
                }

                _system?.Dispose();
                // No cached store outlives shutdown: opening any database now reaches the disposal check.
                _stores.Clear();
            }
            finally
            {
                // The directory is released by whichever thread closes the last connection, and never before.
                closed = Pool.StopAsync(_ownership.Dispose);
            }

            try
            {
                await closed.WaitAsync(shutdownGrace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Not closed by force, and the lock stays held: the last connection to close releases it, or the
                // operating system does when the process exits.
                LogShutdownGraceExpired(logger, (int)shutdownGrace.TotalSeconds, Pool.OpenCount);
            }
        }
        finally
        {
            _creation.Release();
        }
    }

    // Counts only: no database or tenant names in the log.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown waited {Seconds} s and stopped waiting with {Count} database connections still open; the data directory stays owned until they close or the process exits")]
    private static partial void LogShutdownGraceExpired(ILogger logger, int seconds, int count);

    // Takes the data directory's lock for this process, then checks the layout under it: another process holding the
    // lock, or a directory from before tenancy, stops the start.
    private static FileStream Own(string dataDirectory)
    {
        FileStream ownership;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            // FileShare.None is an exclusive advisory lock (flock on Linux) for as long as the stream is open.
            ownership = new FileStream(Path.Combine(dataDirectory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when (IsLockConflict(error))
        {
            throw new TerraBaseConfigurationException("data-directory is in use by another TerraBase process; stop it, then start again");
        }
        catch (IOException)
        {
            // Not contention (a full disk, a broken lock file): say so rather than blame another process.
            throw new TerraBaseConfigurationException("data-directory must be a directory where this process can create and lock files");
        }
        catch (UnauthorizedAccessException)
        {
            throw new TerraBaseConfigurationException("data-directory must be a directory this process can write to");
        }

        try
        {
            CheckLayout(dataDirectory, ownership);
            return ownership;
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    // The lock file records the layout once a server of this build has started on an empty directory. Files without that
    // record, or a database file directly in the directory other than the system database's, are from before tenancy:
    // refused without opening, changing or moving anything. There is no automatic migration (docs/decisions/0005-multi-tenancy.md).
    private static void CheckLayout(string dataDirectory, FileStream ownership)
    {
        if (ownership.Length > 0 && (ownership.Length != 1 || ownership.ReadByte() != LayoutVersion))
        {
            throw new TerraBaseConfigurationException("data directory has a layout this build does not support");
        }

        var recorded = ownership.Length == 1;
        var entries = Directory.EnumerateFileSystemEntries(dataDirectory)
            .Select(entry => Path.GetFileName(entry))
            .Where(name => !string.Equals(name, LockFileName, StringComparison.Ordinal));
        if (recorded ? entries.Any(IsFlatDatabaseFile) : entries.Any())
        {
            throw new TerraBaseConfigurationException(PreTenancyLayout);
        }

        if (!recorded)
        {
            ownership.WriteByte(LayoutVersion);
            ownership.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    /// Whether opening the lock file failed only because another open stream holds its lock: <c>EWOULDBLOCK</c> from
    /// <c>flock</c> on Linux (the errno is the HResult), a sharing or lock violation on Windows.
    /// </summary>
    internal static bool IsLockConflict(IOException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.GetType() == typeof(IOException) && error.HResult is 11 or unchecked((int)0x80070020) or unchecked((int)0x80070021);
    }

    // <name>.db or one of its journal files directly in the data directory, other than exactly the system database's.
    private static bool IsFlatDatabaseFile(string name) =>
        !string.Equals(name, SystemName + FileExtension, StringComparison.Ordinal)
        && !Sidecars.Any(sidecar => string.Equals(name, SystemName + FileExtension + sidecar, StringComparison.Ordinal))
        && (name.EndsWith(FileExtension, StringComparison.Ordinal)
            || Sidecars.Any(sidecar => name.EndsWith(FileExtension + sidecar, StringComparison.Ordinal)));

    // <data-directory>/<organization>/<project>/<database>.db. Every id must be a lowercase hyphenated UUID, so none can
    // name a parent, another tenant's directory or the system database.
    private ((string Organization, string Project, string Database) Key, string Path) Locate(TenantContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        (string Organization, string Project, string Database) key = (Id(database.OrganizationId), Id(database.ProjectId), Id(database.DatabaseId));
        return (key, Path.Combine(dataDirectory, key.Organization, key.Project, key.Database + FileExtension));
    }

    private static string Id(string? id) =>
        Guid.TryParseExact(id, "D", out var guid) && string.Equals(guid.ToString(), id, StringComparison.Ordinal)
            ? id
            : throw new ArgumentException("a tenant database needs organization, project and database ids that are lowercase hyphenated UUIDs", nameof(id));

    // Creates and initializes the file unless it exists; true when this call created it. The caller holds _creation.
    private static async Task<bool> CreateFileAsync(string name, string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await PublishAsync(name, path, temporary => SqliteDatabaseStore.InitializeAsync(name, temporary, cancellationToken)).ConfigureAwait(false);
        return true;
    }

    // Initializes a private temporary file next to its destination and then moves it into place, so a database is
    // visible only once fully initialized and a failed or cancelled creation leaves nothing behind.
    private static async Task PublishAsync(string name, string path, Func<string, Task> initialize)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{name}.{Guid.NewGuid():N}.creating");
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
        File.Delete(path);
        foreach (var sidecar in Sidecars)
        {
            File.Delete(path + sidecar);
        }
    }

    private static void DeleteIfEmpty(string directory)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best effort: the database is already deleted; a directory that cannot be removed (a file appeared
            // meanwhile, or no permission) stays.
        }
    }

    // Returns the open store, or null when the file is gone (a delete won the lock).
    private async ValueTask<SqliteDatabaseStore?> OpenStoreAsync(
        (string Organization, string Project, string Database) key, string path, CancellationToken cancellationToken)
    {
        if (_stores.TryGetValue(key, out var store))
        {
            return store;
        }

        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await OpenStoreLockedAsync(key, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _creation.Release();
        }
    }

    // The caller holds _creation.
    private async ValueTask<SqliteDatabaseStore?> OpenStoreLockedAsync(
        (string Organization, string Project, string Database) key, string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stores.TryGetValue(key, out var store))
        {
            return store;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        store = new SqliteDatabaseStore(key.Database, path, Pool);
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

        _stores[key] = store;
        return store;
    }
}
