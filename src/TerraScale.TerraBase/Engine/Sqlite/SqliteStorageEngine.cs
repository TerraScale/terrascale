using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Engine.Sqlite;

/// <summary>
/// One SQLite file per database in one directory per tenant,
/// <c>&lt;data-directory&gt;/&lt;organization&gt;/&lt;project&gt;/&lt;database&gt;.db</c>, plus TerraBase's own
/// <c>&lt;data-directory&gt;/_system.db</c>. WAL journal; connections and their prepared statements are pooled per
/// database, so per tenant and database, by <see cref="SqliteDatabaseStore"/>.
/// Write transactions on tenant databases always record change
/// groups, whether or not peers are configured, so a database's change log never has a hole a peer would miss.
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
/// <param name="readinessCacheTtl">How long completed filesystem readiness checks are reused.</param>
/// <param name="checkpoint">A per-host lifecycle checkpoint for integration tests.</param>
/// <param name="time">The clock of the query deadline; the system clock by default.</param>
/// <param name="afterMigration">Optional integration-test hook before a tenant database's baseline is seeded.</param>
internal sealed partial class SqliteStorageEngine(
    string dataDirectory, TimeSpan shutdownGrace, ILogger<SqliteStorageEngine> logger, TimeSpan readinessCacheTtl,
    DatabaseLifecycleCheckpoint checkpoint, TimeProvider? time = null, Action<SqliteDatabaseStore>? afterMigration = null) : IStorageEngine
{
    private const string FileExtension = ".db";

    // A readiness probe still on the disk after this long means a stalled volume: not ready.
    private static readonly TimeSpan StalledProbe = TimeSpan.FromSeconds(2);
    internal const string SystemName = "_system";
    // Recorded in the lock file: the layout of this build, one directory per tenant.
    private const byte LayoutVersion = 1;
    private const string PreTenancyLayout = "data directory uses the pre-tenancy layout; start with an empty data directory (no automatic migration)";

    /// <summary>The file whose exclusive lock makes a process the only owner of a data directory.</summary>
    internal const string LockFileName = ".terrabase.lock";
    private static readonly string[] Sidecars = ["-wal", "-shm", "-journal"];

    // Opened (and migrated) stores, keyed by tenant and database; a database is migrated once per process.
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), SqliteDatabaseStore> _stores = new();

    // Coordinates file lifecycle and publication. Migration and baseline batches run independently per database.
    private readonly SemaphoreSlim _creation = new(1, 1);
    private readonly Dictionary<(string Organization, string Project, string Database), Task<SqliteDatabaseStore>> _initializing = [];
    private readonly CancellationTokenSource _stopping = new();

    // Fixed-size UUID triples bound both items and bytes; failed intents retain their reservation until recovery.
    private const int MaxPendingIntents = 1000;
    private readonly HashSet<(string Organization, string Project, string Database)> _pendingIntents = [];

    // Held from construction (at startup, before any store opens) until the last connection closes after disposal:
    // another TerraBase process cannot open, create or delete databases in this data directory meanwhile.
    private readonly FileStream _ownership = Own(dataDirectory);

    // Guarded by _creation.
    private bool _disposed;
    private Task? _shutdown;

    // Published under _creation.
    private SqliteDatabaseStore? _system;

    // Protects only probe bookkeeping, never disk work or an await. Task state distinguishes idle/completed/running,
    // independently of timestamps (including zero).
    private readonly Lock _readinessGate = new();
    private Task<bool>? _probe;
    private long _probeStarted;
    private long? _probeCompleted;

    // Instance-scoped clock and barrier for deterministic integration checks of a stalled volume over real listeners.
    internal TimeProvider ReadinessClock { get; set; } = TimeProvider.System;

    internal Func<Task>? BeforeReadinessProbe { get; set; }

    internal Task ReadinessProbeTask => Volatile.Read(ref _probe) ?? Task.CompletedTask;

    private static readonly StoragePressure UnknownStoragePressure = new(null, []);
    private StoragePressure _storagePressure = UnknownStoragePressure;
    private long _committedWrites;
    private int _refreshingStoragePressure;

    public string Name => "sqlite";

    public StoragePressure StoragePressure => Volatile.Read(ref _storagePressure);

    public long CommittedWrites => Interlocked.Read(ref _committedWrites);

    public ValueTask RefreshStoragePressureAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref _storagePressure, UnknownStoragePressure);
            cancellationToken.ThrowIfCancellationRequested();
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (Interlocked.CompareExchange(ref _refreshingStoragePressure, 1, 0) != 0)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            var top = new List<StoredTenantPressure>(10);
            long total = 0;
            // Stream each layout level: one organization's running total and at most ten retained UUIDs.
            // ponytail: metadata is sampled rather than atomic across files; use lifecycle coordination if an exact instant is required.
            RejectLink(dataDirectory);
            foreach (var organization in new DirectoryInfo(dataDirectory).EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsId(organization.Name))
                {
                    continue;
                }

                RejectLink(organization.FullName);

                long bytes = 0;
                foreach (var project in organization.EnumerateDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsId(project.Name))
                    {
                        continue;
                    }

                    RejectLink(project.FullName);

                    foreach (var file in project.EnumerateFiles())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (Path.GetExtension(file.Name) is ".db" or ".db-wal" or ".db-shm"
                            && IsId(Path.GetFileNameWithoutExtension(file.Name)))
                        {
                            RejectLink(file.FullName);
                            bytes = checked(bytes + file.Length);
                        }
                    }
                }

                total = checked(total + bytes);
                var index = top.FindIndex(tenant => bytes > tenant.Bytes
                    || (bytes == tenant.Bytes && string.CompareOrdinal(organization.Name, tenant.Org) < 0));
                if (index < 0)
                {
                    index = top.Count;
                }

                if (index < 10)
                {
                    if (top.Count == 10)
                    {
                        top.RemoveAt(9);
                    }

                    top.Insert(index, new StoredTenantPressure(organization.Name, bytes));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _storagePressure, new StoragePressure(total, top.AsReadOnly()));
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref _storagePressure, UnknownStoragePressure);
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OverflowException)
        {
            // Never publish a partial sum as known storage; the next periodic refresh retries from scratch.
            Volatile.Write(ref _storagePressure, UnknownStoragePressure);
        }
        finally
        {
            Volatile.Write(ref _refreshingStoragePressure, 0);
        }

        return ValueTask.CompletedTask;
    }

    private void RecordCommittedWrite()
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _committedWrites);
            if (current == long.MaxValue)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _committedWrites, current + 1, current) != current);
    }

    public async ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<bool> probe;
        TimeSpan remaining;
        lock (_readinessGate)
        {
            if (Volatile.Read(ref _disposed))
            {
                return false;
            }

            var now = ReadinessClock.GetTimestamp();
            if (_probe is null || (_probe.IsCompleted && _probeCompleted is { } completed
                && ReadinessClock.GetElapsedTime(completed, now) >= readinessCacheTtl))
            {
                _probeStarted = now;
                _probeCompleted = null;
                // Off the request thread and outside every storage lock: a stalled volume blocks neither requests nor shutdown.
                _probe = Task.Run(ProbeAsync, CancellationToken.None);
            }

            probe = _probe;
            remaining = StalledProbe - ReadinessClock.GetElapsedTime(_probeStarted, now);
        }

        try
        {
            // Every overlapping caller awaits this same disk task, with the deadline measured from its start.
            var writable = await probe.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ReadinessClock, cancellationToken).ConfigureAwait(false);
            return writable && !Volatile.Read(ref _disposed);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // Creates and removes an empty file in the data directory, then timestamps the completed result.
    private async Task<bool> ProbeAsync()
    {
        try
        {
            if (BeforeReadinessProbe is { } beforeProbe)
            {
                await beforeProbe().ConfigureAwait(false);
            }

            // A unique name, so a leftover never blocks a later probe. Not a database file, so even one left by a crash
            // between create and close never fails the layout check.
            using var probe = new FileStream(
                Path.Combine(dataDirectory, $".ready.{Guid.NewGuid():N}.probe"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            lock (_readinessGate)
            {
                _probeCompleted = ReadinessClock.GetTimestamp();
            }
        }
    }

    public bool SystemDatabaseExists => File.Exists(LocateSystem());

    public event EventHandler<ChangeGroupCommitted>? ChangesCommitted;

    public event EventHandler<IDatabaseStore>? DatabaseDeleted;

    /// <summary>
    /// Idle connections of every database: enough for every core to work on one database, and four
    /// times that in total.
    /// </summary>
    internal TimeProvider Time { get; } = time ?? TimeProvider.System;

    internal SqliteSessionPool Pool { get; } = new(maxIdle: 4 * Environment.ProcessorCount, maxIdlePerDatabase: Environment.ProcessorCount);

    public async ValueTask<bool> CreateDatabaseWithIntentAsync(
        TenantContext database, TenantContext? source, Func<CancellationToken, ValueTask> recordIntent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recordIntent);
        var (key, path) = Locate(database);
        SqliteDatabaseStore? store = null;
        if (source is not null)
        {
            if (!string.Equals(source.OrganizationId, database.OrganizationId, StringComparison.Ordinal))
            {
                throw new ArgumentException("a database is copied only within its organization", nameof(database));
            }

            var (sourceKey, sourcePath) = Locate(source);
            store = await OpenStoreAsync(sourceKey, sourcePath, cancellationToken).ConfigureAwait(false);
            if (store is null)
            {
                return false;
            }
        }

        Task<SqliteDatabaseStore> initialization;
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ = Locate(database);
            ThrowIfIntentPending(key);
            if (File.Exists(path) || Directory.Exists(path)
                || Sidecars.Any(sidecar => File.Exists(path + sidecar) || Directory.Exists(path + sidecar)))
            {
                throw new IOException("a database create target already contains files");
            }

            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
            {
                // ponytail: scan the project on create; index artifacts if large projects need faster provisioning.
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Case variants block creation even on a case-sensitive filesystem; audit and cleanup stay canonical.
                    if (string.Equals(CollisionDatabaseIdFromFileName(Path.GetFileName(entry)), key.Database, StringComparison.Ordinal))
                    {
                        throw new IOException("a database create target already contains files");
                    }
                }
            }

            if (store is { IsClosed: true })
            {
                return false;
            }

            ReserveIntent(key);
            await recordIntent(cancellationToken).ConfigureAwait(false);
            if (store is not null)
            {
                _ = Locate(source!);
                await CopyDatabaseLockedAsync(store, key, path, cancellationToken).ConfigureAwait(false);
            }
            else if (!await CreateFileAsync(key.Database, path, cancellationToken).ConfigureAwait(false))
            {
                throw new IOException("a database create target already contains files");
            }

            // Register under the lifecycle gate before another caller can delete the published file.
            initialization = GetInitializationLocked(key, path, cancellationToken, out _);
        }
        finally
        {
            _creation.Release();
        }

        await initialization.ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> CreateDatabaseAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var (key, path) = Locate(database);
        bool created;
        bool started;
        Task<SqliteDatabaseStore> initialization;
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfIntentPending(key);
            created = await CreateFileAsync(key.Database, path, cancellationToken).ConfigureAwait(false);
            if (created)
            {
                checkpoint("create.published", database);
            }

            initialization = GetInitializationLocked(key, path, cancellationToken, out started);
        }
        finally
        {
            _creation.Release();
        }

        _ = started
            ? await initialization.ConfigureAwait(false)
            : await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        return created;
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
        if (await OpenStoreAsync(sourceKey, sourcePath, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return false;
        }

        Task<SqliteDatabaseStore> initialization;
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfIntentPending(key);
            // A deletion between opening the source and taking the lifecycle lock retires that exact store.
            if (store.IsClosed)
            {
                return false;
            }

            await CopyDatabaseLockedAsync(store, key, path, cancellationToken).ConfigureAwait(false);
            checkpoint("create.published", target);
            initialization = GetInitializationLocked(key, path, cancellationToken, out _);
        }
        finally
        {
            _creation.Release();
        }

        await initialization.ConfigureAwait(false);
        return true;
    }

    public async ValueTask CompleteDatabaseIntentAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var (key, _) = Locate(database);
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pendingIntents.Remove(key);
        }
        finally
        {
            _creation.Release();
        }
    }

    public async ValueTask<bool> RecordDatabaseDeleteIntentAsync(
        TenantContext database, Func<CancellationToken, ValueTask<bool>> recordIntent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recordIntent);
        var (key, _) = Locate(database);
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pendingIntents.Contains(key))
            {
                return false;
            }

            ReserveIntent(key);
            if (await recordIntent(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            _pendingIntents.Remove(key);
            return false;
        }
        finally
        {
            _creation.Release();
        }
    }

    private void ThrowIfIntentPending((string Organization, string Project, string Database) key)
    {
        if (_pendingIntents.Contains(key))
        {
            throw new IOException("a database cleanup intent still owns the target");
        }
    }

    private void ReserveIntent((string Organization, string Project, string Database) key)
    {
        if (!_pendingIntents.Contains(key))
        {
            if (_pendingIntents.Count == MaxPendingIntents)
            {
                throw new IOException("too many unresolved database lifecycle operations");
            }

            _pendingIntents.Add(key);
        }
    }

    /// <remarks>
    /// ponytail: a transaction already running on the deleted database finishes on the unlinked file (Linux)
    /// and new ones fail; add per-database reference counting if deletes must wait for in-flight work.
    /// </remarks>
    public async ValueTask<bool> DeleteDatabaseAsync(TenantContext database, CancellationToken cancellationToken, bool retainIntentOwnership = false)
    {
        var (key, path) = Locate(database);
        while (true)
        {
            Task initialization;
            SqliteDatabaseStore? retired = null;
            await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                RejectDatabaseLinks(path);
                if (retainIntentOwnership)
                {
                    ReserveIntent(key);
                }
                else
                {
                    ThrowIfIntentPending(key);
                }

                if (!_initializing.TryGetValue(key, out var pending))
                {
                    var existed = File.Exists(path);
                    if (_stores.TryRemove(key, out var store))
                    {
                        retired = store;
                        store.Dispose();
                    }

                    RejectLink(path);
                    DeleteFileIfPresent(path);
                    checkpoint("delete.main_file", database);
                    DeleteSidecars(path);
                    var directory = Path.GetDirectoryName(path)!;
                    if (Directory.Exists(directory))
                    {
                        // Only exact private names belonging to this intent's database, including orphaned sidecars.
                        foreach (var file in Directory.EnumerateFiles(directory, $".{key.Database}.*.creating*"))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (string.Equals(DatabaseIdFromFileName(Path.GetFileName(file)), key.Database, StringComparison.Ordinal))
                            {
                                RejectLink(file);
                                DeleteFileIfPresent(file);
                            }
                        }
                    }

                    DeleteIfEmpty(directory);
                    DeleteIfEmpty(Path.Combine(dataDirectory, key.Organization));
                    return existed;
                }

                initialization = pending;
            }
            finally
            {
                _creation.Release();
                if (retired is not null)
                {
                    Notify(DatabaseDeleted, retired, key.Database);
                }
            }

            try
            {
                await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (initialization.IsCompleted && !cancellationToken.IsCancellationRequested)
            {
                // A failed initialization must also release its store before the durable file can be deleted.
            }
        }
    }

    public IEnumerable<StoredDatabaseFile> EnumerateDatabaseFiles(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(dataDirectory);
        RejectLink(root);
        var files = new FileSystemEnumerable<StoredDatabaseFile>(root, (ref entry) =>
        {
            var path = entry.ToFullPath();
            return new StoredDatabaseFile(path,
                !entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0 ? DatabaseFromPath(root, path) : null);
        }, new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false })
        {
            ShouldRecursePredicate = (ref entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (entry.Attributes & FileAttributes.ReparsePoint) == 0;
            },
            ShouldIncludePredicate = (ref entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var link = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                var reservedRootName = entry.Directory.SequenceEqual(entry.RootDirectory)
                    && (entry.FileName.SequenceEqual(LockFileName)
                        || entry.FileName.SequenceEqual(SystemName + FileExtension)
                        || entry.FileName.SequenceEqual(SystemName + FileExtension + "-wal")
                        || entry.FileName.SequenceEqual(SystemName + FileExtension + "-shm")
                        || entry.FileName.SequenceEqual(SystemName + FileExtension + "-journal"));
                return reservedRootName
                    ? entry.IsDirectory || link
                    : !entry.IsDirectory || link || CollisionDatabaseIdFromFileName(entry.FileName.ToString()) is not null;
            },
        };
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    public async ValueTask<IDatabaseStore?> OpenDatabaseAsync(TenantContext database, CancellationToken cancellationToken)
    {
        // Unlocked fast check; OpenStoreAsync checks lifecycle state again under the lock.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        var (key, path) = Locate(database);
        return File.Exists(path) ? await OpenStoreAsync(key, path, cancellationToken).ConfigureAwait(false) : null;
    }

    public ValueTask<long?> GetDatabaseSizeAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var (_, path) = Locate(database);
        var file = new FileInfo(path);
        var wal = new FileInfo(path + "-wal");
        return ValueTask.FromResult<long?>(file.Exists ? file.Length + (wal.Exists ? wal.Length : 0) : null);
    }

    public async ValueTask<IDatabaseStore> OpenSystemDatabaseAsync(CancellationToken cancellationToken)
    {
        // Like every other store: none is handed out after shutdown, even a cached one.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        _ = LocateSystem();
        if (Volatile.Read(ref _system) is { } system)
        {
            return system;
        }

        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_system is null)
            {
                var path = LocateSystem();
                await CreateFileAsync(SystemName, path, cancellationToken).ConfigureAwait(false);
                system = new SqliteDatabaseStore(SystemName, path, Pool, Time);
                try
                {
                    await system.MigrateAsync(cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref _system, system);
                }
                catch
                {
                    system.Dispose();
                    throw;
                }
            }

            return _system;
        }
        finally
        {
            _creation.Release();
        }
    }

    public async IAsyncEnumerable<TenantContext> ListDatabasesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        cancellationToken.ThrowIfCancellationRequested();
        // Stream exactly three path components; no sorting or recursive materialization, and no symlink traversal.
        var enumeration = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (var organizationPath in Enumerate(dataDirectory, directories: true, enumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var organization = Path.GetFileName(organizationPath);
            if (!IsId(organization))
            {
                continue;
            }

            foreach (var projectPath in Enumerate(organizationPath, directories: true, enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var project = Path.GetFileName(projectPath);
                if (!IsId(project))
                {
                    continue;
                }

                foreach (var path in Enumerate(projectPath, directories: false, enumeration))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var database = Path.GetFileNameWithoutExtension(path);
                    if (IsId(database) && string.Equals(Path.GetExtension(path), FileExtension, StringComparison.Ordinal))
                    {
                        yield return new TenantContext(organization, PlanTier.Hobby, project, database);
                    }
                }
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static IEnumerable<string> Enumerate(string directory, bool directories, EnumerationOptions options)
    {
        try
        {
            return directories
                ? Directory.EnumerateDirectories(directory, "*", options)
                : Directory.EnumerateFiles(directory, "*" + FileExtension, options);
        }
        catch (DirectoryNotFoundException)
        {
            // Deleting the last database can remove a tenant directory between listing it and opening it.
            return [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task shutdown;
        var reportFailure = false;
        await _creation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_shutdown is null)
            {
                reportFailure = true;
                _disposed = true;
                _stopping.Cancel();
                var stores = _stores.Values.Append(_system).OfType<SqliteDatabaseStore>().ToArray();
                _stores.Clear();
                // Even connection closure can block. The caller's grace bounds its wait for the
                // entire drain; actual cleanup keeps directory ownership until its last operation ends.
                var initializations = _initializing.Values.ToArray();
                _shutdown = Task.Run(() => FinishShutdownAsync(stores, initializations));
                _ = ObserveShutdownAsync(_shutdown);
            }

            shutdown = _shutdown;
        }
        finally
        {
            _creation.Release();
        }

        try
        {
            await shutdown.WaitAsync(shutdownGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogShutdownGraceExpired(logger, (int)shutdownGrace.TotalSeconds, Pool.OpenCount);
        }
        catch (Exception) when (!reportFailure)
        {
            // The first disposer reports close failures; repeated disposal still waits for shutdown to finish.
        }
    }

    private async Task ObserveShutdownAsync(Task shutdown)
    {
        try
        {
            await shutdown.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            LogShutdownFailed(logger, error);
        }
    }

    private async Task FinishShutdownAsync(SqliteDatabaseStore[] stores, Task[] initializations)
    {
        var failures = new List<Exception>();
        foreach (var store in stores)
        {
            try
            {
                store.Dispose();
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        try
        {
            await Task.WhenAll(initializations).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Initialization callers observe their failures; every initializer disposes its unpublished store.
        }
        finally
        {
            _stopping.Dispose();
        }

        Task? closed = null;
        try
        {
            closed = Pool.StopAsync(_ownership.Dispose);
        }
        catch (Exception error)
        {
            failures.Add(error);
        }

        if (closed is not null)
        {
            await closed.ConfigureAwait(false);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("closing database connections failed", failures).Flatten();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Database shutdown cleanup failed")]
    private static partial void LogShutdownFailed(ILogger logger, Exception error);

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
        var path = Path.Combine(dataDirectory, key.Organization, key.Project, key.Database + FileExtension);
        RejectDatabaseLinks(path);
        return (key, path);
    }

    private string LocateSystem()
    {
        var path = Path.Combine(dataDirectory, SystemName + FileExtension);
        RejectDatabaseLinks(path);
        return path;
    }

    // Recheck after lifecycle waits/callbacks and before publication/cleanup. The data directory and its parents
    // must remain under trusted operator control; path checks cannot isolate hostile concurrent filesystem writers.
    private void RejectDatabaseLinks(string path)
    {
        RejectLink(dataDirectory);
        var directory = Path.GetDirectoryName(path)!;
        if (Path.GetRelativePath(dataDirectory, directory) != ".")
        {
            RejectLink(Path.GetDirectoryName(directory)!);
            RejectLink(directory);
        }

        RejectLink(path);
        foreach (var sidecar in Sidecars)
        {
            RejectLink(path + sidecar);
        }
    }

    private static string Id(string? id) =>
        IsId(id)
            ? id
            : throw new ArgumentException("a tenant database needs organization, project and database ids that are lowercase hyphenated UUIDs", nameof(id));

    private static bool IsId([NotNullWhen(true)] string? id) =>
        Guid.TryParseExact(id, "D", out var guid) && string.Equals(guid.ToString(), id, StringComparison.Ordinal);

    private static TenantContext? DatabaseFromPath(string root, string path)
    {
        var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
        return parts is [var organization, var project, var name]
            && IsId(organization) && IsId(project) && DatabaseIdFromFileName(name) is { } database
                ? new TenantContext(organization, PlanTier.Hobby, project, database)
                : null;
    }

    // Conservatively recognizes collision shapes; case variants never acquire canonical audit/cleanup ownership.
    private static string? CollisionDatabaseIdFromFileName(string name) => DatabaseIdFromFileName(name.ToLowerInvariant());

    private static string? DatabaseIdFromFileName(string name)
    {
        foreach (var sidecar in Sidecars)
        {
            if (name.EndsWith(sidecar, StringComparison.Ordinal))
            {
                name = name[..^sidecar.Length];
                break;
            }
        }

        if (name.EndsWith(FileExtension, StringComparison.Ordinal))
        {
            var database = name[..^FileExtension.Length];
            return IsId(database) ? database : null;
        }

        return name.Split('.') is ["", var id, var nonce, "creating"]
            && IsId(id) && Guid.TryParseExact(nonce, "N", out var guid) && string.Equals(guid.ToString("N"), nonce, StringComparison.Ordinal)
                ? id
                : null;
    }

    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new IOException("a database path must not follow a symbolic link");
        }
    }

    // The caller holds _creation and has resolved the source under it.
    private async ValueTask<bool> CopyDatabaseLockedAsync(
        SqliteDatabaseStore source, (string Organization, string Project, string Database) key, string path, CancellationToken cancellationToken)
    {
        RejectDatabaseLinks(path);
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"database '{key.Database}' already exists");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await PublishAsync(key.Database, path, temporary => source.CopyToAsync(temporary, cancellationToken)).ConfigureAwait(false);
        return true;
    }

    // Creates and initializes the file unless it exists; true when this call created it. The caller holds _creation.
    private async Task<bool> CreateFileAsync(string name, string path, CancellationToken cancellationToken)
    {
        RejectDatabaseLinks(path);
        if (File.Exists(path))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await PublishAsync(name, path, temporary => SqliteDatabaseStore.InitializeAsync(name, temporary, afterMigration: null, cancellationToken)).ConfigureAwait(false);
        return true;
    }

    // Initializes a private temporary file next to its destination and then moves it into place, so a database is
    // visible only once fully initialized and a failed or cancelled creation leaves nothing behind.
    private async Task PublishAsync(string name, string path, Func<string, Task> initialize)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{name}.{Guid.NewGuid():N}.creating");
        try
        {
            RejectDatabaseLinks(temporary);
            await initialize(temporary).ConfigureAwait(false);
            RejectDatabaseLinks(temporary);
            RejectDatabaseLinks(path);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            DeleteFiles(temporary);
        }
    }

    private void DeleteFiles(string path)
    {
        RejectDatabaseLinks(path);
        DeleteFileIfPresent(path);
        DeleteSidecars(path);
    }

    private void DeleteSidecars(string path)
    {
        RejectDatabaseLinks(path);
        foreach (var sidecar in Sidecars)
        {
            RejectLink(path + sidecar);
            DeleteFileIfPresent(path + sidecar);
        }
    }

    private static void DeleteFileIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // Windows distinguishes a missing parent from a missing file; both mean cleanup is complete.
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (_stores.TryGetValue(key, out var store))
        {
            return store;
        }

        Task<SqliteDatabaseStore> initialization;
        var started = false;
        await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RejectDatabaseLinks(path);
            if (_stores.TryGetValue(key, out store))
            {
                return store;
            }

            if (!File.Exists(path))
            {
                return null;
            }

            initialization = GetInitializationLocked(key, path, cancellationToken, out started);
        }
        finally
        {
            _creation.Release();
        }

        // The initiating caller owns cancellation and observes it only after the unpublished store is closed.
        // Other callers can stop waiting without canceling that shared initialization.
        return started
            ? await initialization.ConfigureAwait(false)
            : await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // The caller holds _creation; publication and initialization registration describe the same file incarnation.
    private Task<SqliteDatabaseStore> GetInitializationLocked(
        (string Organization, string Project, string Database) key, string path, CancellationToken cancellationToken, out bool started)
    {
        started = false;
        if (_stores.TryGetValue(key, out var store))
        {
            return Task.FromResult(store);
        }

        if (_initializing.TryGetValue(key, out var initialization))
        {
            return initialization;
        }

        // SQLite async operations perform synchronous disk work; keep them off the lifecycle lock.
        initialization = Task.Run(() => InitializeStoreAsync(key, path, cancellationToken), CancellationToken.None);
        _initializing.Add(key, initialization);
        started = true;
        return initialization;
    }

    private async Task<SqliteDatabaseStore> InitializeStoreAsync(
        (string Organization, string Project, string Database) key, string path, CancellationToken cancellationToken)
    {
        var tenant = new TenantContext(key.Organization, PlanTier.Hobby, key.Project, key.Database);
        SqliteDatabaseStore? store = null;
        store = new SqliteDatabaseStore(key.Database, path, Pool, tenant, Time,
            sequence => Notify(ChangesCommitted, new ChangeGroupCommitted(store!, sequence), key.Database), RecordCommittedWrite);
        var published = false;
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            RejectDatabaseLinks(path);
            await store.MigrateAsync(lifetime.Token).ConfigureAwait(false);
            afterMigration?.Invoke(store);
            var (sequence, skipped) = await store.SeedAsync(lifetime.Token).ConfigureAwait(false);
            if (skipped > 0)
            {
                LogBaselineSkipped(logger, key.Database, skipped);
            }

            await _creation.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                RejectDatabaseLinks(path);
                _stores[key] = store;
                published = true;
                _initializing.Remove(key);
            }
            finally
            {
                _creation.Release();
            }

            if (sequence > 0)
            {
                Notify(ChangesCommitted, new ChangeGroupCommitted(store, sequence), key.Database);
            }

            return store;
        }
        finally
        {
            try
            {
                if (!published)
                {
                    store.Dispose();
                }
            }
            finally
            {
                if (!published)
                {
                    await _creation.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        _initializing.Remove(key);
                    }
                    finally
                    {
                        _creation.Release();
                    }
                }
            }
        }
    }

    // Subscribers run after the operation is durable: their failure must not turn it into a failed request.
    private void Notify<T>(EventHandler<T>? handlers, T notification, string name)
    {
        foreach (var subscriber in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                subscriber(this, notification);
            }
            catch (Exception error)
            {
                LogNotificationFailed(logger, error, name);
            }
        }
    }

    [LoggerMessage(LogLevel.Error, "A change notification for database {Database} failed; the change itself is committed")]
    private static partial void LogNotificationFailed(ILogger logger, Exception error, string database);

    [LoggerMessage(LogLevel.Warning, "Database {Database}: {Skipped} pre-existing states fail replication admission and are not replicated")]
    private static partial void LogBaselineSkipped(ILogger logger, string database, int skipped);
}
