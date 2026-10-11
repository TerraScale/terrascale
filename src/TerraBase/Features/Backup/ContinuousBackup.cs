using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Backup;

/// <summary>Where one storage file's backup stands on this node.</summary>
/// <param name="File">The file's directory in the bucket (<see cref="BackupLayout.File"/>).</param>
/// <param name="Generation">The generation being shipped; null until its first snapshot is stored.</param>
/// <param name="Frames">Frames of the generation read so far.</param>
/// <param name="LastShippedAt">When the newest data in the bucket was read from the file.</param>
/// <param name="LagSeconds">Seconds the oldest data not yet in the bucket has waited; 0 when none waits.</param>
/// <param name="PendingBytes">Frames read and not yet stored.</param>
/// <param name="LastError">The kind of the last failure, cleared by the next successful pass.</param>
/// <param name="Shipping">Whether this node ships the file now (<see cref="IShipperLeases"/>).</param>
/// <param name="SizeBytes">Bytes the file's backup occupies in the bucket, measured daily by its shipper.</param>
/// <param name="LastDrill">The last restore drill this member ran on the file.</param>
public sealed record BackupFileStatus(
    string File, string? Generation, long Frames, DateTimeOffset? LastShippedAt, double LagSeconds, long PendingBytes, string? LastError, bool Shipping,
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
/// Litestream-style continuous backup (<c>docs/backup.md</c>). Once a second each file's newly committed log frames are
/// sealed and stored as one segment; each file starts a generation with a snapshot when this member starts shipping it,
/// after a hole, and daily. Restoring a time takes the newest snapshot at or before it and its generation's segments up to
/// it. Every member taps every file and checkpoints it; one ships it, chosen by <see cref="IShipperLeases"/>.
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

    // Frames read but not stored, per file, before its generation is abandoned for a fresh snapshot (bounds scratch disk).
    private const long MaxPendingBytes = 256L * 1024 * 1024;
    private const int MaxJobs = 4;
    private const int MaxCustomerJobs = 2;

    private readonly IWalBackup _engine;
    private readonly IStorageEngine _storage;
    private readonly BackupTargets _targets;
    private readonly TimeProvider _time;
    private readonly ILogger<ContinuousBackup> _logger;
    private readonly IShipperLeases _shippers;
    private readonly ITenantDirectory _tenants;
    private readonly BackupSettings _settings;
    private readonly string _node;
    private readonly string _scratch;
    private readonly ConcurrentDictionary<string, ShippedFile> _files = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _pass = new(1, 1);
    // Only the scheduling gate owns admission and the counter; jobs alone own their file's shipping state.
    private readonly List<ShippingJob> _jobs = [with(capacity: MaxJobs)];
    private readonly CancellationTokenSource _jobCancellation = new();
    private long _scheduleCounter;
    private volatile bool _stopped;
    private readonly Lock _stopGate = new();
    private Task? _stopping;
    private readonly Counter<long> _uploaded;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _holes;
    private readonly Counter<long> _generations;
    private readonly Counter<long> _drills;
    private DateTimeOffset _nextDiscovery = DateTimeOffset.MinValue;

    public ContinuousBackup(
        IWalBackup engine, IStorageEngine storage, BackupTargets targets, TerraBaseOptions options, TimeProvider time,
        IMeterFactory meters, ILogger<ContinuousBackup> logger, IShipperLeases shippers, ITenantDirectory tenants, BackupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(meters);
        (_engine, _storage, _targets, _time, _logger, _shippers) = (engine, storage, targets, time, logger, shippers);
        (_tenants, _settings, _node) = (tenants, settings, options.NodeName);
        _scratch = Scratch(options);
        var meter = meters.Create(Telemetry.MeterName);
        _uploaded = meter.CreateCounter<long>("terrabase.backup.uploaded", "By", "Sealed backup bytes stored in the bucket, by kind (snapshot, segment).");
        _failures = meter.CreateCounter<long>("terrabase.backup.failures", description: "Backup passes of a file that failed.");
        _holes = meter.CreateCounter<long>("terrabase.backup.holes", description: "Logs found checkpointed or rewritten by someone else, each ending a generation.");
        _generations = meter.CreateCounter<long>("terrabase.backup.generations", description: "Generations started (each stores a snapshot).");
        _drills = meter.CreateCounter<long>("terrabase.backup.drills", description: "Restore drills, by result (passed, failed).");
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
            if (_stopped)
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
                    next.PendingBytes = next.Pending.Length;
                    continue;
                }

                schedulingToken.ThrowIfCancellationRequested();
                // A formerly platform file can become customer work during the refresh.
                if (!CanAdmit(next))
                {
                    continue;
                }

                var job = new ShippingJob(next, CustomerWork(next), CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _jobCancellation.Token));
                next.Job = job;
                _jobs.Add(job);
                job.Completion = Task.Run(() => ShipAsync(job.File, job.Cancellation.Token), CancellationToken.None);
                pending?.Add(job.Completion);
            }

            return boundary;
        }
        finally
        {
            _pass.Release();
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
        // Known platform files flush before cached customer lookups can consume the shutdown budget.
        // Then refresh cached customer files for detachments, and only then restart customer I/O.
        for (var phase = 0; phase < 3; phase++)
        {
            foreach (var (_, file) in _files)
            {
                if (file.Removed || cancellationToken.IsCancellationRequested)
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

                    await ShipAsync(file, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    errors.Add(error);
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
            await OpenAsync(database, present, cancellationToken).ConfigureAwait(false);
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
                FileStream pending;
                try
                {
                    pending = new FileStream(Path.Combine(_scratch, EntityIds.New().ToString() + ".frames"), FileMode.CreateNew, FileAccess.ReadWrite,
                        FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
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
            if (!await HoldAsync(file, cancellationToken).ConfigureAwait(false))
            {
                // Another member ships this file: read only to checkpoint it.
                await file.Tap.ReadAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
                file.LastError = null;
                return;
            }

            // Admission already classified the catalog record. Only this job may switch or dispose its fixed target;
            // failed catalog or Vault reads fail closed: customer-only data never falls back to platform.
            var due = _time.GetUtcNow() >= file.RetryAt;
            if (due)
            {
                if (file.Target.Revision != (file.Configuration?.Revision ?? BackupDestinationNames.Platform))
                {
                    var target = await _targets.ShippingAsync(file.Database, file.Configuration, cancellationToken).ConfigureAwait(false);
                    file.Target.Dispose();
                    file.Target = target;
                    Abandon(file);
                    (file.LastShipped, file.LastSnapshot, file.RetainedAt, file.DrilledAt, file.SizeBytes, file.SizedAt) = (null, default, default, default, null, default);
                }
            }
            if (file.Generation is null)
            {
                if (due)
                {
                    await StartGenerationAsync(file, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await file.Tap.ReadAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            WalRead read;
            try
            {
                read = await file.Tap.ReadAsync(file.Pending, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The tap may have moved past frames that are now only partly in Pending: start over from a snapshot.
                Abandon(file);
                throw;
            }

            if (!read.Continuous)
            {
                Hole(file);
                if (due)
                {
                    await StartGenerationAsync(file, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            var now = _time.GetUtcNow();
            if (read.Frames > 0)
            {
                file.Position += read.Frames;
                file.PendingAt = now;
                file.BehindSince ??= now;
            }

            if (file.Pending.Length > MaxPendingBytes)
            {
                // The bucket has been unreachable too long: drop the frames and start over with a snapshot once it answers.
                Abandon(file);
                return;
            }

            if (due && file.Pending.Length > 0)
            {
                await UploadPendingAsync(file, cancellationToken).ConfigureAwait(false);
            }

            if (due && now - file.LastSnapshot >= SnapshotInterval)
            {
                await SnapshotAsync(file, cancellationToken).ConfigureAwait(false);
            }

            if (due && file.Generation is not null && now - file.RetainedAt >= RetentionInterval)
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

            if (due)
            {
                (file.LastError, file.Failures) = (null, 0);
            }
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

            file.PendingBytes = file.Pending.Length;
        }
    }

    private void Failed(ShippedFile file, Exception error)
    {
        // A partial mirror acknowledgement must not be retried as an expanding, overlapping segment.
        if (file.Target.Revision != BackupDestinationNames.Platform)
        {
            Abandon(file);
        }

        if (error is ObjectDisposedException)
        {
            // The store closed under this read: its database was deleted or the engine stops. The job ends the file, so the
            // next discovery opens a new tap if the database exists again, rather than reading the closed store forever.
            file.Removed = true;
            _logger.ZLogDebug($"Continuous backup of {file.Prefix:@File} stopped: its database closed while it was read; the next discovery reopens it if it exists");
            return;
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

    // A generation starts with a snapshot; the frames read before it are in it, so they are dropped.
    private async Task StartGenerationAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var generation = EntityIds.New(_time);
        var snapshot = ScratchFile(".db");
        try
        {
            file.BehindSince ??= _time.GetUtcNow();
            file.Pending.SetLength(0);
            var captured = await file.Tap.SnapshotAsync(Stream.Null, snapshot, cancellationToken).ConfigureAwait(false);
            var at = captured.At;
            var key = BackupLayout.Snapshot(file.Prefix, at, generation, 0);
            await StoreAsync(file, key, snapshot, "snapshot", cancellationToken).ConfigureAwait(false);
            (file.Generation, file.Position, file.PendingStart, file.LastSnapshot, file.LastShipped, file.BehindSince) = (generation, 0, 0, at, at, null);
            AcknowledgePlatform(file, at);
            _generations.Add(1);
            if (at - file.DrilledAt >= DrillInterval)
            {
                await DrillAsync(file, new SnapshotKey(key, at, generation, 0), snapshot, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            DeleteSnapshot(snapshot);
        }
    }

    // A later snapshot of the same generation: the frames before it are stored first, so the next segment starts at it.
    private async Task SnapshotAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var snapshot = ScratchFile(".db");
        try
        {
            WalSnapshot captured;
            try
            {
                captured = await file.Tap.SnapshotAsync(file.Pending, snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Abandon(file);
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
            file.Position += read.Frames;
            if (read.Frames > 0)
            {
                file.PendingAt = at;
                file.BehindSince ??= at;
            }

            if (file.Pending.Length > 0)
            {
                await UploadPendingAsync(file, cancellationToken).ConfigureAwait(false);
            }

            var key = BackupLayout.Snapshot(file.Prefix, at, file.Generation!.Value, file.Position);
            await StoreAsync(file, key, snapshot, "snapshot", cancellationToken).ConfigureAwait(false);
            (file.LastSnapshot, file.LastShipped) = (at, at);
            AcknowledgePlatform(file, at);
            if (at - file.DrilledAt >= DrillInterval)
            {
                await DrillAsync(file, new SnapshotKey(key, at, file.Generation.Value, file.Position), snapshot, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            DeleteSnapshot(snapshot);
        }
    }

    private async Task UploadPendingAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        file.Pending.Position = 0;
        try
        {
            await StoreAsync(file, BackupLayout.Segment(file.Prefix, file.Generation!.Value, file.PendingStart, file.PendingAt), file.Pending, "segment",
                cancellationToken).ConfigureAwait(false);
            file.Pending.SetLength(0);
            (file.PendingStart, file.LastShipped, file.BehindSince) = (file.Position, file.PendingAt, null);
            AcknowledgePlatform(file, file.PendingAt);
        }
        finally
        {
            file.Pending.Seek(0, SeekOrigin.End);
        }
    }

    private Task StoreAsync(ShippedFile file, string key, string path, string kind, CancellationToken cancellationToken) =>
        Use(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan),
            content => StoreAsync(file, key, content, kind, cancellationToken));

    private async Task StoreAsync(ShippedFile file, string key, Stream content, string kind, CancellationToken cancellationToken)
    {
        var (keyId, dataKey) = await file.Target.Keys.CurrentAsync(file.Scope, cancellationToken).ConfigureAwait(false);
        await using var sealedObject = new FileStream(ScratchFile(".sealed"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await BackupObject.WriteAsync(content, sealedObject, key, keyId, dataKey, cancellationToken).ConfigureAwait(false);
        sealedObject.Position = 0;
        await file.Target.Store.PutAsync(key, sealedObject, cancellationToken).ConfigureAwait(false);
        _uploaded.Add(sealedObject.Length, new KeyValuePair<string, object?>("kind", kind));
    }

    private void Hole(ShippedFile file)
    {
        _holes.Add(1);
        _logger.ZLogWarning($"The log of {file.Prefix:@File} has a hole; its backup starts a new generation");
        Abandon(file);
    }

    // 1 s after the first failure, doubling up to five minutes, like Litestream's sync backoff.
    private static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(failures - 1, 9))));

    private static void Abandon(ShippedFile file)
    {
        file.PlatformProgress = null;
        file.Generation = null;
        file.Pending.SetLength(0);
    }

    // Publish generation and time together only after the fixed target acknowledges all its destinations.
    private static void AcknowledgePlatform(ShippedFile file, DateTimeOffset at) =>
        file.PlatformProgress = file.Target.IncludesPlatform ? new(file.Generation!.Value, at) : null;

    private static double Lag(ShippedFile file, DateTimeOffset now) => file.BehindSince is { } since ? Math.Max(0, (now - since).TotalSeconds) : 0;

    private string ScratchFile(string extension) => Path.Combine(_scratch, EntityIds.New().ToString() + extension);

    // SQLite may leave a journal, and a drill a log, next to a snapshot.
    private static void DeleteSnapshot(string path) => BackupReader.Delete(path);

    private static async Task Use(Stream stream, Func<Stream, Task> use)
    {
        await using (stream.ConfigureAwait(false))
        {
            await use(stream).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _jobCancellation.Dispose();
        _pass.Dispose();
        base.Dispose();
    }

    private sealed class ShippingJob(ShippedFile file, bool customer, CancellationTokenSource cancellation)
    {
        public ShippedFile File { get; } = file;
        public bool Customer { get; } = customer;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Completion { get; set; } = Task.CompletedTask;
        public Task? Cancelling { get; set; }
    }

    private sealed record ShippingProgress(Ulid Generation, DateTimeOffset At);

    /// <summary>One shipped file. The scheduler owns admission fields; its single job owns shipping state.</summary>
    private sealed class ShippedFile(TenantContext? database, string prefix, string scope, IWalTap tap, FileStream pending, DateTimeOffset opened, BackupTarget target)
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

        /// <summary>Frames read and not yet stored, from <see cref="PendingStart"/>.</summary>
        public FileStream Pending { get; } = pending;

        public Ulid? Generation { get; set; }
        public long Position { get; set; }
        public long PendingStart { get; set; }
        public DateTimeOffset PendingAt { get; set; }
        public DateTimeOffset LastSnapshot { get; set; }
        public DateTimeOffset? LastShipped { get; set; }
        public ShippingProgress? PlatformProgress { get; set; }

        /// <summary>Since when committed data waits to reach the bucket; null when none waits.</summary>
        public DateTimeOffset? BehindSince { get; set; } = opened;

        public string? LastError { get; set; }

        /// <summary>Failures in a row, and when uploads may be tried again.</summary>
        public int Failures { get; set; }

        public DateTimeOffset RetryAt { get; set; } = DateTimeOffset.MinValue;

        /// <summary><see cref="Pending"/>'s length after the last pass, for status readers on other threads.</summary>
        public long PendingBytes { get; set; }

        /// <summary>Maintenance: last retention pass, measured bucket size, plan retention and last restore drill.</summary>
        public DateTimeOffset RetainedAt { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset SizedAt { get; set; } = DateTimeOffset.MinValue;

        public long? SizeBytes { get; set; }

        public int RetentionDays { get; set; }

        public BackupDrill? LastDrill { get; set; }

        public DateTimeOffset DrilledAt { get; set; } = DateTimeOffset.MinValue;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Tap.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
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
