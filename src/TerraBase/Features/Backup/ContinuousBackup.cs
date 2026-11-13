using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Backup;

public sealed record BackupFileStatus(
    string File, string? Generation, long LogBytes, DateTimeOffset? LastShippedAt, double LagSeconds, long PendingBytes, string? LastError, bool Shipping,
    long? SizeBytes, BackupDrill? LastDrill);
public sealed record BackupStatus(bool Shipping, IReadOnlyList<BackupFileStatus> Files);
public interface IContinuousBackup
{
    BackupStatus Status { get; }
    DateTimeOffset? LastShipped(TenantContext database, Ulid? generation = null);
    Task RunOnceAsync(CancellationToken cancellationToken);
}
internal sealed partial class ContinuousBackup : BackgroundService, IContinuousBackup
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan ConfigurationTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private const long MaxPendingBytes = 256L * 1024 * 1024;
    private const int MaxJobs = 4;
    private const int MaxCustomerJobs = 2;

    private readonly IDiskBudget _disk;
    private readonly string _systemPath;
    private readonly IWalBackup _engine;
    private readonly IStorageEngine _storage;
    private readonly BackupTargets _targets;
    private readonly Lazy<ICatalogGate> _catalogGate;
    private readonly SystemCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly ILogger<ContinuousBackup> _logger;
    private readonly IShipperLeases _shippers;
    private readonly ITenantDirectory _tenants;
    private readonly IDatabaseFileCatalog _fileCatalog;
    private readonly BackupSettings _settings;
    private readonly string _node;
    private readonly string _scratch;
    private readonly ConcurrentDictionary<string, ShippedFile> _files = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly List<ShippingJob> _jobs = [with(capacity: MaxJobs)];
    private readonly CancellationTokenSource _jobCancellation = new();
    private readonly SemaphoreSlim _snapshotSlots = new(MaxJobs, MaxJobs);
    private readonly SemaphoreSlim _drillSlot = new(1, 1);
    private readonly SemaphoreSlim _customerDrillSlots = new(MaxCustomerJobs, MaxCustomerJobs);
    private long _scheduleCounter;
    private volatile bool _stopped;
    private readonly Lock _stopGate = new();
    private Task? _stopping;
    private readonly Counter<long> _uploaded;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _holes;
    private readonly Counter<long> _abandoned;
    private readonly Counter<long> _deferred;
    private readonly Counter<long> _generations;
    private readonly Counter<long> _drills;
    private readonly Counter<long> _bucketSnapshots;
    private readonly S3ObjectStore _bucket;
    private DateTimeOffset? _newestBucketSnapshot;
    private bool? _bucketSnapshotsEnabled;
    private DateTimeOffset _nextDiscovery = DateTimeOffset.MinValue;

    public ContinuousBackup(
        IWalBackup engine, IStorageEngine storage, BackupTargets targets, TerraBaseOptions options, TimeProvider time,
        IMeterFactory meters, ILogger<ContinuousBackup> logger, IShipperLeases shippers, ITenantDirectory tenants, BackupSettings settings, S3ObjectStore bucket,
        Lazy<ICatalogGate> catalogGate, IDatabaseFileCatalog fileCatalog, IDiskBudget disk, SystemCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(meters);
        _catalog = catalog;
        (_engine, _storage, _targets, _time, _logger, _shippers) = (engine, storage, targets, time, logger, shippers);
        (_tenants, _settings, _node, _bucket) = (tenants, settings, options.MachineId, bucket);
        _catalogGate = catalogGate;
        _fileCatalog = fileCatalog;
        _disk = disk;
        _systemPath = Path.Combine(options.DataDirectory, "_system.db");
        _scratch = Scratch(options);
        var meter = meters.Create(Telemetry.MeterName);
        _uploaded = meter.CreateCounter<long>("terrabase.backup.uploaded", "By", "Sealed backup bytes stored in the bucket, by kind (snapshot, segment).");
        _failures = meter.CreateCounter<long>("terrabase.backup.failures", description: "Backup passes of a file that failed.");
        _holes = meter.CreateCounter<long>("terrabase.backup.holes", description: "Backup continuity breaks that require a fresh snapshot.");
        _abandoned = meter.CreateCounter<long>("terrabase.backup.abandoned",
            description: "Generations ended by a failure with log read but not stored, by reason (read, destination, shredded, missing_key, lease, shutdown, disk).");
        _deferred = meter.CreateCounter<long>("terrabase.backup.snapshots_deferred",
            description: "Passes that put off a due daily snapshot, by reason (backlog: unread log remains; busy: snapshot uploads in flight).");
        _generations = meter.CreateCounter<long>("terrabase.backup.generations", description: "Generations started (each stores a snapshot).");
        _drills = meter.CreateCounter<long>("terrabase.backup.drills", description: "Restore drills, by result (passed, failed).");
        _bucketSnapshots = meter.CreateCounter<long>("terrabase.backup.bucket_snapshots", description: "Tigris snapshots of the backup bucket, by result (taken, deleted, failed).");
        meter.CreateObservableGauge("terrabase.backup.bucket_snapshot", () => _newestBucketSnapshot?.ToUnixTimeSeconds() ?? 0, "s",
            "Unix time of the newest Tigris snapshot of the backup bucket, on the member that takes them (0 elsewhere).");
        meter.CreateObservableGauge("terrabase.backup.stored", () => Shipped().Sum(file => file.SizeBytes ?? 0), "By",
            "Bytes the backups of the files this member ships occupy in the bucket, measured daily.");
        meter.CreateObservableGauge("terrabase.backup.files", () => Shipped().Count(), description: "Storage files this node ships.");
        meter.CreateObservableGauge("terrabase.backup.lag", () => Shipped().Select(file => Lag(file, _time.GetUtcNow())).DefaultIfEmpty().Max(),
            "s", "Age of data known stored, including verified idle reads, over every file this node ships; age since discovery before the first upload.");
        meter.CreateObservableGauge("terrabase.backup.last_success", () => Shipped().Select(file => file.LastShipped?.ToUnixTimeSeconds() ?? 0).DefaultIfEmpty().Min(),
            "s", "Unix time of the least recent data in the bucket over every file this node ships (0 while a file has none).");
    }
    internal static string Scratch(TerraBaseOptions options) => Path.Combine(options.DataDirectory, ".backup");

    public BackupStatus Status
    {
        get
        {
            var now = _time.GetUtcNow();
            return new(true, [.. _files.Values.OrderBy(file => file.Prefix, StringComparer.Ordinal).Select(file => new BackupFileStatus(
                file.Prefix, file.Generation?.ToString(), file.Position, file.LastShipped, Lag(file, now), file.PendingBytes, file.LastError, file.Shipping,
                file.SizeBytes, file.LastDrill))]);
        }
    }

    public DateTimeOffset? LastShipped(TenantContext database, Ulid? generation = null) =>
        _files.TryGetValue(BackupLayout.File(database, _node), out var file) && file.Shipping && file.PlatformProgress is { } progress
            && (generation is null || progress.Generation == generation) ? progress.At : null;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        long? pass = null;
        var pending = new List<Task>(MaxJobs);
        try
        {
            while (true)
            {
                pass = await ScheduleAsync(pass, pending, cancellationToken).ConfigureAwait(false);
                if (pending.Count == 0)
                {
                    return;
                }
                await Task.WhenAny(pending).ConfigureAwait(false);
                for (var index = pending.Count - 1; index >= 0; index--)
                {
                    if (pending[index].IsCompleted)
                    {
                        var completed = pending[index];
                        pending.RemoveAt(index);
                        await completed.ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }
    internal async Task RetireDeletedAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var prefix = BackupLayout.File(database, _node);
        ShippedFile file;
        ShippingJob? job;
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_files.TryGetValue(prefix, out file!))
            {
                return;
            }
            file.Removed = true;
            file.Retiring ??= file.Retirement.CancelAsync();
            job = file.Job;
            if (job is not null)
            {
                job.Cancelling ??= job.Cancellation.CancelAsync();
            }
        }
        finally
        {
            _pass.Release();
        }

        await Task.WhenAll(file.Retiring!, job?.Completion ?? Task.CompletedTask, job?.Cancelling ?? Task.CompletedTask)
            .WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        cancellationToken.ThrowIfCancellationRequested();

        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_files.TryGetValue(prefix, out var current) && ReferenceEquals(current, file))
            {
                if (file.Job is { } completed)
                {
                    _jobs.Remove(completed);
                    completed.Cancellation.Dispose();
                    file.Job = null;
                }
                await file.DisposeAsync().ConfigureAwait(false);
                _files.TryRemove(prefix, out _);
            }
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task<long> ScheduleAsync(long? pass, List<Task>? pending, CancellationToken cancellationToken)
    {
        using var scheduling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _jobCancellation.Token);
        var schedulingToken = scheduling.Token;
        await _pass.WaitAsync(schedulingToken).ConfigureAwait(false);
        try
        {
            foreach (var (prefix, file) in _files)
            {
                if (file.Job is null && file.Tap.Ended)
                {
                    _files.TryRemove(prefix, out _);
                    await file.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (_stopped || !_catalogGate.Value.IsOpen)
            {
                return pass ?? _scheduleCounter;
            }

            for (var index = _jobs.Count - 1; index >= 0; index--)
            {
                var job = _jobs[index];
                if (!job.Completion.IsCompleted || job.Cancelling is { IsCompleted: false })
                {
                    continue;
                }

                await job.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (job.Cancelling is { } cancelling)
                {
                    await cancelling.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                job.Cancellation.Dispose();
                job.File.Job = null;
                job.File.ScheduledAt = ++_scheduleCounter;
                _jobs.RemoveAt(index);
                if (job.File.Removed)
                {
                    _files.TryRemove(job.File.Prefix, out _);
                    await job.File.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (_time.GetUtcNow() >= _nextDiscovery)
            {
                await DiscoverAsync(schedulingToken).ConfigureAwait(false);
                _nextDiscovery = _time.GetUtcNow() + DiscoveryInterval;
            }

            var boundary = pass ?? _scheduleCounter;
            var candidates = 0;
            while (!_stopped && _jobs.Count < MaxJobs && (pending is null || pending.Count < MaxJobs) && candidates < MaxJobs)
            {
                ShippedFile? next = null;
                foreach (var (_, file) in _files)
                {
                    if (!file.Removed && file.Job is null && file.ScheduledAt <= boundary && CanAdmit(file)
                        && (next is null || file.ScheduledAt < next.ScheduledAt
                            || file.ScheduledAt == next.ScheduledAt && file.Database is null))
                    {
                        next = file;
                    }
                }

                if (next is null)
                {
                    break;
                }

                schedulingToken.ThrowIfCancellationRequested();
                candidates++;
                next.ScheduledAt = ++_scheduleCounter;
                try
                {
                    await RefreshConfigurationAsync(next, schedulingToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!schedulingToken.IsCancellationRequested)
                {
                    await FailedAsync(next, error, schedulingToken).ConfigureAwait(false);
                    next.PendingBytes = next.Buffered;
                    continue;
                }

                schedulingToken.ThrowIfCancellationRequested();
                if (!CanAdmit(next))
                {
                    continue;
                }

                var job = new ShippingJob(next, CustomerWork(next), CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _jobCancellation.Token), cancellationToken);
                next.Job = job;
                _jobs.Add(job);
                job.Completion = Task.Run(() => ShipAsync(job.File, job.Cancellation.Token), CancellationToken.None);
                pending?.Add(JoinAsync(job));
            }

            return boundary;
        }
        finally
        {
            _pass.Release();
        }
    }
    private static async Task JoinAsync(ShippingJob job)
    {
        await job.Completion.ConfigureAwait(false);
        if (job.Side is { } side)
        {
            await side.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static bool CustomerWork(ShippedFile file) =>
        file.Configuration is not null || file.Target.Revision != BackupDestinationNames.Platform;

    private bool CanAdmit(ShippedFile file) => !CustomerWork(file)
        || (_jobs.Count(job => job.Customer) < MaxCustomerJobs
            && !_jobs.Any(job => job.Customer && job.File.Database!.OrganizationId == file.Database!.OrganizationId));

    private async Task RefreshConfigurationAsync(ShippedFile file, CancellationToken cancellationToken, bool force = false)
    {
        if (force || _time.GetUtcNow() >= file.RetryAt && _time.GetUtcNow() >= file.NextConfiguration)
        {
            using var timeout = new CancellationTokenSource(ConfigurationTimeout, _time);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var configuration = await _targets.ConfigurationAsync(file.Database, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            file.Configuration = configuration;
            file.NextConfiguration = _time.GetUtcNow() + DiscoveryInterval;
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        PrepareScratch();
        return base.StartAsync(cancellationToken);
    }

    internal void PrepareScratch()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }

        Directory.CreateDirectory(_scratch);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var bulk = StorageWork.Bulk();
        using var timer = new PeriodicTimer(Interval, _time);
        try
        {
            do
            {
                try
                {
                    _ = await ScheduleAsync(null, null, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested && !_jobCancellation.IsCancellationRequested)
                {
                    _logger.ZLogWarning(error, $"Continuous backup pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _jobCancellation.IsCancellationRequested)
        {
        }
    }
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stopGate)
        {
            return _stopping ??= StopOnceAsync(cancellationToken);
        }
    }

    private async Task StopOnceAsync(CancellationToken cancellationToken)
    {
        _stopped = true;
        var errors = new List<Exception>();
        try
        {
            await _jobCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        await _pass.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _pass.Release();

        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        finally
        {
            if (ExecuteTask is { } execution)
            {
                await execution.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        foreach (var job in _jobs)
        {
            try
            {
                if (job.Cancelling is { } cancelling)
                {
                    await cancelling.ConfigureAwait(false);
                }

                await job.Completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            finally
            {
                await job.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                job.File.Job = null;
                job.Cancellation.Dispose();
            }
        }

        _jobs.Clear();
        using var finalShipping = new CancellationTokenSource(TimeSpan.FromSeconds(4), TimeProvider.System);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, finalShipping.Token);
        var callerCancellation = cancellationToken;
        cancellationToken = deadline.Token;
        for (var phase = 0; phase < 3; phase++)
        {
            foreach (var (_, file) in _files)
            {
                if (file.Removed || cancellationToken.IsCancellationRequested || !_catalogGate.Value.IsOpen)
                {
                    continue;
                }

                if (phase < 2)
                {
                    if (file.FinalRefreshed || CustomerWork(file) != (phase == 1))
                    {
                        continue;
                    }
                    file.FinalRefreshed = true;
                }
                else if (!file.FinalCustomer)
                {
                    continue;
                }

                try
                {
                    if (phase < 2)
                    {
                        await RefreshConfigurationAsync(file, cancellationToken, force: true).ConfigureAwait(false);
                        file.FinalCustomer = file.Configuration is not null;
                        if (file.FinalCustomer)
                        {
                            continue;
                        }
                    }
                    if (file.Target.Revision != (file.Configuration?.Revision ?? BackupDestinationNames.Platform))
                    {
                        file.RetryAt = DateTimeOffset.MinValue;
                    }
                    do
                    {
                        await ShipAsync(file, cancellationToken).ConfigureAwait(false);
                    }
                    while (file.Behind && file.Failures == 0 && file.Generation is not null && !file.Removed && !cancellationToken.IsCancellationRequested);
                }
                catch (Exception error)
                {
                    if (error is OperationCanceledException && cancellationToken.IsCancellationRequested && !callerCancellation.IsCancellationRequested)
                    {
                        _logger.ZLogWarning($"Final backup shipping exhausted its shutdown budget; continuity accounting runs before the remaining taps close");
                    }
                    else
                    {
                        errors.Add(error);
                    }
                }
            }
        }
        foreach (var (_, file) in _files)
        {
            try
            {
                if (!file.Tap.Ended)
                {
                    await EndContinuityAsync(file, "shutdown", CancellationToken.None).ConfigureAwait(false);
                }
                await file.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        _files.Clear();
        if (errors.Count != 0)
        {
            throw new AggregateException("continuous backup shutdown failed", errors).Flatten();
        }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        await OpenAsync(null, present, cancellationToken).ConfigureAwait(false);
        await foreach (var database in _storage.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            var registered = await _fileCatalog.RegisteredFileAsync(database, cancellationToken).ConfigureAwait(false);
            if (registered is true)
            {
                await OpenAsync(database, present, cancellationToken).ConfigureAwait(false);
            }
            else if (registered is not false)
            {
                present.Add(BackupLayout.File(database, _node));
            }
        }

        foreach (var (prefix, file) in _files)
        {
            if (!present.Contains(prefix))
            {
                file.Removed = true;
                if (file.Job is { } job)
                {
                    job.Cancelling ??= job.Cancellation.CancelAsync();
                }
                else
                {
                    _files.TryRemove(prefix, out _);
                    await file.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task OpenAsync(TenantContext? database, HashSet<string> present, CancellationToken cancellationToken)
    {
        var prefix = BackupLayout.File(database, _node);
        present.Add(prefix);
        if (_files.ContainsKey(prefix))
        {
            return;
        }

        try
        {
            if (await _engine.OpenTapAsync(database, cancellationToken).ConfigureAwait(false) is { } tap)
            {
                BackupScratchStream pending;
                try
                {
                    pending = new BackupScratchStream(Path.Combine(_scratch, EntityIds.New().ToString() + ".frames"), null);
                }
                catch
                {
                    await tap.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                _files[prefix] = new ShippedFile(database, prefix, BackupLayout.KeyScope(database), tap, pending, _targets.Platform);
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.ZLogWarning(error, $"Continuous backup cannot open {prefix:@File}; the next discovery retries");
        }
    }


    private async Task FailedAsync(ShippedFile file, Exception error, CancellationToken cancellationToken)
    {
        if (error is ObjectDisposedException)
        {
            file.Removed = true;
            _logger.ZLogDebug($"Continuous backup of {file.Prefix:@File} stopped: its database closed while it was read; the next discovery reopens it if it exists");
            return;
        }
        if (GenerationLost(error))
        {
            file.Target.Keys.Forget(file.Scope);
            if (file.Generation is not null)
            {
                await EndContinuityAsync(file, error is DatabaseShreddedException ? "shredded" : "missing_key", cancellationToken).ConfigureAwait(false);
                file.DrilledAt = DateTimeOffset.MinValue;
            }
        }

        _failures.Add(1);
        file.LastError = error.GetType().Name;
        file.Failures++;
        file.RetryAt = _time.GetUtcNow() + Backoff(file.Failures);
        _logger.ZLogWarning(error, $"Continuous backup of {file.Prefix:@File} failed; the next pass retries");
    }
    private static bool GenerationLost(Exception error) =>
        error is DatabaseShreddedException or InvalidDataException { Message: DataKeys.MissingKeyMessage };

    private static double Lag(ShippedFile file, DateTimeOffset now)
    {
        return file.UnstoredSince is { } since ? Math.Max(0, (now - since).TotalSeconds) : 0;
    }

    private string ScratchFile(string extension) => Path.Combine(_scratch, EntityIds.New().ToString() + extension);
    private static void DeleteSnapshot(string path) => BackupReader.Delete(path);

    public override void Dispose()
    {
        _jobCancellation.Dispose();
        _pass.Dispose();
        _snapshotSlots.Dispose();
        _drillSlot.Dispose();
        _customerDrillSlots.Dispose();
        base.Dispose();
    }

    private sealed class ShippingJob(ShippedFile file, bool customer, CancellationTokenSource cancellation, CancellationToken caller)
    {
        public CancellationToken Caller { get; } = caller;

        public ShippedFile File { get; } = file;
        public bool Customer { get; } = customer;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Completion { get; set; } = Task.CompletedTask;
        public Task? Cancelling { get; set; }
        public Task? Side { get; set; }
    }

    private sealed record ShippingProgress(Ulid Generation, DateTimeOffset At);
    private sealed class SideWork(SnapshotKey snapshot, CancellationTokenSource cancellation)
    {
        public SnapshotKey Snapshot { get; } = snapshot;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource? Stored { get; init; }
        public bool Collected { get; set; }

        public bool Drilling { get; set; }

        public Task Completion { get; set; } = Task.CompletedTask;
    }
    private sealed record Segment(string Key, FileStream Sealed, DateTimeOffset At, DateTimeOffset? Mark, bool EndsMarked, CdcCursor Cursor, long Position);
    private sealed class ShippedFile(TenantContext? database, string prefix, string scope, IWalTap tap, BackupScratchStream pending, BackupTarget target)
        : IAsyncDisposable
    {
        public ShippingJob? Job { get; set; }
        public long ScheduledAt { get; set; }
        public bool Removed { get; set; }
        public CancellationTokenSource Retirement { get; } = new();
        public Task? Retiring { get; set; }
        public bool FinalRefreshed { get; set; }
        public bool FinalCustomer { get; set; }
        public Lock RetainGate { get; } = new();
        public BackupTarget Target { get; set; } = target;
        public ProjectBackupDestinationRecord? Configuration { get; set; }
        public DateTimeOffset NextConfiguration { get; set; } = DateTimeOffset.MinValue;
        public TenantContext? Database { get; } = database;
        public string Prefix { get; } = prefix;
        public ulong? Epoch { get; set; }
        public bool Shipping { get; set; }
        public string Scope { get; } = scope;
        public IWalTap Tap { get; } = tap;
        public BackupScratchStream Pending { get; } = pending;
        public CdcCursor? PendingCursor { get; set; }
        public BackupContinuity? Continuity { get; set; }
        public bool Recovered { get; set; }
        public List<IDisposable> PendingClaims { get; } = [];

        public void ReleasePendingClaims()
        {
            foreach (var claim in PendingClaims)
            {
                claim.Dispose();
            }

            PendingClaims.Clear();
        }
        public Segment? Unstored { get; set; }
        public long Buffered => Pending.Length + (Unstored?.Sealed.Length ?? 0);
        public bool Behind { get; set; }
        public SideWork? Side { get; set; }

        public Ulid? Generation { get; set; }
        public string? GenerationKey { get; set; }
        public long Position { get; set; }
        public long PendingStart { get; set; }
        public DateTimeOffset PendingAt { get; set; }
        public DateTimeOffset? PendingMark { get; set; }

        public bool PendingEndsMarked { get; set; }
        public DateTimeOffset LastSnapshot { get; set; }
        public DateTimeOffset? LastShipped { get; set; }
        public DateTimeOffset? UnstoredSince { get; set; }
        public ShippingProgress? PlatformProgress { get; set; }

        public string? LastError { get; set; }
        public int Failures { get; set; }

        public DateTimeOffset RetryAt { get; set; } = DateTimeOffset.MinValue;
        public int SnapshotFailures { get; set; }

        public DateTimeOffset SnapshotRetryAt { get; set; } = DateTimeOffset.MinValue;

        public string? SnapshotError { get; set; }
        public long PendingBytes { get; set; }
        public DateTimeOffset RetainedAt { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset SizedAt { get; set; } = DateTimeOffset.MinValue;

        public long? SizeBytes { get; set; }
        public TaskCompletionSource? Retaining { get; set; }

        public int RetentionDays { get; set; }

        public BackupDrill? LastDrill { get; set; }
        public DateTimeOffset DrilledAt { get; set; } = DateTimeOffset.MinValue;
        public async Task StopSideAsync()
        {
            if (Side is { } side)
            {
                await side.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                side.Cancellation.Dispose();
                Side = null;
            }
        }
        public void Clear()
        {
            Unstored?.Sealed.Dispose();
            Unstored = null;
            (PendingMark, PendingEndsMarked) = (null, false);
            Pending.SetLength(0);
            ReleasePendingClaims();
        }

        public async ValueTask DisposeAsync()
        {
            await (Retiring ??= Retirement.CancelAsync()).ConfigureAwait(false);
            Side?.Cancellation.Cancel();
            await StopSideAsync().ConfigureAwait(false);
            try
            {
                await Tap.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    Clear();
                    await Pending.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    Target.Dispose();
                    Retirement.Dispose();
                }
            }
        }
    }
}
