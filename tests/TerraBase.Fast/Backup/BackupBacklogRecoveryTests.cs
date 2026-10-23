using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

/// <summary>
/// A backlog (about 43 MB of change log, more than one 32 MiB read) survives what used to drop or stall it: a short bucket
/// outage, shutdown, a customer destination switch, a failed change-log cleanup, and a due daily snapshot.
/// </summary>
public sealed class BackupBacklogRecoveryTests
{
    private const int Writes = 48;
    private const long ReadBudget = 32L << 20;

    [Test]
    public async Task A_short_outage_during_a_backlog_keeps_the_generation_and_leaves_the_backlog_in_the_change_log(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var generation = host.Status().Generation!;
        var texts = await WriteBacklogAsync(host, cancellationToken);

        // Segment uploads fail for ten seconds: the first read's segment waits, and the rest of the backlog stays unread.
        host.S3.FailPut = key => key.StartsWith($"{host.File}/wal/", StringComparison.Ordinal);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var buffered = host.Status().PendingBytes;
        await Assert.That(buffered).IsGreaterThan(0);
        for (var pass = 0; pass < 10; pass++)
        {
            await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
            var status = host.Status();
            await Assert.That(status.Generation).IsEqualTo(generation);
            await Assert.That(status.LastError).IsNotNull();
            await Assert.That(status.PendingBytes).IsEqualTo(buffered);
            await Assert.That(status.LagSeconds).IsGreaterThan(0);
        }

        host.S3.FailPut = _ => false;
        await CatchUpAsync(host, cancellationToken);
        await Assert.That(host.Status().Generation).IsEqualTo(generation);
        await Assert.That(await host.MetricsAsync(cancellationToken)).DoesNotContain("terrabase_backup_abandoned");
        var segments = host.Segments(generation);
        await BackupShippingHost.AssertContiguousAsync(segments);
        await AssertRestoredAsync(host, segments[^1].At, "after-outage", texts, cancellationToken);
    }

    [Test]
    public async Task A_sustained_outage_during_a_backlog_drops_the_generation_counts_it_and_recovers_from_a_new_snapshot(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var generation = host.Status().Generation!;
        var texts = await WriteBacklogAsync(host, cancellationToken);

        // Every retry fails, five minutes apart, until the backoff reaches its cap; the backlog stays in the change log.
        host.S3.FailPut = key => key.StartsWith($"{host.File}/wal/", StringComparison.Ordinal);
        for (var retry = 0; retry < 10; retry++)
        {
            await host.ShipAsync(TimeSpan.FromMinutes(5), cancellationToken);
            await Assert.That(host.Status().Generation).IsEqualTo(generation);
        }

        await Assert.That(await host.MetricsAsync(cancellationToken)).DoesNotContain("terrabase_backup_abandoned");

        // A sustained outage: the next pass drops the generation, so reads trim the change log again; it is counted.
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        host.Time.Advance(TimeSpan.FromSeconds(1));
        var dropped = host.Status();
        await Assert.That(dropped.Generation).IsNull();
        await Assert.That(dropped.PendingBytes).IsEqualTo(0);
        await Assert.That(dropped.LagSeconds).IsGreaterThan(0);
        await Assert.That(await host.MetricsAsync(cancellationToken)).Contains("reason=\"outage\"");

        host.S3.FailPut = _ => false;
        await host.ShipAsync(TimeSpan.FromMinutes(5), cancellationToken);
        var recovered = host.Status();
        await Assert.That(recovered.Generation).IsNotNull();
        await Assert.That(recovered.Generation).IsNotEqualTo(generation);
        await AssertRestoredAsync(host, recovered.LastShippedAt!.Value, "after-sustained-outage", texts, cancellationToken);
    }

    [Test]
    public async Task Shutdown_ships_the_whole_backlog_not_one_read(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync(services =>
            services.AddSingleton<IWalBackup>(provider => new WatchedBackup(provider.GetRequiredService<TursoStorageEngine>())));
        var engine = (WatchedBackup)host.Api.Server.Services.GetRequiredService<IWalBackup>();
        engine.Watch(host.Database.Id);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        // Finish initial retention before the backlog, so shutdown's budget measures draining it.
        await host.ShipAsync(TimeSpan.Zero, cancellationToken);
        var generation = host.Status().Generation!;
        await WriteBacklogAsync(host, cancellationToken);

        var trace = new ShutdownTrace();
        var status = host.Status();
        var tenantPrefix = host.File + "/";
        trace.Record("pre_stop", cancellationToken, string.Create(CultureInfo.InvariantCulture,
            $"generation_present={status.Generation is not null} shipping={status.Shipping} log_bytes={status.LogBytes} pending_bytes={status.PendingBytes} lag_seconds={status.LagSeconds:F3} error_present={status.LastError is not null} previous_read_bytes={engine.LastRead.Bytes} previous_read_remaining={engine.LastRead.Remaining}"));
        engine.Trace = trace;
        host.S3.BeforePut = (key, token) => Request("put.enter", key, token);
        host.S3.BeforePart = (key, part, token) => Request("part.enter", key, token, part);
        host.S3.BeforeList = (prefix, token) => Request("list.enter", prefix, token);
        try
        {
            var started = Stopwatch.GetTimestamp();
            trace.Record("stop.enter", cancellationToken);
            try
            {
                await ((IHostedService)host.Api.Server.Services.GetRequiredService<IContinuousBackup>()).StopAsync(cancellationToken);
            }
            catch (Exception error)
            {
                trace.Record("stop.exception", cancellationToken, $"exception_type={error.GetType().Name}");
                throw;
            }
            finally
            {
                trace.Record("stop.exit", cancellationToken, string.Create(CultureInfo.InvariantCulture,
                    $"duration_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}"));
            }

            // The last read before shutdown found nothing left, and every read went into a stored segment.
            var segments = host.Segments(generation);
            trace.Record("post_stop", cancellationToken, string.Create(CultureInfo.InvariantCulture,
                $"segments={segments.Count} last_read_bytes={engine.LastRead.Bytes} last_read_continuous={engine.LastRead.Continuous} last_read_remaining={engine.LastRead.Remaining}"));
            var diagnostics = trace.Report();
            await Assert.That(engine.LastRead.Remaining).IsFalse().Because(diagnostics);
            await Assert.That(segments.Count).IsGreaterThanOrEqualTo(2).Because(diagnostics);
            await BackupShippingHost.AssertContiguousAsync(segments);
        }
        finally
        {
            engine.Trace = null;
            host.S3.BeforePut = null;
            host.S3.BeforePart = null;
            host.S3.BeforeList = null;
            TUnit.Core.TestContext.Current!.OutputWriter.WriteLine(trace.Report());
        }

        Task Request(string operation, string key, CancellationToken token, int? part = null)
        {
            var scope = key.StartsWith(tenantPrefix, StringComparison.Ordinal) ? "tenant"
                : key.StartsWith("v1/_system/", StringComparison.Ordinal) ? "system" : "other";
            var kind = key.Contains("/wal/", StringComparison.Ordinal) ? "segment"
                : key.Contains("/snapshots/", StringComparison.Ordinal) ? "snapshot" : "other";
            trace.Record("s3." + operation, token, string.Create(CultureInfo.InvariantCulture, $"scope={scope} kind={kind} part={part}"));
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task A_customer_destination_switch_during_a_backlog_reads_it_before_the_snapshot_holds_writers(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync(services =>
            services.AddSingleton<IWalBackup>(provider => new WatchedBackup(provider.GetRequiredService<TursoStorageEngine>())));
        var engine = (WatchedBackup)host.Api.Server.Services.GetRequiredService<IWalBackup>();
        engine.Watch(host.Database.Id);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await WriteBacklogAsync(host, cancellationToken);
        using (var saved = await host.Api.Client.PutAsJsonAsync(ManagementApi.Uri(host.Database.ProjectPath + "/backup-destination"), new
        {
            endpoint = FakeS3.CustomerEndpoint,
            region = "test-region-1",
            bucket = FakeS3.CustomerBucket,
            accessKeyId = FakeS3.CustomerAccessKey,
            secretKey = FakeS3.CustomerSecretKey,
            mode = "customer",
        }, cancellationToken))
        {
            saved.EnsureSuccessStatusCode();
        }

        // The next pass that sees the destination starts a customer generation; its snapshot reads only what came since.
        for (var pass = 0; pass < 5 && !host.S3.CustomerObjects.Keys.Any(key => key.StartsWith($"{host.File}/snapshots/", StringComparison.Ordinal)); pass++)
        {
            await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        await Assert.That(host.S3.CustomerObjects.Keys.Any(key => key.StartsWith($"{host.File}/snapshots/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(engine.SnapshotReads.Count).IsEqualTo(2);
        await Assert.That(engine.SnapshotReads.Last()).IsLessThan(ReadBudget);
    }

    [Test]
    public async Task A_failed_change_log_cleanup_puts_the_anchor_back_and_the_next_pass_ships_the_same_changes(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync(services =>
            services.AddSingleton<IWalBackup>(provider => new WatchedBackup(provider.GetRequiredService<TursoStorageEngine>())));
        var engine = (WatchedBackup)host.Api.Server.Services.GetRequiredService<IWalBackup>();
        engine.Watch(host.Database.Id);
        var faker = new Faker();
        var before = faker.Lorem.Sentence(8);
        var after = faker.Lorem.Sentence(8);
        await host.PutAsync("people", "before", before, cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var generation = host.Status().Generation!;

        // The read succeeds, then the real cleanup on the writer fails: the tap must not keep its new anchor.
        engine.FailNextTrim();
        await host.PutAsync("people", "after", after, cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        await Assert.That(engine.TrimFailures).IsEqualTo(1);
        var failed = host.Status();
        await Assert.That(failed.Generation).IsEqualTo(generation);
        await Assert.That(failed.LastError).IsEqualTo(nameof(IOException));
        await Assert.That(failed.PendingBytes).IsEqualTo(0);

        await host.ShipAsync(TimeSpan.FromSeconds(2), cancellationToken);
        var shipped = host.Status();
        await Assert.That(shipped.Generation).IsEqualTo(generation);
        await Assert.That(shipped.LagSeconds).IsEqualTo(0);
        await Assert.That(await host.MetricsAsync(cancellationToken)).DoesNotContain("terrabase_backup_holes");
        await host.RestoreAsync(shipped.LastShippedAt!.Value, "after-cleanup", cancellationToken);
        await Assert.That(await host.ReadAsync("after-cleanup", "people", "before", cancellationToken)).IsEqualTo(before);
        await Assert.That(await host.ReadAsync("after-cleanup", "people", "after", cancellationToken)).IsEqualTo(after);
    }

    [Test]
    public async Task A_daily_snapshot_put_off_by_a_backlog_is_counted_and_taken_once_the_backlog_drains(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var snapshots = $"{host.File}/snapshots/";
        await WriteBacklogAsync(host, cancellationToken);

        await host.ShipAsync(TimeSpan.FromDays(1), cancellationToken);
        await Assert.That(host.S3.Objects.Keys.Count(key => key.StartsWith(snapshots, StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(await host.MetricsAsync(cancellationToken)).Contains("terrabase_backup_snapshots_deferred_total");

        await CatchUpAsync(host, cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        await Assert.That(host.S3.Objects.Keys.Count(key => key.StartsWith(snapshots, StringComparison.Ordinal))).IsEqualTo(2);
    }

    // Writes a backlog of distinct documents, each about 900 KB; returns their texts by index.
    private static async Task<string[]> WriteBacklogAsync(BackupShippingHost host, CancellationToken cancellationToken)
    {
        var bulk = new Faker().Random.String2(900_000, "abcdefghijklmnopqrstuvwxyz");
        var texts = Enumerable.Range(0, Writes).Select(write => write.ToString("D4", CultureInfo.InvariantCulture) + bulk[4..]).ToArray();
        for (var write = 0; write < Writes; write++)
        {
            await host.PutAsync("load", $"doc-{write}", texts[write], cancellationToken);
        }

        return texts;
    }

    // Passes, one second apart, until nothing waits; the retry after an outage can be up to its backoff away.
    private static async Task CatchUpAsync(BackupShippingHost host, CancellationToken cancellationToken)
    {
        for (var pass = 0; pass < 60; pass++)
        {
            await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
            if (host.Status() is { LagSeconds: 0, PendingBytes: 0, LastError: null })
            {
                return;
            }
        }

        await Assert.That(host.Status().LagSeconds).IsEqualTo(0);
    }

    private static async Task AssertRestoredAsync(BackupShippingHost host, DateTimeOffset at, string name, string[] texts, CancellationToken cancellationToken)
    {
        await host.RestoreAsync(at, name, cancellationToken);
        for (var write = 0; write < texts.Length; write++)
        {
            await Assert.That(await host.ReadAsync(name, "load", $"doc-{write}", cancellationToken) == texts[write]).IsTrue();
        }
    }

    private sealed class ShutdownTrace
    {
        private const int Limit = 96;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly ConcurrentQueue<(int Sequence, string Text)> _events = new();
        private int _observed;

        public void Record(string phase, CancellationToken token, string details = "")
        {
            var sequence = Interlocked.Increment(ref _observed);
            if (sequence <= Limit)
            {
                _events.Enqueue((sequence, string.Create(CultureInfo.InvariantCulture,
                    $"{sequence:D2} utc={DateTimeOffset.UtcNow:O} elapsed_ms={Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F3} phase={phase} cancelled={token.IsCancellationRequested} {details}")));
            }
        }

        public string Report() => "Shutdown backlog diagnostics (before_trim.return ends the hook, not cleanup; S3 markers are request entry only)\n"
            + string.Join('\n', _events.OrderBy(item => item.Sequence).Select(item => item.Text))
            + string.Create(CultureInfo.InvariantCulture, $"\ndropped_events={Math.Max(0, Volatile.Read(ref _observed) - Limit)}");
    }

    // Watches one database's tap: its reads and snapshot reads, and fails its change-log cleanup once when asked.
    private sealed class WatchedBackup(IWalBackup engine) : IWalBackup
    {
        private string? _watched;
        private int _failTrim;
        private int _trimFailures;

        public ConcurrentQueue<long> SnapshotReads { get; } = new();

        public int TrimFailures => Volatile.Read(ref _trimFailures);

        public ShutdownTrace? Trace { get; set; }

        // Written by the file's job; the test reads it only after shipping has stopped.
        public WalRead LastRead { get; set; }

        public void Watch(string database) => Volatile.Write(ref _watched, database);

        public void FailNextTrim() => Volatile.Write(ref _failTrim, 1);

        public async ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken)
        {
            var tap = await engine.OpenTapAsync(database, cancellationToken);
            if (tap is null || database?.DatabaseId != Volatile.Read(ref _watched))
            {
                return tap;
            }

            if (tap is TursoChangeTap turso)
            {
                turso.BeforeTrim = token =>
                {
                    var trace = Trace;
                    trace?.Record("tenant.before_trim.enter", token);
                    if (Interlocked.Exchange(ref _failTrim, 0) == 1)
                    {
                        Interlocked.Increment(ref _trimFailures);
                        trace?.Record("tenant.before_trim.exception", token, "injected_io_failure=true");
                        throw new IOException("the change log cleanup failed");
                    }

                    trace?.Record("tenant.before_trim.return", token);
                    return Task.CompletedTask;
                };
            }

            return new WatchedTap(tap, this);
        }

        public ValueTask<bool> RollForwardAsync(string file, IAsyncEnumerable<Stream> segments, CancellationToken cancellationToken) =>
            engine.RollForwardAsync(file, segments, cancellationToken);

        public ValueTask ImportDatabaseAsync(TenantContext database, string file, CancellationToken cancellationToken) =>
            engine.ImportDatabaseAsync(database, file, cancellationToken);

        public ValueTask<byte[]> ChecksumAsync(string file, CancellationToken cancellationToken) => engine.ChecksumAsync(file, cancellationToken);

        public LogCut CutLog(Stream segment, DateTimeOffset at, long? previous) => engine.CutLog(segment, at, previous);

        public DateTimeOffset? ReadMark(Stream segment) => engine.ReadMark(segment);
    }

    private sealed class WatchedTap(IWalTap tap, WatchedBackup owner) : IWalTap
    {
        public async ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken)
        {
            if (owner.Trace is not { } trace)
            {
                var read = await tap.ReadAsync(destination, cancellationToken);
                owner.LastRead = read;
                return read;
            }

            var started = Stopwatch.GetTimestamp();
            var appended = destination.Length;
            trace.Record("tenant.read.enter", cancellationToken);
            using var cancellation = cancellationToken.Register(() => trace.Record("tenant.read.token_cancelled", cancellationToken));
            try
            {
                var read = await tap.ReadAsync(destination, cancellationToken);
                owner.LastRead = read;
                trace.Record("tenant.read.return", cancellationToken, string.Create(CultureInfo.InvariantCulture,
                    $"duration_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3} appended_bytes={destination.Length - appended} read_bytes={read.Bytes} continuous={read.Continuous} remaining={read.Remaining}"));
                return read;
            }
            catch (Exception error)
            {
                trace.Record("tenant.read.exception", cancellationToken, string.Create(CultureInfo.InvariantCulture,
                    $"duration_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3} appended_bytes={destination.Length - appended} exception_type={error.GetType().Name}"));
                throw;
            }
        }

        public async ValueTask<WalSnapshot> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken)
        {
            var snapshot = await tap.SnapshotAsync(destination, snapshotPath, cancellationToken);
            owner.SnapshotReads.Enqueue(snapshot.Read.Bytes);
            return snapshot;
        }

        public ValueTask DisposeAsync() => tap.DisposeAsync();
    }
}
