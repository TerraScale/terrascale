using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using TerraBase.Configuration;
using TerraBase.Engine.Revisions;
using TerraBase.Features.Backup;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine : IStorageEngine, IWalBackup
{
    internal IDiskBudget Disk { get; }
    private const string FileExtension = ".db";

    private static readonly TimeSpan StalledProbe = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _readinessCacheTtl;
    internal const string SystemName = "_system";

    private static readonly byte[] LayoutRecord = Encoding.ASCII.GetBytes(StorageFormat.Id);
    private static readonly string PreTenancyLayout =
        $"data directory uses the pre-tenancy layout, older than storage format {StorageFormat.SupportedFrom}; this build supports storage formats {StorageFormat.SupportedFrom} to {StorageFormat.SupportedTo} and cannot open it";

    internal const string LockFileName = ".terrabase.lock";
    private static readonly string[] Sidecars = ["-wal", "-shm", "-journal", DatabaseKeys.Suffix, RevisionManifestFile.Suffix, RevisionManifestFile.StagedSuffix, ".deleting"];

    private static readonly string[] ReservedRootNames =
        [LockFileName, .. new[] { SystemName, IntentName }.SelectMany(database => Sidecars.Prepend(string.Empty).Select(suffix => database + FileExtension + suffix))];

    private static readonly string[] ScratchDirectories = [".tmp", ".backup", ".import", ".exports"];
    internal DatabaseKeys Encryption { get; }

    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), TursoDatabaseStore> _stores = new();

    private readonly SemaphoreSlim _creation = new(1, 1);
    private readonly Dictionary<(string Organization, string Project, string Database), Task<TursoDatabaseStore>> _initializing = [];

    private const int MaxPendingPublications = 1000;
    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), Ulid> _publishing = new();

    private readonly List<TaskCompletionSource> _awaitingRelease = [];

    private readonly CancellationTokenSource _stopping = new();

    private const int MaxPendingIntents = 1000;
    private readonly HashSet<(string Organization, string Project, string Database)> _pendingIntents = [];

    private readonly FileStream _ownership;
    private readonly string _dataDirectory;
    private readonly IMemoryBudget? _schemaMemory;
    private readonly Counter<long> _storageReads;

    private static readonly double[] WriterHeldBuckets = [0.001, 0.005, 0.01, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60];
    private readonly Histogram<double> _writerHeld;
    private readonly Counter<long> _transactions;
    private readonly Histogram<double> _rewriteSteps;
    private readonly Counter<long> _rewriteRefused;
    private readonly Counter<long> _writersEvicted;
    private readonly ILogger<TursoStorageEngine> _logger;
    private readonly TimeSpan _shutdownGrace;
    private readonly DatabaseLifecycleCheckpoint _checkpoint;

    internal TursoIoPool Io { get; }

    internal TursoDatabaseStore? SystemStore => Volatile.Read(ref _system);

    internal StorageFaultLedger Ledger { get; }

    internal DatabaseFences Fences { get; }
    internal HomeLeaseGate Leases { get; }

    internal void Checkpoint(string step, TenantContext database) => _checkpoint(step, database);

    internal TursoDatabaseStore? OpenStore(StorageFaultLedger.Key key) =>
    _stores.TryGetValue((key.Organization, key.Project, key.Database), out var store) ? store : null;

    internal Ulid? KnownIncarnation(StorageFaultLedger.Key key) =>
        _publishing.TryGetValue((key.Organization, key.Project, key.Database), out var incarnation) ? incarnation : OpenStore(key)?.Incarnation;
    private readonly string _pragmas;

    private readonly Lock _disposalGate = new();
    private Task? _disposal;

    private bool _disposed;

    private TursoDatabaseStore? _system;

    private readonly Lock _readinessGate = new();
    private Task<bool>? _probe;
    private long _probeStarted;
    private long? _probeCompleted;

    private static readonly StoragePressure UnknownStoragePressure = new(null, []);
    private StoragePressure _storagePressure = UnknownStoragePressure;
    private long _committedWrites;
    private int _refreshingStoragePressure;

    public string Name => "turso";

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
            if (!Volatile.Read(ref _recovered) || Volatile.Read(ref _disposed) || Ledger.FailedClosed is not null)
            {
                return false;
            }

            var now = Time.GetTimestamp();
            if (_probe is null || (_probe.IsCompleted && _probeCompleted is { } completed
                && Time.GetElapsedTime(completed, now) >= _readinessCacheTtl))
            {
                _probeStarted = now;
                _probeCompleted = null;

                _probe = Task.Run(ProbeAsync, CancellationToken.None);
            }

            probe = _probe;
            remaining = StalledProbe - Time.GetElapsedTime(_probeStarted, now);
        }

        try
        {

            var writable = await probe.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Time, cancellationToken).ConfigureAwait(false);
            return writable && !Volatile.Read(ref _disposed);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task<bool> ProbeAsync()
    {
        try
        {
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
                _probeCompleted = Time.GetTimestamp();
            }
        }
    }

    public bool SystemDatabaseExists => File.Exists(LocateSystem());

    public event EventHandler<IDatabaseStore>? DatabaseDeleted;

    internal TimeProvider Time { get; }

    internal TimeProvider HostTime { get; }

    internal TimeSpan QueryBudget { get; }
    internal TursoSessionPool Pool { get; }

    public TursoStorageEngine(
    StorageEngineOptions configured, Meter meter, ILogger<TursoStorageEngine> logger, IMasterKey masterKey,
    DatabaseLifecycleCheckpoint? checkpoint = null, IMemoryBudget? schemaMemory = null,
    INativeTemporaryStorage? nativeTemporaryStorage = null, IDiskBudget? diskBudget = null)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        var clock = configured.Time ?? TimeProvider.System;
        Encryption = new DatabaseKeys(masterKey);
        _dataDirectory = configured.DataDirectory;
        Fences = new DatabaseFences(Path.Combine(configured.DataDirectory, ".fences"), clock);
        Disk = diskBudget ?? new DiskBudget(configured.DataDirectory, meter);
        _schemaMemory = schemaMemory;
        _storageReads = meter.CreateCounter<long>("terrabase.storage.reads", description: "Instrumented record and shape SELECT executions, by path (point, query, shape, shape_body).");
        _writerHeld = meter.CreateHistogram<double>("terrabase.storage.writer_held", "s",
            "How long a database's writer was held, by holder kind and call site (bounded sets of names and file:line).",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = WriterHeldBuckets });
        _transactions = meter.CreateCounter<long>("terrabase.storage.transactions",
            description: "Storage transactions begun, by kind (read, write) and call site (a bounded set of file:line): which code keeps the I/O threads and writers busy.");
        _writersEvicted = meter.CreateCounter<long>("terrabase.storage.writers_evicted", description: "Database writers closed because the engine-wide bound on open writers was reached.");
        _rewriteRefused = meter.CreateCounter<long>("terrabase.storage.rewrite_refused", description: "Healthy rewrite refusals by bounded reason.");
        _rewriteSteps = meter.CreateHistogram<double>("terrabase.storage.rewrite_step", "s",
            "Healthy rewrite work by bounded step and database kind.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = WriterHeldBuckets });
        _migrationFailures = meter.CreateCounter<long>("terrabase.storage.migration_failures", description: "Tenant files that failed the startup migration pass.");
        meter.CreateObservableGauge("terrabase.storage.old_format_files", () => _oldFormatPaths.Count,
            description: "Tenant files still awaiting a successful format migration.");
        _logger = logger;
        _shutdownGrace = configured.ShutdownGrace;
        _checkpoint = checkpoint ?? (static (_, _) => { });
        HostTime = configured.BackupTime ?? clock;
        _readinessCacheTtl = configured.ReadinessCacheTtl ?? TimeSpan.FromSeconds(1);
        Time = clock;
        QueryBudget = configured.QueryBudget ?? TursoDatabaseStore.DefaultQueryBudget;

        _pragmas = TursoSession.Pragmas(configured.MemoryBudget, configured.Threads);
        Pool = new(maxIdle: 4 * Environment.ProcessorCount, maxIdlePerDatabase: Environment.ProcessorCount,
            maxWriters: configured.MaxWriters ?? Math.Max(64, 8 * configured.Threads));
        meter.CreateObservableGauge("terrabase.cdc.retained.rows", () => _stores.Values.Sum(store => store.RetainedCdcRows),
            description: "Retained CDC row span across open tenant databases, sampled by retention maintenance.");
        meter.CreateObservableGauge("terrabase.storage.open_writers", () => Pool.OpenWriters, description: "Client databases holding an open writer connection.");
        _ownership = Own(configured.DataDirectory);
        try
        {
            nativeTemporaryStorage?.Initialize();
            RecoverPublications();
            CheckIntentJournal();
            DeleteRewriteScratch();
            Ledger = new StorageFaultLedger(this, logger, schemaMemory);
            meter.CreateObservableGauge("terrabase.storage.faulted.databases", () => Ledger.FaultCount);
            meter.CreateObservableGauge("terrabase.storage.proof.pending", () => Ledger.PendingCount);
            Ledger.IncompleteProofs = meter.CreateCounter<long>("terrabase.storage.proof.incomplete");
            Leases = new HomeLeaseGate(HostTime);

            Io = new(configured.Threads, configured.Queue, meter);
        }
        catch
        {
            _ownership.Dispose();
            throw;
        }
    }

    public async ValueTask<bool> CreateDatabaseWithIntentAsync(
        TenantContext database, TenantContext? source, Func<CancellationToken, ValueTask<bool>> recordIntent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recordIntent);
        var (key, path) = Locate(database);
        TursoDatabaseStore? store = null;
        TursoDatabaseStore.CopyOperation? copyOperation = null;
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

            copyOperation = await store.PrepareCopyAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var sourceOperation = copyOperation;

        copyOperation?.Complete();

        Task<TursoDatabaseStore> initialization;
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ = Locate(database);
            await ThrowIfPurgeFencedAsync(key.Database, cancellationToken).ConfigureAwait(false);
            ThrowIfIntentPending(key);
            if (HoldsFiles(path, key.Database, cancellationToken))
            {
                throw new IOException("a database create target already contains files");
            }

            if (store is { IsClosed: true })
            {
                return false;
            }

            if (store is not null)
            {

                ThrowIfCopyRefused(store);
            }

            ReserveIntent(key);
            if (!await Io.WithAdmissionAsync(() => recordIntent(cancellationToken)).ConfigureAwait(false))
            {
                _pendingIntents.Remove(key);
                return false;
            }
            if (store is not null)
            {
                _ = Locate(source!);
                await CopyDatabaseLockedAsync(store, key, path, cancellationToken).ConfigureAwait(false);
            }
            else if (!await CreateFileAsync(key.Database, path, cancellationToken, key).ConfigureAwait(false))
            {
                throw new IOException("a database create target already contains files");
            }

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
        Task<TursoDatabaseStore> initialization;
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ThrowIfPurgeFencedAsync(key.Database, cancellationToken).ConfigureAwait(false);
            ThrowIfIntentPending(key);
            created = await CreateFileAsync(key.Database, path, cancellationToken, key).ConfigureAwait(false);
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

        await using var sourceOperation = await store.PrepareCopyAsync(cancellationToken).ConfigureAwait(false);
        sourceOperation?.Complete();
        Task<TursoDatabaseStore> initialization;
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ThrowIfPurgeFencedAsync(key.Database, cancellationToken).ConfigureAwait(false);
            ThrowIfIntentPending(key);

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

    public async ValueTask<bool> DeleteDatabaseAsync(TenantContext database, CancellationToken cancellationToken, bool retainIntentOwnership = false, Ulid? incarnation = null)
    {
        var (key, path) = Locate(database);
        if (SystemDatabaseExists)
        {

            _ = await OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            Task initialization;
            TursoDatabaseStore? retired = null;
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

                var pending = (Task?)_initializing.GetValueOrDefault(key) ?? _migrating.GetValueOrDefault(key);
                if (pending is null)
                {
                    var existed = File.Exists(path);
                    await Io.RunAsync(() =>
                    {
                        if (_stores.TryRemove(key, out var store))
                        {
                            retired = store;
                            store.Dispose();
                        }

                        return true;
                    }, CancellationToken.None).ConfigureAwait(false);
                    await Io.RunAsync(() =>
                    {
                        RejectLink(path);
                        if (Directory.Exists(Path.GetDirectoryName(path)!))
                        {
                            using var deleting = new FileStream(path + ".deleting", FileMode.Create, FileAccess.Write, FileShare.None);
                            deleting.Flush(flushToDisk: true);
                        }
                        SyncExistingDirectory(Path.GetDirectoryName(path)!);
                        DeleteFileIfPresent(path);
                        SyncExistingDirectory(Path.GetDirectoryName(path)!);
                        _checkpoint("delete.main_file", database);
                        DeleteSidecars(path);
                        var directory = Path.GetDirectoryName(path)!;
                        if (Directory.Exists(directory))
                        {

                            foreach (var file in Directory.EnumerateFiles(directory, $".{key.Database}.*.creating*")
                                .Concat(Directory.EnumerateFiles(directory, $".{key.Database}.*.{RewriteSuffix}*")))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (string.Equals(DatabaseIdFromFileName(Path.GetFileName(file)), key.Database, StringComparison.Ordinal))
                                {
                                    RejectLink(file);
                                    DeleteFileIfPresent(file);
                                }
                            }

                        }

                        SyncExistingDirectory(directory);
                        return true;
                    }, CancellationToken.None).ConfigureAwait(false);
                    _ = _publishing.TryRemove(key, out _);

                    _ = await Io.WithAdmissionAsync(async () =>
                    {
                        await Ledger.ForgetAsync(database, CancellationToken.None).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
                    await Io.RunAsync(() =>
                    {
                        DeleteIfEmpty(Path.GetDirectoryName(path)!);
                        SyncExistingDirectory(Path.Combine(_dataDirectory, key.Organization));
                        DeleteIfEmpty(Path.Combine(_dataDirectory, key.Organization));
                        DirectorySync(_dataDirectory);
                        return true;
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
                await initialization.WaitAsync(QueryBudget, Time, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (initialization.IsCompleted && !cancellationToken.IsCancellationRequested)
            {

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
                if (entry.Directory.SequenceEqual(entry.RootDirectory))
                {
                    foreach (var scratch in ScratchDirectories)
                    {
                        if (entry.FileName.SequenceEqual(scratch))
                        {
                            return false;
                        }
                    }
                }

                return (entry.Attributes & FileAttributes.ReparsePoint) == 0;
            },
            ShouldIncludePredicate = (ref entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var link = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                var reservedRootName = entry.Directory.SequenceEqual(entry.RootDirectory) && IsReservedRootName(ref entry);
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

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        await Ledger.ThrowIfUnavailableAsync(database, cancellationToken).ConfigureAwait(false);
        if (_stores.TryGetValue(Key(database), out var open))
        {
            open.ThrowIfUnavailable();
            return open.FilePresent(Volatile.Read(ref _fileEpoch)) ? open : null;
        }

        var (key, path) = Place(database);
        RejectDatabaseLinks(path);
        return File.Exists(path) ? await OpenStoreAsync(key, path, cancellationToken).ConfigureAwait(false) : null;
    }

    public bool HasDatabaseFiles(TenantContext database, CancellationToken cancellationToken)
    {
        var (key, path) = Locate(database);
        return HoldsFiles(path, key.Database, cancellationToken);
    }

    private static bool HoldsFiles(string path, string database, CancellationToken cancellationToken)
    {
        if (File.Exists(path) || Directory.Exists(path)
            || Sidecars.Any(sidecar => File.Exists(path + sidecar) || Directory.Exists(path + sidecar)))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(CollisionDatabaseIdFromFileName(Path.GetFileName(entry)), database, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        if (Volatile.Read(ref _system) is { } system)
        {
            return system;
        }

        var path = LocateSystem();
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_system is null)
            {
                TursoDatabaseStore? openingSystem = null;
                try
                {
                    await PrepareStorageMigrationAsync(cancellationToken).ConfigureAwait(false);
                    _ = await Io.WithAdmissionAsync(async () =>
                    {
                        await OpenIntentStoreLockedAsync(cancellationToken).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
                    var created = await CreateFileAsync(SystemName, path, cancellationToken).ConfigureAwait(false);

                    var openingToken = created ? CancellationToken.None : cancellationToken;
                    openingSystem = CreateSystemStore(path);
                    await openingSystem.BootstrapAsync(openingToken).ConfigureAwait(false);

                    _ = await Io.WithAdmissionAsync(async () =>
                    {
                        await Ledger.RecoverAsync(openingSystem, openingToken).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
                    Volatile.Write(ref _system, openingSystem);
                    Ledger.StartRetries();

                }
                catch
                {
                    try
                    {
                        await CloseIntentStoreAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        if (openingSystem is not null)
                        {
                            await Io.RunAsync(openingSystem.Dispose, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    throw;
                }
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

            return [];
        }
    }

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

            _logger.ZLogWarning($"Shutdown waited {(int)_shutdownGrace.TotalSeconds:@Seconds} s and stopped waiting with {Pool.OpenCount:@Count} database connections still open; the data directory stays owned until they close or the process exits");
        }
    }

    private async Task CleanupAsync()
    {
        await Ledger.StopRetriesAsync().ConfigureAwait(false);
        var drained = Io.DrainAdmissionsAsync();
        if (!drained.IsCompleted)
        {
            _logger.ZLogInformation($"Waiting for {Io.Admitted:@Count} admitted storage operations before shutdown");
        }

        var errors = new List<Exception>();
        Task[] initializations;
        await _creation.WaitAsync().ConfigureAwait(false);
        try
        {

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

        }
        finally
        {
            _stopping.Dispose();
        }

        await drained.ConfigureAwait(false);

        Task? closed = null;
        List<string> files = [];

        await _creation.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var store in _stores.Values.Concat(_system is { } closingSystem ? [closingSystem] : []))
            {
                try
                {
                    _ = await Io.WithAdmissionAsync(async () =>
                    {
                        await store.CheckpointOnCloseAsync().ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
            if (errors.Count == 0 && Ledger.FailedClosed is null)
            {
                try
                {
                    _ = await Io.WithAdmissionAsync(async () =>
                    {
                        await Ledger.ClearRunAsync().ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }

            closed = await Io.RunAsync(() =>
            {
                foreach (var store in _stores.Values.Concat(_system is { } system ? [system] : [])
                    .Concat(_intents is { } intents ? [intents] : []))
                {
                    if (store.CloseRefusal is null)
                    {
                        files.Add(store.FilePath);
                    }
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

                foreach (var file in files)
                {
                    if (new FileInfo(file + "-wal") is { Exists: true, Length: 0 } log)
                    {
                        log.Delete();
                    }
                }
            }
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        try
        {
            await Io.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        Ledger.Dispose();

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


    private ((string Organization, string Project, string Database) Key, string Path) Locate(TenantContext database)
    {
        var located = Place(database);
        RejectDatabaseLinks(located.Path);
        return located;
    }

    private ((string Organization, string Project, string Database) Key, string Path) Place(TenantContext database)
    {
        var key = Key(database);
        return (key, Path.Combine(_dataDirectory, key.Organization, key.Project, key.Database + FileExtension));
    }

    private static (string Organization, string Project, string Database) Key(TenantContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return (Id(database.OrganizationId), Id(database.ProjectId), Id(database.DatabaseId));
    }

    private string LocateSystem()
    {
        var path = Path.Combine(_dataDirectory, SystemName + FileExtension);
        RejectDatabaseLinks(path);
        return path;
    }

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

        if (name.Split('.') is not ["", var id, var nonce, var suffix]
            || !(suffix.Equals("creating", comparison) || suffix.Equals(RewriteSuffix, comparison)))
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

    private async ValueTask<bool> CopyDatabaseLockedAsync(
        TursoDatabaseStore source, (string Organization, string Project, string Database) key, string path, CancellationToken cancellationToken)
    {
        RejectDatabaseLinks(path);
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"database '{key.Database}' already exists");
        }

        using var disk = Disk.Reserve(DiskBudget.WorkspaceBytes(DiskBudget.FileBytes(source.FilePath)));
        ThrowIfCopyRefused(source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ExceptionDispatchInfo? copyError = null;
        try
        {
            await PublishFreshFileAsync(key.Database, path, async temporary =>
            {
                Ulid incarnation;
                try
                {
                    incarnation = await source.CopyToAsync(temporary, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    copyError = ExceptionDispatchInfo.Capture(error);
                    throw;
                }

                _ = await source.WithWritersHeldAsync(() =>
                {
                    ThrowIfCopyRefused(source);
                    return Task.FromResult(true);
                }, cancellationToken, "a copy checking its source").ConfigureAwait(false);
                return incarnation;
            }, key).ConfigureAwait(false);
        }
        catch (Exception) when (copyError is not null)
        {

            copyError.Throw();
            throw;
        }
        return true;
    }

    private void ThrowIfCopyRefused(TursoDatabaseStore source)
    {
        if (Ledger.Refusal(source) is { } refusal)
        {
            throw new StorageCopyFaultedException(refusal);
        }
    }

    private async Task<bool> CreateFileAsync(
        string name, string path, CancellationToken cancellationToken, (string Organization, string Project, string Database)? key = null)
    {
        RejectDatabaseLinks(path);
        if (File.Exists(path))
        {
            return false;
        }

        if (Sidecars.Any(sidecar => File.Exists(path + sidecar) || Directory.Exists(path + sidecar)))
        {

            throw new IOException("a database create target already contains files");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (key is { } publishing)
        {
            await PublishFreshFileAsync(name, path,
                temporary => TursoDatabaseStore.InitializeAsync(name, temporary, Io, _pragmas, Encryption, cancellationToken), publishing).ConfigureAwait(false);
        }
        else
        {
            await PublishAsync(name, path, temporary => TursoDatabaseStore.InitializeAsync(name, temporary, Io, _pragmas, Encryption, cancellationToken))
                .ConfigureAwait(false);
        }
        return true;
    }

    private Task PublishFreshFileAsync(
        string name, string path, Func<string, Task<Ulid>> initialize, (string Organization, string Project, string Database) key) =>
        PublishAsync(name, path, async temporary =>
        {
            var incarnation = await initialize(temporary).ConfigureAwait(false);

            if (_publishing.Count >= MaxPendingPublications)
            {
                throw new StorageBusyException();
            }

            _publishing[key] = incarnation;
        }, key);

    private async Task PublishAsync(
        string name, string path, Func<string, Task> initialize, (string Organization, string Project, string Database)? key = null)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{name}.{EntityIds.New()}.creating");
        var published = false;
        try
        {
            RejectDatabaseLinks(temporary);
            await initialize(temporary).ConfigureAwait(false);
            RejectDatabaseLinks(temporary);
            RejectDatabaseLinks(path);
            await ThrowIfPurgeFencedAsync(name, CancellationToken.None).ConfigureAwait(false);
            await Io.RunAsync(() =>
            {
                foreach (var file in new[] { temporary, temporary + DatabaseKeys.Suffix })
                {
                    using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.ReadWrite);
                    RandomAccess.FlushToDisk(handle);
                }

                File.Move(temporary + DatabaseKeys.Suffix, path + DatabaseKeys.Suffix, overwrite: false);
                SyncPublicationDirectory(Path.GetDirectoryName(path)!);
                File.Move(temporary, path, overwrite: false);
                DirectorySync(Path.GetDirectoryName(path)!);
            }, CancellationToken.None).ConfigureAwait(false);
            published = true;
        }
        finally
        {
            if (!published && key is { } publication)
            {
                _ = _publishing.TryRemove(publication, out _);
            }

            await Io.RunAsync(() =>
            {
                DeleteFiles(temporary);
                if (!File.Exists(path))
                {
                    DeleteSidecars(path);
                }
                DirectorySync(Path.GetDirectoryName(path)!);
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void DeleteFiles(string path)
    {
        RejectDatabaseLinks(path);
        DeleteFileIfPresent(path);
        DeleteSidecars(path);
    }

    private void DeleteSidecars(string path, bool keepKey = false)
    {
        RejectDatabaseLinks(path);
        foreach (var sidecar in Sidecars.Where(sidecar => !keepKey || sidecar != DatabaseKeys.Suffix))
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

        }
    }

    private long _fileEpoch;

    private async ValueTask EnterCreationAsync(CancellationToken cancellationToken, bool admitted = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!admitted)
        {
            Io.Admit();
        }
        try
        {
            await _creation.WaitAsync(cancellationToken).ConfigureAwait(false);

            Interlocked.Increment(ref _fileEpoch);
        }
        catch
        {
            if (!admitted)
            {
                Io.Release();
            }
            throw;
        }
    }

    private void ExitCreation(bool admitted = false)
    {
        TaskCompletionSource[] released = _awaitingRelease.Count == 0 ? [] : [.. _awaitingRelease];
        _awaitingRelease.Clear();
        Interlocked.Increment(ref _fileEpoch);
        _creation.Release();
        if (!admitted)
        {
            Io.Release();
        }
        foreach (var initialization in released)
        {
            initialization.TrySetResult();
        }
    }

    private async ValueTask<TursoDatabaseStore?> OpenStoreAsync(
        (string Organization, string Project, string Database) key, string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (_stores.TryGetValue(key, out var store))
        {
            return store;
        }

        Task<TursoDatabaseStore> initialization;
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

        return started
            ? await initialization.ConfigureAwait(false)
            : await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<TursoDatabaseStore> GetInitializationLocked(
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

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _awaitingRelease.Add(release);
        var migration = _migrating.GetValueOrDefault(key) ?? Task.CompletedTask;
        initialization = Task.Run(
            async () =>
            {
                await release.Task.ConfigureAwait(false);
                return await Io.WithAdmissionAsync(() => InitializeStoreAsync(key, path, migration, cancellationToken)).ConfigureAwait(false);
            },
            CancellationToken.None);
        _initializing.Add(key, initialization);
        started = true;
        _checkpoint("open.registered", new TenantContext(key.Organization, PlanTier.Hobby, key.Project, key.Database));
        return initialization;
    }

    private TursoDatabaseStore CreateSystemStore(string path) =>
        new(SystemName, path, Pool, Io, _pragmas, null, Time, RecordCommittedWrite, _logger, _schemaMemory, _storageReads, Encryption)
        {
            HostTime = HostTime,
            QueryBudget = QueryBudget,
            Checkpoint = _checkpoint,
            Ledger = Ledger,
            WriterHeld = _writerHeld,
            Transactions = _transactions,
            RewriteSteps = _rewriteSteps,
        };

    private TursoDatabaseStore CreateTenantStore((string Organization, string Project, string Database) key, string path) =>
        new(key.Database, path, Pool, Io, _pragmas, new TenantContext(key.Organization, PlanTier.Hobby, key.Project, key.Database), Time,
            RecordCommittedWrite, _logger, _schemaMemory, _storageReads, Encryption)
        {
            HostTime = HostTime,
            Fences = Fences,
            QueryBudget = QueryBudget,
            Checkpoint = _checkpoint,
            Ledger = Ledger,
            WriterHeld = _writerHeld,
            Transactions = _transactions,
            RewriteSteps = _rewriteSteps,
            WritersEvicted = _writersEvicted,
            Revised = RaiseRevised,
            Leases = Leases,
        };

    private async ValueTask<TursoDatabaseStore> InitializeStoreAsync(
        (string Organization, string Project, string Database) key, string path, Task migration, CancellationToken cancellationToken)
    {
        var store = CreateTenantStore(key, path);
        var published = false;
        var admitted = false;
        try
        {

            Io.Admit();
            admitted = true;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            await migration.WaitAsync(QueryBudget, Time, lifetime.Token).ConfigureAwait(false);
            RejectDatabaseLinks(path);
            await Ledger.LoadAsync(store, lifetime.Token).ConfigureAwait(false);
            store.Settled = true;
            store.ThrowIfUnavailable();
            await store.BootstrapAsync(lifetime.Token).ConfigureAwait(false);
            _ = _oldFormatPaths.TryRemove(path, out _);
            await _creation.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                RejectDatabaseLinks(path);
                await ThrowIfPurgeFencedAsync(key.Database, lifetime.Token).ConfigureAwait(false);
                store.Settled = false;
                _stores[key] = store;
                published = true;
                _initializing.Remove(key);
                _ = _publishing.TryRemove(key, out _);
            }
            finally
            {
                _creation.Release();
            }

            try
            {
                await Ledger.SettleAsync(store, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {

                _logger.ZLogWarning($"A database's fault record could not be read again after it opened; its writes retry it: {error.GetType().Name:@Error}");
            }

            return store;
        }
        finally
        {
            try
            {
                if (!published)
                {

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
                            _ = _publishing.TryRemove(key, out _);
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
                _logger.ZLogError(error, $"A change notification for database {name:@Database} failed; the change itself is committed");
            }
        }
    }
}
