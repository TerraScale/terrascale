using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
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
        Lazy<ICatalogGate> catalogGate, IDatabaseFileCatalog fileCatalog, IDiskBudget disk)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(meters);
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
        _holes = meter.CreateCounter<long>("terrabase.backup.holes", description: "Logs found checkpointed or rewritten by someone else, each ending a generation.");
        _abandoned = meter.CreateCounter<long>("terrabase.backup.abandoned",
            description: "Generations ended by a failure with log read but not stored, by reason (outage, read, destination).");
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
            "s", "Seconds the oldest committed data not yet in the bucket has waited, over every file this node ships.");
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
        _files.TryGetValue(BackupLayout.File(database), out var file) && file.Shipping && file.PlatformProgress is { } progress
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
    internal async Task WaitForShippingAsync(string prefix, CancellationToken cancellationToken)
    {
        Task? completion;
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            completion = _files.TryGetValue(prefix, out var file) ? file.Job?.Completion : null;
        }
        finally
        {
            _pass.Release();
        }
        if (completion is not null)
        {
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                    Failed(next, error);
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
                        _logger.ZLogWarning($"Final backup shipping exhausted its shutdown budget; unshipped changes remain in local storage for the next snapshot");
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
                present.Add(BackupLayout.File(database));
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
        var prefix = BackupLayout.File(database);
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

                _files[prefix] = new ShippedFile(database, prefix, BackupLayout.KeyScope(database), tap, pending, _time.GetUtcNow(), _targets.Platform);
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.ZLogWarning(error, $"Continuous backup cannot open {prefix:@File}; the next discovery retries");
        }
    }

    private async Task ShipAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        try
        {
            if (file.Database is { } database)
            {
                var registered = await _fileCatalog.RegisteredFileAsync(database, cancellationToken).ConfigureAwait(false);
                if (registered is false)
                {
                    file.Removed = true;
                    file.Side?.Cancellation.Cancel();
                    await file.StopSideAsync().ConfigureAwait(false);
                    await file.Tap.DisposeAsync().ConfigureAwait(false);
                    return;
                }
            }

            if (file.Tap.Ended)
            {
                file.Removed = true;
                await file.Tap.DisposeAsync().ConfigureAwait(false);
                return;
            }

            if (!await HoldAsync(file, cancellationToken).ConfigureAwait(false))
            {
                await DiscardAsync(file, cancellationToken).ConfigureAwait(false);
                file.LastError = null;
                return;
            }
            var due = _time.GetUtcNow() >= file.RetryAt;
            var switched = false;
            if (due)
            {
                if (file.Target.Revision != (file.Configuration?.Revision ?? BackupDestinationNames.Platform))
                {
                    switched = true;
                    file.Side?.Cancellation.Cancel();
                    await file.StopSideAsync().ConfigureAwait(false);
                    var target = await _targets.ShippingAsync(file.Database, file.Configuration, cancellationToken).ConfigureAwait(false);
                    file.Target.Dispose();
                    file.Target = target;
                    Abandon(file);
                    (file.LastShipped, file.LastSnapshot, file.RetainedAt, file.DrilledAt, file.SizeBytes, file.SizedAt) = (null, default, default, default, null, default);
                    (file.SnapshotFailures, file.SnapshotRetryAt, file.SnapshotError) = (0, DateTimeOffset.MinValue, null);
                }
            }
            if (file.Side is { Stored.Task.IsCompleted: true, Collected: false } side)
            {
                side.Collected = true;
                if (side.Snapshot.Generation == file.Generation && !side.Cancellation.IsCancellationRequested)
                {
                    CollectSnapshot(file, side);
                }
            }

            if (file.Generation is null)
            {
                if (_stopped && !switched)
                {
                    return;
                }

                if (due)
                {
                    await StartGenerationAsync(file, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await DiscardAsync(file, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            if (due && file.Unstored is not null)
            {
                await PutUnstoredAsync(file, cancellationToken).ConfigureAwait(false);
            }
            else if (!due && (file.Behind || file.Buffered >= MaxPendingBytes))
            {
                if (Backoff(file.Failures) >= MaxBackoff)
                {
                    Drop(file, "outage");
                }

                return;
            }
            if (due && file.Pending.Length > 0)
            {
                await UploadPendingAsync(file, cancellationToken).ConfigureAwait(false);
            }

            WalRead read;
            var appended = file.Pending.Length;
            try
            {
                read = await ReadPendingAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                file.Pending.SetLength(appended);
                file.BehindSince ??= _time.GetUtcNow();
                throw;
            }

            if (!read.Continuous)
            {
                Hole(file);
                if (due && !_stopped)
                {
                    await StartGenerationAsync(file, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            var now = _time.GetUtcNow();
            if (read.Bytes > 0)
            {
                file.Position += read.Bytes;
                file.PendingAt = now;
                (file.PendingMark, file.PendingEndsMarked) = (read.Mark ?? file.PendingMark, read.Mark is not null);
                file.BehindSince ??= now;
            }

            file.Behind = read.Remaining;
            if (!due)
            {
                return;
            }

            if (file.Buffered > 0)
            {
                await UploadPendingAsync(file, cancellationToken).ConfigureAwait(false);
            }

            if (!file.Behind)
            {
                file.BehindSince = null;
            }

            if (file.Generation is not null && now - file.RetainedAt >= RetentionInterval)
            {
                try
                {
                    await RetainAsync(file, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.ZLogWarning(error, $"Retention of {file.Prefix:@File} failed; it retries next hour");
                }
            }
            if (now - file.LastSnapshot >= SnapshotInterval && now >= file.SnapshotRetryAt && !_stopped)
            {
                if (file.Behind)
                {
                    _deferred.Add(1, new KeyValuePair<string, object?>("reason", "backlog"));
                }
                else if (file.Side is { Completion.IsCompleted: false } || !_snapshotSlots.Wait(0, cancellationToken))
                {
                    _deferred.Add(1, new KeyValuePair<string, object?>("reason", "busy"));
                }
                else
                {
                    await SnapshotAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }
            (file.LastError, file.Failures) = (file.SnapshotError, 0);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            Failed(file, error);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested && file.Target.Revision != BackupDestinationNames.Platform)
            {
                Abandon(file);
            }

            file.PendingBytes = file.Buffered;
        }
    }
    private void CollectSnapshot(ShippedFile file, SideWork side)
    {
        if (side.Stored!.Task.Exception?.GetBaseException() is { } error)
        {
            _failures.Add(1);
            file.SnapshotFailures++;
            (file.SnapshotRetryAt, file.SnapshotError) = (_time.GetUtcNow() + Backoff(file.SnapshotFailures), error.GetType().Name);
            _logger.ZLogWarning(error, $"The daily snapshot of {file.Prefix:@File} failed; it retries after a backoff while segments ship");
            return;
        }

        (file.LastSnapshot, file.SnapshotFailures, file.SnapshotRetryAt, file.SnapshotError) = (side.Snapshot.At, 0, DateTimeOffset.MinValue, null);
        Prove(file, side.Snapshot.At);
    }

    private void Failed(ShippedFile file, Exception error)
    {
        if (error is ObjectDisposedException)
        {
            file.Removed = true;
            _logger.ZLogDebug($"Continuous backup of {file.Prefix:@File} stopped: its database closed while it was read; the next discovery reopens it if it exists");
            return;
        }
        if (file.Target.Revision != BackupDestinationNames.Platform && file.Generation is not null)
        {
            Drop(file, "destination");
        }

        _failures.Add(1);
        file.LastError = error.GetType().Name;
        file.Failures++;
        file.RetryAt = _time.GetUtcNow() + Backoff(file.Failures);
        _logger.ZLogWarning(error, $"Continuous backup of {file.Prefix:@File} failed; the next pass retries");
    }
    private async Task<bool> HoldAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        if (await _shippers.ShipperEpochAsync(file.Database, cancellationToken).ConfigureAwait(false) is not { } epoch)
        {
            if (file.Shipping)
            {
                (file.Shipping, file.Epoch, file.BehindSince) = (false, null, null);
                Abandon(file);
            }

            return false;
        }

        if (file.Epoch != epoch)
        {
            (file.Shipping, file.Epoch, file.BehindSince) = (true, epoch, _time.GetUtcNow());
            Abandon(file);
            _logger.ZLogInformation($"This member now ships {file.Prefix:@File}");
        }

        return true;
    }

    private IEnumerable<ShippedFile> Shipped() => _files.Values.Where(file => file.Shipping);
    private async Task<long> WorkspaceAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var source = file.Database is { } database
            ? await _storage.GetDatabaseSizeAsync(database, cancellationToken).ConfigureAwait(false) ?? throw new IOException("backup source disappeared")
            : new FileInfo(_systemPath).Length + (File.Exists(_systemPath + "-wal") ? new FileInfo(_systemPath + "-wal").Length : 0);
        return DiskBudget.WorkspaceBytes(source);
    }

    private async Task<WalRead> ReadPendingAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var allowance = await WorkspaceAsync(file, cancellationToken).ConfigureAwait(false);
        IDisposable? claim = _disk.Reserve(allowance);
        var before = file.Pending.Length;
        file.Pending.Limit = checked(before + allowance / 4);
        try
        {
            var read = await file.Tap.ReadAsync(file.Pending, cancellationToken).ConfigureAwait(false);
            if (file.Pending.Length > before)
            {
                file.PendingClaims.Add(claim);
                claim = null;
            }

            return read;
        }
        catch
        {
            file.Pending.SetLength(before);
            file.Pending.Position = before;
            throw;
        }
        finally
        {
            claim?.Dispose();
        }
    }
    private async Task StartGenerationAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var claim = _disk.Reserve(await WorkspaceAsync(file, cancellationToken).ConfigureAwait(false));
        var generation = EntityIds.New(_time);
        var snapshot = ScratchFile(".db");
        try
        {
            file.BehindSince ??= _time.GetUtcNow();
            await file.StopSideAsync().ConfigureAwait(false);
            file.Clear();
            await DiscardAsync(file, cancellationToken).ConfigureAwait(false);
            var captured = await file.Tap.SnapshotAsync(Stream.Null, snapshot, cancellationToken).ConfigureAwait(false);
            var at = captured.At;
            var key = BackupLayout.Snapshot(file.Prefix, at, generation, 0);
            var live = !_stopped && at - file.DrilledAt >= DrillInterval
                ? await _engine.ChecksumAsync(snapshot, cancellationToken).ConfigureAwait(false)
                : null;
            await StoreGenerationProofAsync(file, generation, key, captured, cancellationToken).ConfigureAwait(false);
            await StoreAsync(file, key, snapshot, "snapshot", cancellationToken).ConfigureAwait(false);
            (file.Generation, file.Position, file.PendingStart, file.LastSnapshot, file.LastShipped, file.BehindSince, file.Behind) =
                (generation, 0, 0, at, at, at, false);
            Prove(file, at);
            _generations.Add(1);
            (file.LastError, file.Failures) = (null, 0);
            (file.SnapshotFailures, file.SnapshotRetryAt, file.SnapshotError) = (0, DateTimeOffset.MinValue, null);
            if (live is not null)
            {
                file.DrilledAt = at;
                StartSide(file, new SnapshotKey(key, at, generation, 0), snapshot: null, live, drill: true);
            }
        }
        finally
        {
            DeleteSnapshot(snapshot);
            claim.Dispose();
        }
    }
    private async Task SnapshotAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var snapshot = ScratchFile(".db");
        var handedOff = false;
        IDisposable? claim = null;
        try
        {
            var allowance = await WorkspaceAsync(file, cancellationToken).ConfigureAwait(false);
            claim = _disk.Reserve(allowance);
            await file.StopSideAsync().ConfigureAwait(false);
            file.PendingClaims.Add(_disk.Reserve(allowance));
            file.Pending.Limit = checked(file.Pending.Length + allowance / 4);
            WalSnapshot captured;
            try
            {
                captured = await file.Tap.SnapshotAsync(file.Pending, snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Abandon(file);
                }
                else
                {
                    Drop(file, "read");
                }

                throw;
            }

            var read = captured.Read;
            if (!read.Continuous)
            {
                Hole(file);
                return;
            }

            var at = captured.At;
            file.Position += read.Bytes;
            if (read.Bytes > 0)
            {
                file.PendingAt = at;
                (file.PendingMark, file.PendingEndsMarked) = (read.Mark ?? file.PendingMark, read.Mark is not null);
            }

            if (file.Buffered > 0)
            {
                await UploadPendingAsync(file, cancellationToken).ConfigureAwait(false);
            }
            if (file.LastShipped is not { } shipped || shipped < at)
            {
                file.LastShipped = at;
            }

            var generation = file.Generation!.Value;
            var key = new SnapshotKey(BackupLayout.Snapshot(file.Prefix, at, generation, file.Position), at, generation, file.Position);
            var drill = at - file.DrilledAt >= DrillInterval;
            if (drill)
            {
                file.DrilledAt = at;
            }

            StartSide(file, key, snapshot, live: null, drill, claim);
            handedOff = true;
        }
        finally
        {
            if (file.Pending.Length == 0)
            {
                file.ReleasePendingClaims();
            }

            if (!handedOff)
            {
                try
                {
                    DeleteSnapshot(snapshot);
                    claim?.Dispose();
                }
                finally
                {
                    _snapshotSlots.Release();
                }
            }
        }
    }
    private void StartSide(ShippedFile file, SnapshotKey key, string? snapshot, byte[]? live, bool drill, IDisposable? claim = null)
    {
        var side = new SideWork(key, CancellationTokenSource.CreateLinkedTokenSource(file.Job?.Caller ?? CancellationToken.None, _jobCancellation.Token))
        {
            Stored = snapshot is null ? null : new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        file.Side = side;
        side.Completion = Task.Run(() => SideAsync(file, side, snapshot, live, drill, claim), CancellationToken.None);
        if (file.Job is { } job)
        {
            job.Side = side.Completion;
        }
    }

    private async Task SideAsync(ShippedFile file, SideWork side, string? snapshot, byte[]? live, bool drill, IDisposable? claim)
    {
        var token = side.Cancellation.Token;
        if (snapshot is not null)
        {
            try
            {
                live = drill ? await _engine.ChecksumAsync(snapshot, token).ConfigureAwait(false) : null;
                await StoreAsync(file, side.Snapshot.Key, snapshot, "snapshot", token).ConfigureAwait(false);
                side.Stored!.TrySetResult();
            }
            catch (Exception error)
            {
                side.Stored!.TrySetException(error);
                return;
            }
            finally
            {
                try
                {
                    DeleteSnapshot(snapshot);
                    claim?.Dispose();
                }
                finally
                {
                    _snapshotSlots.Release();
                }
            }
        }

        if (live is null)
        {
            return;
        }

        var slot = file.Target.Revision == BackupDestinationNames.Platform ? _drillSlot : _customerDrillSlots;
        await slot.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Task? retained;
                lock (file)
                {
                    retained = file.Retaining?.Task;
                    if (retained is null)
                    {
                        side.Drilling = true;
                        break;
                    }
                }
                await retained.WaitAsync(token).ConfigureAwait(false);
            }
            await DrillAsync(file, side.Snapshot, live, token).ConfigureAwait(false);
        }
        finally
        {
            slot.Release();
        }
    }
    private async Task UploadPendingAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        await PutUnstoredAsync(file, cancellationToken).ConfigureAwait(false);
        if (file.Pending.Length == 0)
        {
            return;
        }

        var key = BackupLayout.Segment(file.Prefix, file.Generation!.Value, file.PendingStart, file.PendingAt);
        file.Pending.Position = 0;
        BackupScratchStream sealedObject;
        try
        {
            sealedObject = await SealAsync(file, key, file.Pending, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            file.Pending.Seek(0, SeekOrigin.End);
        }
        file.Unstored = new(key, sealedObject, file.PendingAt, file.PendingMark, file.PendingEndsMarked);
        (file.PendingMark, file.PendingEndsMarked) = (null, false);
        file.Pending.SetLength(0);
        sealedObject.TakeClaims(file.PendingClaims);
        file.PendingStart = file.Position;
        await PutUnstoredAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private async Task PutUnstoredAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        if (file.Unstored is not { } segment)
        {
            return;
        }

        await PutAsync(file, segment.Key, segment.Sealed, "segment", cancellationToken).ConfigureAwait(false);
        file.Unstored = null;
        await segment.Sealed.DisposeAsync().ConfigureAwait(false);
        file.LastShipped = segment.At;
        if (segment.Mark is { } mark)
        {
            Prove(file, segment.EndsMarked && segment.At.ToUnixTimeMilliseconds() == mark.ToUnixTimeMilliseconds()
                ? mark
                : DateTimeOffset.FromUnixTimeSeconds(mark.ToUnixTimeSeconds()));
        }
    }
    private static async Task DiscardAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        WalRead read;
        do
        {
            read = await file.Tap.ReadAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        }
        while (read.Remaining);
    }

    private async Task StoreAsync(ShippedFile file, string key, string path, string kind, CancellationToken cancellationToken)
    {
        await using var bundle = new FileStream(ScratchFile(".snapshot"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await SnapshotBundle.WriteAsync(path, bundle, cancellationToken).ConfigureAwait(false);
        bundle.Position = 0;
        await StoreAsync(file, key, bundle, kind, cancellationToken).ConfigureAwait(false);
    }

    private async Task StoreAsync(ShippedFile file, string key, Stream content, string kind, CancellationToken cancellationToken)
    {
        await using var sealedObject = await SealAsync(file, key, content, cancellationToken).ConfigureAwait(false);
        await PutAsync(file, key, sealedObject, kind, cancellationToken).ConfigureAwait(false);
    }
    private async Task<BackupScratchStream> SealAsync(ShippedFile file, string key, Stream content, CancellationToken cancellationToken)
    {
        var (keyId, dataKey) = await file.Target.Keys.CurrentAsync(file.Scope, cancellationToken).ConfigureAwait(false);
        var bytes = checked(content.Length + (content.Length / (1 << 20) + 1) * 24 + 62);
        var sealedObject = new BackupScratchStream(ScratchFile(".sealed"), null, bytes);
        try
        {
            await BackupObject.WriteAsync(content, sealedObject, key, keyId, dataKey, cancellationToken).ConfigureAwait(false);
            return sealedObject;
        }
        catch
        {
            await sealedObject.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task PutAsync(ShippedFile file, string key, FileStream sealedObject, string kind, CancellationToken cancellationToken)
    {
        sealedObject.Position = 0;
        await file.Target.Store.PutAsync(key, sealedObject, cancellationToken).ConfigureAwait(false);
        _uploaded.Add(sealedObject.Length, new KeyValuePair<string, object?>("kind", kind));
    }

    private void Hole(ShippedFile file)
    {
        _holes.Add(1);
        _logger.ZLogWarning($"The log of {file.Prefix:@File} has a hole; its backup starts a new generation");
        file.BehindSince ??= _time.GetUtcNow();
        Abandon(file);
    }
    private void Drop(ShippedFile file, string reason)
    {
        var bytes = file.Buffered;
        _abandoned.Add(1, new KeyValuePair<string, object?>("reason", reason));
        _logger.ZLogWarning($"Continuous backup of {file.Prefix:@File} dropped {bytes:@Bytes} bytes of log it had not stored ({reason:@Reason}); it starts a new generation");
        file.BehindSince ??= _time.GetUtcNow();
        Abandon(file);
    }
    private static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures - 1, 9))));
    private static void Abandon(ShippedFile file)
    {
        file.PlatformProgress = null;
        file.Generation = null;
        if (file.Side is { Completion.IsCompleted: false } side)
        {
            file.DrilledAt = DateTimeOffset.MinValue;
            side.Cancellation.Cancel();
        }

        file.Clear();
    }
    private static void Prove(ShippedFile file, DateTimeOffset at)
    {
        at = DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());
        if (!file.Target.IncludesPlatform)
        {
            file.PlatformProgress = null;
        }
        else if (file.PlatformProgress is not { } progress || progress.Generation != file.Generation || progress.At < at)
        {
            file.PlatformProgress = new(file.Generation!.Value, at);
        }
    }

    private static double Lag(ShippedFile file, DateTimeOffset now) => file.BehindSince is { } since ? Math.Max(0, (now - since).TotalSeconds) : 0;

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
    private sealed record Segment(string Key, FileStream Sealed, DateTimeOffset At, DateTimeOffset? Mark, bool EndsMarked);
    private sealed class ShippedFile(TenantContext? database, string prefix, string scope, IWalTap tap, BackupScratchStream pending, DateTimeOffset opened, BackupTarget target)
        : IAsyncDisposable
    {
        public ShippingJob? Job { get; set; }
        public long ScheduledAt { get; set; }
        public bool Removed { get; set; }
        public bool FinalRefreshed { get; set; }
        public bool FinalCustomer { get; set; }
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
        public long Position { get; set; }
        public long PendingStart { get; set; }
        public DateTimeOffset PendingAt { get; set; }
        public DateTimeOffset? PendingMark { get; set; }

        public bool PendingEndsMarked { get; set; }
        public DateTimeOffset LastSnapshot { get; set; }
        public DateTimeOffset? LastShipped { get; set; }
        public ShippingProgress? PlatformProgress { get; set; }
        public DateTimeOffset? BehindSince { get; set; } = opened;

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
                }
            }
        }
    }
}
