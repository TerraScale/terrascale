using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Tenancy;

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
public sealed record BackupFileStatus(
    string File, string? Generation, long Frames, DateTimeOffset? LastShippedAt, double LagSeconds, long PendingBytes, string? LastError, bool Shipping);

/// <summary>Continuous backup on this node: what it ships.</summary>
/// <param name="Shipping">Whether backup is configured on this node.</param>
/// <param name="Files">Every storage file of the node; the ones it ships have <see cref="BackupFileStatus.Shipping"/> set.</param>
public sealed record BackupStatus(bool Shipping, IReadOnlyList<BackupFileStatus> Files);

/// <summary>Ships every storage file of this node to the backup bucket (TS-633).</summary>
public interface IContinuousBackup
{
    /// <summary>What this node ships, and how far each file is.</summary>
    BackupStatus Status { get; }

    /// <summary>When the newest data of <paramref name="database"/> in the bucket was read, if this node ships it.</summary>
    DateTimeOffset? LastShipped(TenantContext database);

    /// <summary>One pass: finds new and deleted files when due, then ships each file once. Passes never overlap.</summary>
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
/// that upload gets expensive. Files ship four at a time in one pass, so a large fleet stretches the one-second interval.
/// </remarks>
internal sealed partial class ContinuousBackup : BackgroundService, IContinuousBackup
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromDays(1);

    // Frames read but not stored, per file, before its generation is abandoned for a fresh snapshot (bounds scratch disk).
    private const long MaxPendingBytes = 256L * 1024 * 1024;

    private readonly IWalBackup _engine;
    private readonly IStorageEngine _storage;
    private readonly IObjectStore _store;
    private readonly DataKeys _keys;
    private readonly TimeProvider _time;
    private readonly ILogger<ContinuousBackup> _logger;
    private readonly IShipperLeases _shippers;
    private readonly string _scratch;
    private readonly ConcurrentDictionary<string, ShippedFile> _files = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly Lock _stopGate = new();
    private Task? _stopping;
    private readonly Counter<long> _uploaded;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _holes;
    private readonly Counter<long> _generations;
    private DateTimeOffset _nextDiscovery = DateTimeOffset.MinValue;

    public ContinuousBackup(
        IWalBackup engine, IStorageEngine storage, IObjectStore store, DataKeys keys, TerraBaseOptions options, TimeProvider time,
        IMeterFactory meters, ILogger<ContinuousBackup> logger, IShipperLeases shippers)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(meters);
        (_engine, _storage, _store, _keys, _time, _logger, _shippers) = (engine, storage, store, keys, time, logger, shippers);
        _scratch = Scratch(options);
        var meter = meters.Create(Telemetry.MeterName);
        _uploaded = meter.CreateCounter<long>("terrabase.backup.uploaded", "By", "Sealed backup bytes stored in the bucket, by kind (snapshot, segment).");
        _failures = meter.CreateCounter<long>("terrabase.backup.failures", description: "Backup passes of a file that failed.");
        _holes = meter.CreateCounter<long>("terrabase.backup.holes", description: "Logs found checkpointed or rewritten by someone else, each ending a generation.");
        _generations = meter.CreateCounter<long>("terrabase.backup.generations", description: "Generations started (each stores a snapshot).");
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
                file.Prefix, file.Generation?.ToString(), file.Position, file.LastShipped, Lag(file, now), file.PendingBytes, file.LastError, file.Shipping))]);
        }
    }

    public DateTimeOffset? LastShipped(TenantContext database) =>
        _files.TryGetValue(BackupLayout.File(database), out var file) && file.Shipping ? file.LastShipped : null;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_time.GetUtcNow() >= _nextDiscovery)
            {
                await DiscoverAsync(cancellationToken).ConfigureAwait(false);
                _nextDiscovery = _time.GetUtcNow() + DiscoveryInterval;
            }

            await Parallel.ForEachAsync(_files.Values, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (file, token) => await ShipAsync(file, token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            _pass.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }

        Directory.CreateDirectory(_scratch);
        using var timer = new PeriodicTimer(Interval, _time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                // A failed discovery must not stop the host; the next tick retries.
                LogPassFailed(_logger, error);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
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
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        // Ship what committed since the last pass, then give the taps' connections back before the engine closes.
        await _pass.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var file in _files.Values)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await ShipAsync(file, cancellationToken).ConfigureAwait(false);
                }

                await file.DisposeAsync().ConfigureAwait(false);
            }

            _files.Clear();
        }
        finally
        {
            _pass.Release();
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
            if (!present.Contains(prefix) && _files.TryRemove(prefix, out _))
            {
                await file.DisposeAsync().ConfigureAwait(false);
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
                var pending = new FileStream(Path.Combine(_scratch, Guid.NewGuid().ToString("N") + ".frames"), FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                _files[prefix] = new ShippedFile(database, prefix, BackupLayout.KeyScope(database), tap, pending, _time.GetUtcNow());
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // A database deleted or failing to open now is retried by the next discovery.
            LogOpenFailed(_logger, prefix, error);
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

            // After a failure, uploads and snapshots wait out a backoff; reads go on, so the log is still checkpointed.
            var due = _time.GetUtcNow() >= file.RetryAt;
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

            if (due)
            {
                (file.LastError, file.Failures) = (null, 0);
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            _failures.Add(1);
            file.LastError = error.GetType().Name;
            file.Failures++;
            file.RetryAt = _time.GetUtcNow() + Backoff(file.Failures);
            LogShipFailed(_logger, file.Prefix, error);
        }
        finally
        {
            file.PendingBytes = file.Pending.Length;
        }
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
            LogShipping(_logger, file.Prefix);
        }

        return true;
    }

    private IEnumerable<ShippedFile> Shipped() => _files.Values.Where(file => file.Shipping);

    // A generation starts with a snapshot; the frames read before it are in it, so they are dropped.
    private async Task StartGenerationAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var generation = Ulid.NewUlid(_time.GetUtcNow());
        var snapshot = ScratchFile(".db");
        try
        {
            file.BehindSince ??= _time.GetUtcNow();
            file.Pending.SetLength(0);
            await file.Tap.SnapshotAsync(Stream.Null, snapshot, cancellationToken).ConfigureAwait(false);
            var at = _time.GetUtcNow();
            await StoreAsync(file, BackupLayout.Snapshot(file.Prefix, at, generation, 0), snapshot, "snapshot", cancellationToken).ConfigureAwait(false);
            (file.Generation, file.Position, file.PendingStart, file.LastSnapshot, file.LastShipped, file.BehindSince) = (generation, 0, 0, at, at, null);
            _generations.Add(1);
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
            WalRead read;
            try
            {
                read = await file.Tap.SnapshotAsync(file.Pending, snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Abandon(file);
                throw;
            }

            if (!read.Continuous)
            {
                // The frames before the snapshot have a hole: this generation ends; the next pass starts one.
                Hole(file);
                return;
            }

            var at = _time.GetUtcNow();
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

            await StoreAsync(file, BackupLayout.Snapshot(file.Prefix, at, file.Generation!.Value, file.Position), snapshot, "snapshot", cancellationToken)
                .ConfigureAwait(false);
            (file.LastSnapshot, file.LastShipped) = (at, at);
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
        var (keyId, dataKey) = await _keys.CurrentAsync(file.Scope, cancellationToken).ConfigureAwait(false);
        await using var sealedObject = new FileStream(ScratchFile(".sealed"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await BackupObject.WriteAsync(content, sealedObject, key, keyId, dataKey, cancellationToken).ConfigureAwait(false);
        sealedObject.Position = 0;
        await _store.PutAsync(key, sealedObject, cancellationToken).ConfigureAwait(false);
        _uploaded.Add(sealedObject.Length, new KeyValuePair<string, object?>("kind", kind));
    }

    private void Hole(ShippedFile file)
    {
        _holes.Add(1);
        LogHole(_logger, file.Prefix);
        Abandon(file);
    }

    // 1 s after the first failure, doubling up to five minutes, like Litestream's sync backoff.
    private static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(failures - 1, 9))));

    private static void Abandon(ShippedFile file)
    {
        file.Generation = null;
        file.Pending.SetLength(0);
    }

    private static double Lag(ShippedFile file, DateTimeOffset now) => file.BehindSince is { } since ? Math.Max(0, (now - since).TotalSeconds) : 0;

    private string ScratchFile(string extension) => Path.Combine(_scratch, Guid.NewGuid().ToString("N") + extension);

    // SQLite may leave a rollback journal next to a snapshot it wrote.
    private static void DeleteSnapshot(string path)
    {
        File.Delete(path);
        File.Delete(path + "-journal");
    }

    private static async Task Use(Stream stream, Func<Stream, Task> use)
    {
        await using (stream.ConfigureAwait(false))
        {
            await use(stream).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _pass.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Continuous backup pass failed")]
    private static partial void LogPassFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Continuous backup cannot open {File}; the next discovery retries")]
    private static partial void LogOpenFailed(ILogger logger, string file, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Continuous backup of {File} failed; the next pass retries")]
    private static partial void LogShipFailed(ILogger logger, string file, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The log of {File} has a hole; its backup starts a new generation")]
    private static partial void LogHole(ILogger logger, string file);

    [LoggerMessage(Level = LogLevel.Information, Message = "This member now ships {File}")]
    private static partial void LogShipping(ILogger logger, string file);

    /// <summary>One shipped file. Only the pass that ships it touches it, so it needs no lock.</summary>
    private sealed class ShippedFile(TenantContext? database, string prefix, string scope, IWalTap tap, FileStream pending, DateTimeOffset opened)
        : IAsyncDisposable
    {
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

        /// <summary>Since when committed data waits to reach the bucket; null when none waits.</summary>
        public DateTimeOffset? BehindSince { get; set; } = opened;

        public string? LastError { get; set; }

        /// <summary>Failures in a row, and when uploads may be tried again.</summary>
        public int Failures { get; set; }

        public DateTimeOffset RetryAt { get; set; } = DateTimeOffset.MinValue;

        /// <summary><see cref="Pending"/>'s length after the last pass, for status readers on other threads.</summary>
        public long PendingBytes { get; set; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Tap.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await Pending.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
