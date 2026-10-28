using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Backup;

/// <summary>Where one storage file's backup stands on this node.</summary>
/// <param name="File">The file's directory in the bucket (<see cref="BackupLayout.File"/>).</param>
/// <param name="Generation">The generation being shipped; null until its first snapshot is stored.</param>
/// <param name="LogBytes">Bytes of the generation's change log read so far.</param>
/// <param name="LastShippedAt">When the newest data in the bucket was read from the file.</param>
/// <param name="LagSeconds">Seconds the oldest data not yet in the bucket has waited; 0 when none waits.</param>
/// <param name="PendingBytes">Log bytes read and not yet stored.</param>
/// <param name="LastError">The kind of the last failure, cleared by the next successful pass.</param>
/// <param name="Shipping">Whether this node ships the file now (<see cref="IShipperLeases"/>).</param>
/// <param name="SizeBytes">Bytes the file's backup occupies in the bucket, measured daily by its shipper.</param>
/// <param name="LastDrill">The last restore drill this member ran on the file.</param>
public sealed record BackupFileStatus(
    string File, string? Generation, long LogBytes, DateTimeOffset? LastShippedAt, double LagSeconds, long PendingBytes, string? LastError, bool Shipping,
    long? SizeBytes, BackupDrill? LastDrill);

/// <summary>Continuous backup on this node: what it ships.</summary>
/// <param name="Shipping">Whether backup is configured on this node.</param>
/// <param name="Files">Every storage file of the node; the ones it ships have <see cref="BackupFileStatus.Shipping"/> set.</param>
public sealed record BackupStatus(bool Shipping, IReadOnlyList<BackupFileStatus> Files);

/// <summary>Ships every storage file of this node to the backup bucket (TS-633).</summary>
public interface IContinuousBackup
{
    /// <summary>What this node ships, and how far each file is.</summary>
    BackupStatus Status { get; }

    /// <summary>When the newest platform data was read, if this node ships <paramref name="database"/> and the requested generation to the platform.</summary>
    DateTimeOffset? LastShipped(TenantContext database, Ulid? generation = null);

    /// <summary>Finds new and deleted files when due, then waits for the jobs this pass starts. Already busy files are skipped.</summary>
    Task RunOnceAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Litestream-style continuous backup (<c>docs/backup.md</c>). Once a second each file's newly committed log (up to the
/// tap's read budget) is sealed and stored as one segment; a segment whose upload failed is stored again, unchanged under
/// the same key, before anything later. Each file starts a generation with a snapshot when this member starts shipping it,
/// after a hole or dropped log, and daily. Restoring a time takes the newest snapshot at or before it and its generation's
/// segments up to it. Every member taps every file and checkpoints it; one ships it, chosen by <see cref="IShipperLeases"/>.
/// </summary>
/// <remarks>
/// ponytail: every start opens every database and stores a fresh snapshot of each; resume generations across restarts when
/// that upload gets expensive. Four jobs ship at a time, so a large fleet stretches each file's one-second interval.
/// </remarks>
internal sealed partial class ContinuousBackup : BackgroundService, IContinuousBackup
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan ConfigurationTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    // Log read but not stored, per file, past which reads wait while uploads fail, leaving the rest in turso_cdc (bounds
    // scratch disk; a read adds at most the tap's budget and one transaction past it).
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
    // Only the scheduling gate owns admission and the counter; jobs alone own their file's shipping state.
    private readonly List<ShippingJob> _jobs = [with(capacity: MaxJobs)];
    private readonly CancellationTokenSource _jobCancellation = new();
    // Beside shipping: daily snapshot uploads, each holding its scratch copy until stored, and restore drills, each rebuilding
    // a file in scratch. Customer drills have their own slots, so a stalled customer bucket never holds a platform drill.
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

    /// <summary>Scratch space for frames, snapshots and sealed objects: a directory of the data directory, emptied at start.</summary>
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

                // A slow job cannot stop this invocation from filling slots freed by its healthy siblings.
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

    private async Task<long> ScheduleAsync(long? pass, List<Task>? pending, CancellationToken cancellationToken)
    {
        using var scheduling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _jobCancellation.Token);
        var schedulingToken = scheduling.Token;
        await _pass.WaitAsync(schedulingToken).ConfigureAwait(false);
        try
        {
            // A healthy rewrite waits for an ended tap's reader to close, and the catalog reads below wait while it rewrites
            // SYSTEM: release such taps first, or neither moves until the rewrite gives up (TS-982). Discovery opens new ones.
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
                // A later manual pass must not adopt a job that was busy when that pass began.
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
                    // Refresh only bounded admission candidates, never the idle fleet while shipping slots are full.
                    await RefreshConfigurationAsync(next, schedulingToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!schedulingToken.IsCancellationRequested)
                {
                    Failed(next, error);
                    next.PendingBytes = next.Buffered;
                    continue;
                }

                schedulingToken.ThrowIfCancellationRequested();
                // A formerly platform file can become customer work during the refresh.
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

    // A manual pass also waits for the snapshot upload or drill its job started beside shipping.
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
            // Cancel and join the catalog read before releasing admission; its timeout uses the file's normal backoff.
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
        // ExecuteAsync runs on a background thread in .NET 11. Prepare scratch before startup completes, so a caller's
        // first pass cannot race the cleanup or open a tap before the frames directory exists.
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }

        Directory.CreateDirectory(_scratch);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Snapshot copies and log reads of every database: bulk work, which leaves an I/O thread to short reads.
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
                    // A failed discovery must not stop the host; the next tick retries.
                    _logger.ZLogWarning(error, $"Continuous backup pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _jobCancellation.IsCancellationRequested)
        {
            // Service shutdown can interrupt discovery or the timer wait without failing the host.
        }
    }

    // The host can be stopped twice at once (WebApplicationFactory stops it while Program's RunAsync stops and then
    // disposes it): every caller awaits the one stop, so none touches the pass gate after disposal.
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stopGate)
        {
            return _stopping ??= StopOnceAsync(cancellationToken);
        }
    }

    private async Task StopOnceAsync(CancellationToken cancellationToken)
    {
        // Close future scheduling and signal in-flight discovery/jobs before joining the gate.
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

        // Discovery and manual schedulers observe the service signal before this join.
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

        // Admission is closed. Join even when the shutdown token expired before touching any file resources.
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
                // Active jobs use the service token; final shipping below uses the supplied shutdown token.
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            finally
            {
                // A cancellation callback failure must not leave its shipping task unjoined.
                await job.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                job.File.Job = null;
                job.Cancellation.Dispose();
            }
        }

        _jobs.Clear();
        // Final network shipping must leave time for the durable local close; joins still own their resources.
        using var finalShipping = new CancellationTokenSource(TimeSpan.FromSeconds(4), TimeProvider.System);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, finalShipping.Token);
        var callerCancellation = cancellationToken;
        cancellationToken = deadline.Token;
        // Known platform files flush before cached customer lookups can consume the shutdown budget.
        // Then refresh cached customer files for detachments, and only then restart customer I/O.
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

                    // A refresh can change classification; never refresh that file again in the next phase.
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
                        // Refresh even cached customer records: a detached destination now belongs to platform.
                        file.FinalCustomer = file.Configuration is not null;
                        if (file.FinalCustomer)
                        {
                            continue;
                        }
                    }

                    // A confirmed replacement/detachment must not inherit the failed destination's backoff.
                    if (file.Target.Revision != (file.Configuration?.Revision ?? BackupDestinationNames.Platform))
                    {
                        file.RetryAt = DateTimeOffset.MinValue;
                    }

                    // Ship the whole backlog, not one read of it, while the shutdown budget lasts and passes succeed.
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

        // Every job has joined, including after an expired token; return every tap before the engine closes.
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
                // An unreadable catalog cannot authorize new taps or discard an existing generation.
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
            // A database deleted or failing to open now is retried by the next discovery.
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
                // Unknown ownership preserves the existing generation and its normal catalog refusal/retry path.
            }

            if (file.Tap.Ended)
            {
                // Released before HoldAsync reads the catalog (see ScheduleAsync).
                file.Removed = true;
                await file.Tap.DisposeAsync().ConfigureAwait(false);
                return;
            }

            if (!await HoldAsync(file, cancellationToken).ConfigureAwait(false))
            {
                // Another member ships this file: read only to checkpoint it.
                await DiscardAsync(file, cancellationToken).ConfigureAwait(false);
                file.LastError = null;
                return;
            }

            // Admission already classified the catalog record. Only this job may switch or dispose its fixed target;
            // failed catalog reads fail closed: customer-only data never falls back to platform.
            var due = _time.GetUtcNow() >= file.RetryAt;
            var switched = false;
            if (due)
            {
                if (file.Target.Revision != (file.Configuration?.Revision ?? BackupDestinationNames.Platform))
                {
                    switched = true;
                    // A snapshot upload or drill beside shipping uses the old target.
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
                // A cancelled upload (shutdown, removal, a new destination) did not fail: that snapshot is just not stored.
                if (side.Snapshot.Generation == file.Generation && !side.Cancellation.IsCancellationRequested)
                {
                    CollectSnapshot(file, side);
                }
            }

            if (file.Generation is null)
            {
                // Shutdown ships what generations hold and starts none, a full snapshot that would spend the budget other files
                // need; only a file whose destination was just detached gets its final snapshot into the platform bucket.
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
                // Retry the failed segment first: while the bucket still fails, reading on only moves log into scratch.
                await PutUnstoredAsync(file, cancellationToken).ConfigureAwait(false);
            }
            else if (!due && (file.Behind || file.Buffered >= MaxPendingBytes))
            {
                // Uploads fail: leave the backlog in turso_cdc, where the backup anchor holds it. Once failures reach the longest
                // backoff the outage is sustained: drop the generation, so reads trim the change log again.
                if (Backoff(file.Failures) >= MaxBackoff)
                {
                    Drop(file, "outage");
                }

                return;
            }

            // A failed seal/key lookup leaves Pending and its claims intact. Flush it before admitting another read.
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
                // The tap did not move: drop the part this read appended, and a later pass reads the same changes again.
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
                    // Retention retries next hour; shipping goes on.
                    _logger.ZLogWarning(error, $"Retention of {file.Prefix:@File} failed; it retries next hour");
                }
            }

            // Retention precedes the new upload/drill, so its snapshot selection sees only retained inputs.
            // A snapshot reads what is left while it holds writers: never with a backlog. Its upload holds a snapshot slot, and
            // after a failed one the next waits out its own backoff.
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

            // A daily snapshot that keeps failing stays visible while segments ship.
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
                // Cancellation can follow an acknowledgement in only one bucket, regardless of the thrown type.
                Abandon(file);
            }

            file.PendingBytes = file.Buffered;
        }
    }

    // A daily snapshot's upload ended beside shipping. A failure counts, shows in lastError, and puts off the next snapshot by
    // its own backoff, doubling like the shipping one, so a failing upload neither repeats every pass nor holds segments back.
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
            // The store closed under this read: its database was deleted or the engine stops. The job ends the file, so the
            // next discovery opens a new tap if the database exists again, rather than reading the closed store forever.
            file.Removed = true;
            _logger.ZLogDebug($"Continuous backup of {file.Prefix:@File} stopped: its database closed while it was read; the next discovery reopens it if it exists");
            return;
        }

        // A customer destination starts over with a snapshot after any failure (BackupTargets' mirror), never a retry.
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

    // Whether this member ships the file now. A new epoch means the file is shipped from another physical copy or history
    // than this member's last generation of it, so shipping starts a new generation with a snapshot.
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

    // Physical source bytes include WAL. This estimates amplification; it cannot bound concurrent foreground growth.
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
        // Bound frames to a quarter of the claim, leaving room for their sealed copy and envelope overhead.
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

    // A generation starts with a snapshot; the frames read before it are in it, so they are dropped.
    private async Task StartGenerationAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var claim = _disk.Reserve(await WorkspaceAsync(file, cancellationToken).ConfigureAwait(false));
        var generation = EntityIds.New(_time);
        var snapshot = ScratchFile(".db");
        try
        {
            file.BehindSince ??= _time.GetUtcNow();
            // The ended generation's upload or drill, cancelled when it ended.
            await file.StopSideAsync().ConfigureAwait(false);
            file.Clear();
            // Read a backlog before the snapshot holds writers, so the gate covers only what commits meanwhile.
            await DiscardAsync(file, cancellationToken).ConfigureAwait(false);
            var captured = await file.Tap.SnapshotAsync(Stream.Null, snapshot, cancellationToken).ConfigureAwait(false);
            var at = captured.At;
            var key = BackupLayout.Snapshot(file.Prefix, at, generation, 0);
            // The drill compares the bucket with this copy's checksum, so the copy need not outlive this pass.
            var live = !_stopped && at - file.DrilledAt >= DrillInterval
                ? await _engine.ChecksumAsync(snapshot, cancellationToken).ConfigureAwait(false)
                : null;
            await StoreGenerationProofAsync(file, generation, key, captured, cancellationToken).ConfigureAwait(false);
            await StoreAsync(file, key, snapshot, "snapshot", cancellationToken).ConfigureAwait(false);
            // What committed after the pin waits for the next read; until then lag counts from the pin.
            (file.Generation, file.Position, file.PendingStart, file.LastSnapshot, file.LastShipped, file.BehindSince, file.Behind) =
                (generation, 0, 0, at, at, at, false);
            Prove(file, at);
            _generations.Add(1);
            // The generation is stored: an earlier failure no longer stands (lag still shows what waits).
            (file.LastError, file.Failures) = (null, 0);
            (file.SnapshotFailures, file.SnapshotRetryAt, file.SnapshotError) = (0, DateTimeOffset.MinValue, null);
            if (live is not null)
            {
                // Segments of this generation ship from the next pass on, while the drill runs beside them.
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

    // A later snapshot of the same generation: the log before it is stored first, so the next segment starts at it. Its upload
    // and drill then run beside shipping, holding the snapshot slot the caller took and its scratch copy until it is stored.
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
            // SnapshotAsync also drains the current log backlog. Its claim survives a failed segment upload.
            file.PendingClaims.Add(_disk.Reserve(allowance));
            file.Pending.Limit = checked(file.Pending.Length + allowance / 4);
            WalSnapshot captured;
            try
            {
                captured = await file.Tap.SnapshotAsync(file.Pending, snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The tap may have moved past changes now only partly in Pending: this generation ends.
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
                // The frames before the snapshot have a hole: this generation ends; the next pass starts one.
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

            // Everything committed by the pin is in the bucket now (that segment, or nothing new), as lag and status show. The
            // restore window moves to `at` only once the snapshot is stored: until then nothing in the bucket proves it.
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

    // Starts a snapshot's upload (from `snapshot`, a scratch copy it then owns) and its drill beside shipping. A manual pass
    // waits for it through the job; removal, a new destination and shutdown cancel and join it before its target goes.
    private void StartSide(ShippedFile file, SnapshotKey key, string? snapshot, byte[]? live, bool drill, IDisposable? claim = null)
    {
        // Cancelled with the pass that started it (a manual pass waits for it), and with the service.
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
                // The file's next pass collects the failure.
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

    // A segment is sealed once. A PUT that failed, even one the bucket stored, is sent again with exactly those bytes under the
    // same key before anything later is sealed; the log read meanwhile becomes the next segment, so no two segments overlap.
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

        // Nothing was sent under this key yet, so a failure up to here leaves the log to the next pass's segment.
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
            // A read mark proves the whole second at or before it, or its own time when it ends a segment read in that
            // millisecond: restores there place every transaction (PointInTimeRestore).
            Prove(file, segment.EndsMarked && segment.At.ToUnixTimeMilliseconds() == mark.ToUnixTimeMilliseconds()
                ? mark
                : DateTimeOffset.FromUnixTimeSeconds(mark.ToUnixTimeSeconds()));
        }
    }

    // Reads what this member does not store, only to trim the change log: the whole backlog, not one read's budget.
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
        // This scratch payload contains encrypted pages and a wrapped key; the outer envelope authenticates both.
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

    // Seals content into a scratch file deleted when it closes.
    private async Task<BackupScratchStream> SealAsync(ShippedFile file, string key, Stream content, CancellationToken cancellationToken)
    {
        var (keyId, dataKey) = await file.Target.Keys.CurrentAsync(file.Scope, cancellationToken).ConfigureAwait(false);
        var bytes = checked(content.Length + (content.Length / (1 << 20) + 1) * 24 + 62);
        // The snapshot or pending-frame claim already covers this peak; pending ownership transfers after truncation.
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

    // Ends the generation after a failure, dropping log read but not stored: until the next snapshot that history is in no
    // backup, so it is logged and counted, and lag stays above zero until a new generation is stored.
    private void Drop(ShippedFile file, string reason)
    {
        var bytes = file.Buffered;
        _abandoned.Add(1, new KeyValuePair<string, object?>("reason", reason));
        _logger.ZLogWarning($"Continuous backup of {file.Prefix:@File} dropped {bytes:@Bytes} bytes of log it had not stored ({reason:@Reason}); it starts a new generation");
        file.BehindSince ??= _time.GetUtcNow();
        Abandon(file);
    }

    // 1 s after the first failure, doubling up to five minutes, like Litestream's sync backoff.
    private static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures - 1, 9))));

    // Ends the generation; on its own only where the file moves on on purpose (a new shipper or destination, shutdown).
    private static void Abandon(ShippedFile file)
    {
        file.PlatformProgress = null;
        file.Generation = null;
        if (file.Side is { Completion.IsCompleted: false } side)
        {
            // A drill it cancels reports nothing, so the next generation's snapshot is drilled instead of a day later.
            file.DrilledAt = DateTimeOffset.MinValue;
            side.Cancellation.Cancel();
        }

        file.Clear();
    }

    // The restore window's latest time on this member (IContinuousBackup.LastShipped): one the platform bucket proves
    // restorable, published with its generation only after the fixed target acknowledges all its destinations. It moves
    // forward only, to a stored snapshot's time or the time a stored read mark proves.
    private static void Prove(ShippedFile file, DateTimeOffset at)
    {
        // Other members recover progress from object names/read marks, both stored in whole milliseconds.
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

    // Turso may leave a journal, and a drill a log, next to a snapshot.
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
        /// <summary>The token of the pass that started the job.</summary>
        public CancellationToken Caller { get; } = caller;

        public ShippedFile File { get; } = file;
        public bool Customer { get; } = customer;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Completion { get; set; } = Task.CompletedTask;
        public Task? Cancelling { get; set; }

        /// <summary>The snapshot upload or drill this job started beside shipping, for a manual pass to wait for.</summary>
        public Task? Side { get; set; }
    }

    private sealed record ShippingProgress(Ulid Generation, DateTimeOffset At);

    /// <summary>A snapshot's upload, its drill, or both, running beside the file's shipping.</summary>
    private sealed class SideWork(SnapshotKey snapshot, CancellationTokenSource cancellation)
    {
        public SnapshotKey Snapshot { get; } = snapshot;
        public CancellationTokenSource Cancellation { get; } = cancellation;

        /// <summary>Completes once a daily snapshot is stored, or with its failure; null when only a drill runs.</summary>
        public TaskCompletionSource? Stored { get; init; }

        /// <summary>Whether the file's pass has taken in <see cref="Stored"/>.</summary>
        public bool Collected { get; set; }

        public bool Drilling { get; set; }

        public Task Completion { get; set; } = Task.CompletedTask;
    }

    /// <summary>
    /// A sealed segment not yet acknowledged: its key, its sealed bytes (a scratch file), its time, its newest read mark and
    /// whether that mark ends it.
    /// </summary>
    private sealed record Segment(string Key, FileStream Sealed, DateTimeOffset At, DateTimeOffset? Mark, bool EndsMarked);

    /// <summary>One shipped file. The scheduler owns admission fields; its single job owns shipping state.</summary>
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

        /// <summary>The epoch of the shipper lease this member ships the file under, or null.</summary>
        public ulong? Epoch { get; set; }

        /// <summary>Whether this member ships the file now.</summary>
        public bool Shipping { get; set; }
        public string Scope { get; } = scope;
        public IWalTap Tap { get; } = tap;

        /// <summary>Log bytes read and not yet sealed, from <see cref="PendingStart"/>, after <see cref="Unstored"/>.</summary>
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

        /// <summary>The sealed segment being stored: after a failure it is sent again, the same bytes under the same key.</summary>
        public Segment? Unstored { get; set; }

        /// <summary>Scratch bytes of log read and not yet stored, sealed or not.</summary>
        public long Buffered => Pending.Length + (Unstored?.Sealed.Length ?? 0);

        /// <summary>Whether the last read stopped at the tap's budget, leaving committed changes to read.</summary>
        public bool Behind { get; set; }

        /// <summary>The last snapshot upload or drill started beside shipping; only the file's job replaces it.</summary>
        public SideWork? Side { get; set; }

        public Ulid? Generation { get; set; }
        public long Position { get; set; }
        public long PendingStart { get; set; }
        public DateTimeOffset PendingAt { get; set; }

        /// <summary>The newest read mark in <see cref="Pending"/>, or null, and whether the last read there ended with it.</summary>
        public DateTimeOffset? PendingMark { get; set; }

        public bool PendingEndsMarked { get; set; }
        public DateTimeOffset LastSnapshot { get; set; }
        public DateTimeOffset? LastShipped { get; set; }
        public ShippingProgress? PlatformProgress { get; set; }

        /// <summary>Since when committed data waits to reach the bucket; null when none waits.</summary>
        public DateTimeOffset? BehindSince { get; set; } = opened;

        public string? LastError { get; set; }

        /// <summary>Failures in a row, and when uploads may be tried again.</summary>
        public int Failures { get; set; }

        public DateTimeOffset RetryAt { get; set; } = DateTimeOffset.MinValue;

        /// <summary>Daily snapshot uploads failed in a row, when the next may start, and the kind of the last failure.</summary>
        public int SnapshotFailures { get; set; }

        public DateTimeOffset SnapshotRetryAt { get; set; } = DateTimeOffset.MinValue;

        public string? SnapshotError { get; set; }

        /// <summary><see cref="Buffered"/> after the last pass, for status readers on other threads.</summary>
        public long PendingBytes { get; set; }

        /// <summary>Maintenance: last retention pass, measured bucket size, plan retention and last restore drill.</summary>
        public DateTimeOffset RetainedAt { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset SizedAt { get; set; } = DateTimeOffset.MinValue;

        public long? SizeBytes { get; set; }

        // Guarded by locking this file: deletion and the start of a drill cannot race.
        public TaskCompletionSource? Retaining { get; set; }

        public int RetentionDays { get; set; }

        public BackupDrill? LastDrill { get; set; }

        /// <summary>The time of the snapshot the job last asked a drill for.</summary>
        public DateTimeOffset DrilledAt { get; set; } = DateTimeOffset.MinValue;

        /// <summary>Waits for <see cref="Side"/> to end (cancel it first to end it early) and releases it.</summary>
        public async Task StopSideAsync()
        {
            if (Side is { } side)
            {
                await side.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                side.Cancellation.Dispose();
                Side = null;
            }
        }

        /// <summary>Drops every log byte read and not stored.</summary>
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
