using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using TerraBase.Configuration;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// One SQLite file per database in one directory per tenant,
/// <c>&lt;data-directory&gt;/&lt;organization&gt;/&lt;project&gt;/&lt;database&gt;.db</c>, plus TerraBase's own
/// <c>&lt;data-directory&gt;/_system.db</c>. WAL journal; connections and their prepared statements are pooled per
/// database, so per tenant and database, by <see cref="SqliteDatabaseStore"/>.
/// Write transactions on tenant databases always record change
/// groups, whether or not peers are configured, so a database's change log never has a hole a peer would miss.
/// </summary>
/// <remarks>Every SQLite call runs on one <see cref="SqliteIoPool"/> shared by all databases.</remarks>
internal sealed partial class SqliteStorageEngine : IStorageEngine, IWalBackup
{
    private const string FileExtension = ".db";

    // A readiness probe still on the disk after this long means a stalled volume: not ready.
    private static readonly TimeSpan StalledProbe = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _readinessCacheTtl;
    internal const string SystemName = "_system";
    // Recorded in the lock file: the layout of this build, one directory per tenant.
    private const byte LayoutVersion = 2;
    private const string PreTenancyLayout = "data directory uses the pre-tenancy layout; start with an empty data directory (no automatic migration)";

    /// <summary>The file whose exclusive lock makes a process the only owner of a data directory.</summary>
    internal const string LockFileName = ".terrabase.lock";
    private static readonly string[] Sidecars = ["-wal", "-shm", "-journal"];

    // Opened stores, keyed by tenant and database; a database is bootstrapped once per process.
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), SqliteDatabaseStore> _stores = new();

    // Coordinates file lifecycle and publication. Bootstrap and baseline batches run independently per database.
    private readonly SemaphoreSlim _creation = new(1, 1);
    private readonly Dictionary<(string Organization, string Project, string Database), Task<SqliteDatabaseStore>> _initializing = [];

    // Initializations registered while their caller still holds its creation admission; ExitCreation starts them once it
    // released that admission. Started earlier, an initialization's own admission could overlap the caller's, and
    // storage-queue=1 would refuse it (TS-624). Guarded by _creation.
    private readonly List<TaskCompletionSource> _awaitingRelease = [];

    /// <summary>Initializations waiting for their caller's creation admission to be released, for tests.</summary>
    internal int InitializationsAwaitingRelease => _awaitingRelease.Count;

    /// <summary>Whether a caller holds the creation lock, for tests.</summary>
    internal bool CreationHeld => _creation.CurrentCount == 0;
    private readonly CancellationTokenSource _stopping = new();

    // Fixed-size ULID triples bound both items and bytes; failed intents retain their reservation until recovery.
    private const int MaxPendingIntents = 1000;
    private readonly HashSet<(string Organization, string Project, string Database)> _pendingIntents = [];

    // Held from construction (at startup, before any store opens) until the last connection closes after disposal:
    // another TerraBase process cannot open, create or delete databases in this data directory meanwhile.
    private readonly FileStream _ownership;
    private readonly string _dataDirectory;
    private readonly long _memoryBudget;
    private readonly IMemoryBudget? _schemaMemory;
    private readonly Counter<long> _schemaEvaluations;
    private readonly ILogger<SqliteStorageEngine> _logger;
    private readonly TimeSpan _shutdownGrace;
    private readonly DatabaseLifecycleCheckpoint _checkpoint;
    private readonly Action<SqliteDatabaseStore>? _afterBootstrap;
    private readonly TimeProvider _backupTime;

    /// <summary>Runs every SQLite call of this engine.</summary>
    internal SqliteIoPool Io { get; }
    private readonly string _pragmas;
    private bool _heapInitialized;

    private readonly Lock _disposalGate = new();
    private Task? _disposal;

    // Guarded by _creation.
    private bool _disposed;

    // Published under _creation.
    private SqliteDatabaseStore? _system;
    private readonly bool _systemReplica;

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

    public long? IoAdmittedCount => Io.Closed ? null : Io.Admitted;

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
            // Stream each layout level: one organization's running total and at most ten retained ULIDs.
            // ponytail: metadata is sampled rather than atomic across files; use lifecycle coordination if an exact instant is required.
            RejectLink(_dataDirectory);
            foreach (var organization in new DirectoryInfo(_dataDirectory).EnumerateDirectories())
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
                && ReadinessClock.GetElapsedTime(completed, now) >= _readinessCacheTtl))
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
                Path.Combine(_dataDirectory, $".ready.{EntityIds.New()}.probe"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
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
    internal TimeProvider Time { get; }

    internal TimeSpan QueryBudget { get; }
    internal SqliteSessionPool Pool { get; }

    /// <summary>Owns the directory only after managed setup; failed worker startup rolls back before releasing it.</summary>
    /// <param name="dataDirectory">The data directory this engine owns.</param>
    /// <param name="threads">Dedicated SQLite workers.</param>
    /// <param name="queue">The bound on admitted storage operations.</param>
    /// <param name="memoryBudget">Sizes SQLite caches and its process-wide heap limit.</param>
    /// <param name="meter">Records storage admission and worker metrics.</param>
    /// <param name="logger">Reports shutdown waiting and expired grace.</param>
    /// <param name="shutdownGrace">How long disposal waits; cleanup and ownership continue until actual closure.</param>
    /// <param name="maxIdle">Optional idle session bound.</param>
    /// <param name="maxIdlePerDatabase">Optional per-database idle session bound.</param>
    /// <param name="createWorker">Optional worker factory for construction-failure integration tests; returns an unstarted thread.</param>
    /// <param name="readinessCacheTtl">How long completed filesystem readiness checks are reused.</param>
    /// <param name="checkpoint">A per-host lifecycle checkpoint for integration tests.</param>
    /// <param name="time">The clock of the query deadline; the system clock by default.</param>
    /// <param name="queryBudget">SQLite execution time one query may spend; <see cref="SqliteDatabaseStore.DefaultQueryBudget"/> by default.</param>
    /// <param name="afterBootstrap">Optional integration-test hook before a tenant database's baseline is seeded.</param>
    /// <param name="systemReplica">Whether this node follows a catalog primary: its system database accepts only replicated writes.</param>
    /// <param name="walTaps">Whether continuous backup ships from this node: its <see cref="IWalTap"/>s checkpoint every database, not SQLite.</param>
    /// <param name="backupTime">The host's UTC clock for snapshot boundaries, independent of query deadlines.</param>
    /// <param name="schemaMemory">Shared process admission for collection schema evaluation.</param>
    public SqliteStorageEngine(
        string dataDirectory, int threads, int queue, long memoryBudget, Meter meter, ILogger<SqliteStorageEngine> logger,
        TimeSpan shutdownGrace, int? maxIdle = null, int? maxIdlePerDatabase = null, Func<ThreadStart, Thread>? createWorker = null,
        TimeSpan? readinessCacheTtl = null, DatabaseLifecycleCheckpoint? checkpoint = null, TimeProvider? time = null,
        Action<SqliteDatabaseStore>? afterBootstrap = null, bool systemReplica = false, bool walTaps = false, TimeSpan? queryBudget = null,
        TimeProvider? backupTime = null, IMemoryBudget? schemaMemory = null)
    {
        _systemReplica = systemReplica;
        _dataDirectory = dataDirectory;
        _memoryBudget = memoryBudget;
        _schemaMemory = schemaMemory;
        _schemaEvaluations = meter.CreateCounter<long>("terrabase.schema.keyword_evaluations", description: "Collection schema keyword evaluations started by local writes.");
        _logger = logger;
        _shutdownGrace = shutdownGrace;
        _checkpoint = checkpoint ?? (static (_, _) => { });
        _afterBootstrap = afterBootstrap;
        _backupTime = backupTime ?? TimeProvider.System;
        _readinessCacheTtl = readinessCacheTtl ?? TimeSpan.FromSeconds(1);
        Time = time ?? TimeProvider.System;
        QueryBudget = queryBudget ?? SqliteDatabaseStore.DefaultQueryBudget;
        // Prepare managed state before taking ownership or starting workers.
        _pragmas = SqliteSession.Pragmas(memoryBudget, threads, walTaps);
        Pool = new(maxIdle: maxIdle ?? 4 * Environment.ProcessorCount, maxIdlePerDatabase: maxIdlePerDatabase ?? Environment.ProcessorCount);
        _ownership = Own(dataDirectory);
        try
        {
            // Last construction step: the pool owns rollback of any partially started workers.
            Io = new(threads, queue, meter, createWorker);
        }
        catch
        {
            _ownership.Dispose();
            throw;
        }
    }

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
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
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
            await Io.WithAdmissionAsync(async () =>
            {
                await recordIntent(cancellationToken).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
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
            ExitCreation();
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
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfIntentPending(key);
            created = await CreateFileAsync(key.Database, path, cancellationToken).ConfigureAwait(false);
            if (created)
            {
                _checkpoint("create.published", database);
            }

            initialization = GetInitializationLocked(key, path, cancellationToken, out started);
        }
        finally
        {
            ExitCreation();
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
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
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
            _checkpoint("create.published", target);
            initialization = GetInitializationLocked(key, path, cancellationToken, out _);
        }
        finally
        {
            ExitCreation();
        }

        await initialization.ConfigureAwait(false);
        return true;
    }

    public async ValueTask CompleteDatabaseIntentAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var (key, _) = Locate(database);
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pendingIntents.Remove(key);
        }
        finally
        {
            ExitCreation();
        }
    }

    public async ValueTask<bool> RecordDatabaseDeleteIntentAsync(
        TenantContext database, Func<CancellationToken, ValueTask<bool>> recordIntent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recordIntent);
        var (key, _) = Locate(database);
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pendingIntents.Contains(key))
            {
                return false;
            }

            ReserveIntent(key);
            if (await Io.WithAdmissionAsync(() => recordIntent(cancellationToken)).ConfigureAwait(false))
            {
                return true;
            }

            _pendingIntents.Remove(key);
            return false;
        }
        finally
        {
            ExitCreation();
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
    public async ValueTask<bool> DeleteDatabaseAsync(TenantContext database, CancellationToken cancellationToken, bool retainIntentOwnership = false, Ulid? incarnation = null)
    {
        var (key, path) = Locate(database);
        while (true)
        {
            Task initialization;
            SqliteDatabaseStore? retired = null;
            await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                RejectDatabaseLinks(path);
                if (incarnation is { } expected && (!_stores.TryGetValue(key, out var open) || open.Incarnation != expected))
                {
                    return false;
                }

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
                    await Io.RunAsync(() =>
                    {
                        if (_stores.TryRemove(key, out var store))
                        {
                            retired = store;
                            store.Dispose();
                        }

                        RejectLink(path);
                        DeleteFileIfPresent(path);
                        _checkpoint("delete.main_file", database);
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
                        DeleteIfEmpty(Path.Combine(_dataDirectory, key.Organization));
                    }, CancellationToken.None).ConfigureAwait(false);
                    return existed;
                }

                initialization = pending;
            }
            finally
            {
                ExitCreation();
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
        var root = Path.GetFullPath(_dataDirectory);
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

        var path = Path.Combine(_dataDirectory, SystemName + FileExtension);
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_system is null)
            {
                var created = await CreateFileAsync(SystemName, path, cancellationToken).ConfigureAwait(false);
                // Once published, complete the opening even if cancellation arrives.
                var openingToken = created ? CancellationToken.None : cancellationToken;
                await InitializeHeapAsync(openingToken).ConfigureAwait(false);
                // Every write is captured into its journal for catalog followers (docs/decisions/0009); no engine event.
                system = new SqliteDatabaseStore(SystemName, path, Pool, Io, _pragmas, null, Time, static _ => { }, RecordCommittedWrite, _logger)
                {
                    Replica = _systemReplica,
                    QueryBudget = QueryBudget,
                };
                try
                {
                    await system.BootstrapAsync(openingToken).ConfigureAwait(false);
                }
                catch
                {
                    await Io.RunAsync(system.Dispose, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                Volatile.Write(ref _system, system);
            }

            return _system;
        }
        finally
        {
            ExitCreation();
        }
    }

    public async IAsyncEnumerable<TenantContext> ListDatabasesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        cancellationToken.ThrowIfCancellationRequested();
        // Stream exactly three path components; no sorting or recursive materialization, and no symlink traversal.
        var enumeration = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (var organizationPath in Enumerate(_dataDirectory, directories: true, enumeration))
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

    /// <summary>
    /// Actual shutdown, including session closure, admission drain, worker join and directory release.
    /// Access starts cleanup if necessary. Faults retain every cleanup failure even after disposal's grace expires.
    /// </summary>
    internal Task CleanupCompletion
    {
        get
        {
            lock (_disposalGate)
            {
                return field ??= CleanupAsync();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposalGate)
        {
            // Concurrent callers share the grace wait. Once observed, disposal is idempotent; actual failures
            // remain on CleanupCompletion for callers that need to verify complete resource release.
            return new(_disposal is { IsCompleted: true } ? Task.CompletedTask : (_disposal ??= WaitForCleanupAsync()));
        }
    }

    private async Task WaitForCleanupAsync()
    {
        try
        {
            await CleanupCompletion.WaitAsync(_shutdownGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogShutdownGraceExpired(_logger, (int)_shutdownGrace.TotalSeconds, Pool.OpenCount);
        }
    }

    private async Task CleanupAsync()
    {
        // Refuse new operations immediately, while admitted transactions and multi-step maintenance can finish.
        var drained = Io.DrainAdmissionsAsync();
        if (!drained.IsCompleted)
        {
            LogWaitingForDrain(_logger, Io.Admitted);
        }

        var errors = new List<Exception>();
        Task[] initializations;
        await _creation.WaitAsync().ConfigureAwait(false);
        try
        {
            // No store is published or initialized from here on; running initializations stop at their next step.
            _disposed = true;
            _stopping.Cancel();
            initializations = [.. _initializing.Values];
        }
        finally
        {
            _creation.Release();
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
        // A copy/create holds this through every step. Let it finish before closing its stores.
        await _creation.WaitAsync().ConfigureAwait(false);
        try
        {
            closed = await Io.RunAsync(() =>
            {
                foreach (var store in _stores.Values.Concat(_system is { } system ? [system] : []))
                {
                    try
                    {
                        store.Dispose();
                    }
                    catch (Exception error)
                    {
                        errors.Add(error);
                    }
                }

                _stores.Clear();
                // Includes idle sessions of stores not published in _stores. Active connections stay open,
                // and their returns close them on workers rather than pooling them after shutdown.
                return Pool.StopAsync();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        finally
        {
            _creation.Release();
        }

        try
        {
            if (closed is not null)
            {
                await closed.ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        // Release of the last admission follows pooling/closure, including queued-BEGIN cleanup.
        await drained.ConfigureAwait(false);
        try
        {
            await Io.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        // Even workers that were closing sessions are now gone: only here can another process take over.
        try
        {
            _ownership.Dispose();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        if (errors.Count != 0)
        {
            throw new AggregateException("storage shutdown failed", errors).Flatten();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting for {Count} admitted storage operations before shutdown")]
    private static partial void LogWaitingForDrain(ILogger logger, int count);

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

    // <data-directory>/<organization>/<project>/<database>.db. Every id must be a canonical uppercase ULID, so none can
    // name a parent, another tenant's directory or the system database.
    private ((string Organization, string Project, string Database) Key, string Path) Locate(TenantContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        (string Organization, string Project, string Database) key = (Id(database.OrganizationId), Id(database.ProjectId), Id(database.DatabaseId));
        var path = Path.Combine(_dataDirectory, key.Organization, key.Project, key.Database + FileExtension);
        RejectDatabaseLinks(path);
        return (key, path);
    }

    private string LocateSystem()
    {
        var path = Path.Combine(_dataDirectory, SystemName + FileExtension);
        RejectDatabaseLinks(path);
        return path;
    }

    // Recheck after lifecycle waits/callbacks and before publication/cleanup. The data directory and its parents
    // must remain under trusted operator control; path checks cannot isolate hostile concurrent filesystem writers.
    private void RejectDatabaseLinks(string path)
    {
        RejectLink(_dataDirectory);
        var directory = Path.GetDirectoryName(path)!;
        if (Path.GetRelativePath(_dataDirectory, directory) != ".")
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
            : throw new ArgumentException("a tenant database needs organization, project and database ids that are canonical uppercase ULIDs", nameof(id));

    private static bool IsId([NotNullWhen(true)] string? id) =>
        EntityIds.IsValid(id);

    private static TenantContext? DatabaseFromPath(string root, string path)
    {
        var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
        return parts is [var organization, var project, var name]
            && IsId(organization) && IsId(project) && DatabaseIdFromFileName(name) is { } database
                ? new TenantContext(organization, PlanTier.Hobby, project, database)
                : null;
    }

    // Conservatively recognizes collision shapes; case variants never acquire canonical audit/cleanup ownership.
    private static string? CollisionDatabaseIdFromFileName(string name) => DatabaseIdFromFileName(name, collision: true);

    private static string? DatabaseIdFromFileName(string name, bool collision = false)
    {
        var comparison = collision ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var sidecar in Sidecars)
        {
            if (name.EndsWith(sidecar, comparison))
            {
                name = name[..^sidecar.Length];
                break;
            }
        }

        if (name.EndsWith(FileExtension, comparison))
        {
            var database = name[..^FileExtension.Length];
            database = collision ? database.ToUpperInvariant() : database;
            return IsId(database) ? database : null;
        }

        if (name.Split('.') is not ["", var id, var nonce, var suffix] || !suffix.Equals("creating", comparison))
        {
            return null;
        }

        id = collision ? id.ToUpperInvariant() : id;
        nonce = collision ? nonce.ToUpperInvariant() : nonce;
        return IsId(id) && EntityIds.IsValid(nonce) ? id : null;
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
        await InitializeHeapAsync(cancellationToken).ConfigureAwait(false);
        await PublishAsync(name, path, temporary => SqliteDatabaseStore.InitializeAsync(name, temporary, Io, _pragmas, afterBootstrap: null, cancellationToken)).ConfigureAwait(false);
        return true;
    }

    // Initializes a private temporary file next to its destination and then moves it into place, so a database is
    // visible only once fully initialized and a failed or cancelled creation leaves nothing behind.
    private async Task PublishAsync(string name, string path, Func<string, Task> initialize)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{name}.{EntityIds.New()}.creating");
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

    // The caller holds an admission; native heap-limit initialization also runs on a worker. Racing first
    // calls are harmless: LimitHeap only ever raises the process-wide limit.
    private async Task InitializeHeapAsync(CancellationToken cancellationToken)
    {
        if (!Volatile.Read(ref _heapInitialized))
        {
            await Io.RunAsync(() => SqliteSession.LimitHeap(_memoryBudget), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _heapInitialized, true);
        }
    }

    // Count semaphore waiters toward storage-queue, and release admission even if waiting is cancelled.
    private async ValueTask EnterCreationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Io.Admit();
        try
        {
            await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Io.Release();
            throw;
        }
    }

    private void ExitCreation()
    {
        TaskCompletionSource[] released = _awaitingRelease.Count == 0 ? [] : [.. _awaitingRelease];
        _awaitingRelease.Clear();
        _creation.Release();
        Io.Release();
        foreach (var initialization in released)
        {
            initialization.TrySetResult();
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
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
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
            ExitCreation();
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

        // Bootstrap and seeding run on the I/O pool; keep them off the lifecycle lock. Nested transactions
        // reuse the initialization's own admission, taken only once the caller released its creation admission.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _awaitingRelease.Add(release);
        initialization = Task.Run(
            async () =>
            {
                await release.Task.ConfigureAwait(false);
                return await Io.WithAdmissionAsync(() => InitializeStoreAsync(key, path, cancellationToken)).ConfigureAwait(false);
            },
            CancellationToken.None);
        _initializing.Add(key, initialization);
        started = true;
        _checkpoint("open.registered", new TenantContext(key.Organization, PlanTier.Hobby, key.Project, key.Database));
        return initialization;
    }

    private async ValueTask<SqliteDatabaseStore> InitializeStoreAsync(
        (string Organization, string Project, string Database) key, string path, CancellationToken cancellationToken)
    {
        var tenant = new TenantContext(key.Organization, PlanTier.Hobby, key.Project, key.Database);
        SqliteDatabaseStore? store = null;
        store = new SqliteDatabaseStore(key.Database, path, Pool, Io, _pragmas, tenant, Time,
            sequence => Notify(ChangesCommitted, new ChangeGroupCommitted(store!, sequence), key.Database), RecordCommittedWrite, _logger, _schemaMemory, _schemaEvaluations)
        {
            QueryBudget = QueryBudget,
        };
        var published = false;
        var admitted = false;
        try
        {
            // Held from bootstrap to publication or cleanup, so shutdown drains it and storage-queue bounds it.
            Io.Admit();
            admitted = true;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            RejectDatabaseLinks(path);
            await InitializeHeapAsync(lifetime.Token).ConfigureAwait(false);
            await store.BootstrapAsync(lifetime.Token).ConfigureAwait(false);
            _afterBootstrap?.Invoke(store);
            var (sequence, skipped) = await store.SeedAsync(lifetime.Token).ConfigureAwait(false);
            if (skipped > 0)
            {
                LogBaselineSkipped(_logger, key.Database, skipped);
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
                    // Not published, so nothing else would close it: release its sessions and file handles now.
                    await Io.RunAsync(store.Dispose, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                try
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
                finally
                {
                    if (admitted)
                    {
                        Io.Release();
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
                LogNotificationFailed(_logger, error, name);
            }
        }
    }

    [LoggerMessage(LogLevel.Error, "A change notification for database {Database} failed; the change itself is committed")]
    private static partial void LogNotificationFailed(ILogger logger, Exception error, string database);

    [LoggerMessage(LogLevel.Warning, "Database {Database}: {Skipped} pre-existing states fail replication admission and are not replicated")]
    private static partial void LogBaselineSkipped(ILogger logger, string database, int skipped);
}
