using System.Collections.Concurrent;
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
        // Finish initial retention before the backlog.
        await host.ShipAsync(TimeSpan.Zero, cancellationToken);
        var generation = host.Status().Generation!;
        host.Time.Advance(TimeSpan.FromSeconds(1));
        var texts = await WriteBacklogAsync(host, cancellationToken);
        // Read the real encrypted log outside shutdown's deadline; Stop still seals and uploads every original chunk.
        var reads = await engine.StageAsync(cancellationToken);
        await Assert.That(reads.Length).IsEqualTo(2);
        await Assert.That(reads[0].Bytes).IsGreaterThanOrEqualTo(ReadBudget);
        await Assert.That(reads[0].Remaining).IsTrue();
        await Assert.That(reads[1].Remaining).IsFalse();
        await Assert.That(reads.All(read => read.Continuous && read.Bytes > 0)).IsTrue();
        await Assert.That(host.Segments(generation)).IsEmpty();

        await ((IHostedService)host.Api.Server.Services.GetRequiredService<IContinuousBackup>()).StopAsync(cancellationToken);

        var segments = host.Segments(generation);
        await Assert.That(engine.DeliveredReads).IsEqualTo(reads.Length);
        await Assert.That(engine.LastRead.Remaining).IsFalse();
        await Assert.That(segments.Count).IsEqualTo(reads.Length);
        await BackupShippingHost.AssertContiguousAsync(segments);
        await Assert.That(segments[1].Start).IsEqualTo(reads[0].Bytes);
        await AssertRestoredAsync(host, segments[^1].At, "after-shutdown", texts, cancellationToken);
    }

    [Test]
    public async Task Shutdown_deadline_keeps_the_acknowledged_segment_restorable_and_cancels_the_next_upload(CancellationToken cancellationToken)
    {
        var host = await BackupShippingHost.StartAsync();
        Exception? failure = null;
        try
        {
            await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await host.ShipAsync(TimeSpan.Zero, cancellationToken);
            var generation = host.Status().Generation!;
            var prefix = $"{host.File}/wal/{generation}/";
            var text = new Faker().Lorem.Sentence();
            await host.PutAsync("people", "acknowledged", text, cancellationToken);
            // Retain a genuine marked segment for Stop to retry before reading the next write.
            host.S3.FailPut = key => key.StartsWith(prefix, StringComparison.Ordinal);
            await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
            await Assert.That(host.Status().LastError).IsNotNull();
            await Assert.That(host.Segments(generation)).IsEmpty();
            host.S3.FailPut = _ => false;
            await host.PutAsync("people", "unacknowledged", new Faker().Lorem.Sentence(), cancellationToken);
            host.Time.Advance(TimeSpan.FromSeconds(2));
            var now = host.Time.GetUtcNow();
            var backup = host.Api.Server.Services.GetRequiredService<IContinuousBackup>();
            var blocked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            string? acknowledged = null;
            byte[]? stored = null;
            Task? stop = null;
            host.S3.BeforePut = async (key, token) =>
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return;
                }

                if (acknowledged is null)
                {
                    acknowledged = key;
                    return;
                }

                stored = [.. host.S3.Objects[acknowledged]];
                blocked.TrySetResult(key);
                try
                {
                    await release.Task.WaitAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            };
            try
            {
                stop = ((IHostedService)backup).StopAsync(shutdown.Token);
                // This watchdog is not the shipping budget: only production's real four-second timer can release the upload.
                await stop.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
                await Assert.That(blocked.Task.IsCompletedSuccessfully).IsTrue();
                await Assert.That(cancelled.Task.IsCompletedSuccessfully).IsTrue();
                await Assert.That(release.Task.IsCompleted).IsFalse();
                await Assert.That(shutdown.IsCancellationRequested).IsFalse();
                await Assert.That(host.Time.GetUtcNow()).IsEqualTo(now);
                await Assert.That(backup.Status.Files).IsEmpty();
                await Assert.That(Directory.EnumerateFiles(Path.Combine(host.Api.Server.DataDirectory, ".backup"))).IsEmpty();
                await Assert.That(host.S3.Objects.ContainsKey(await blocked.Task)).IsFalse();
                var segment = host.Segments(generation).Single();
                await Assert.That(segment.Key).IsEqualTo(acknowledged);
                await Assert.That(host.S3.Objects[segment.Key].AsSpan().SequenceEqual(stored)).IsTrue();
                await host.RestoreAsync(segment.At, "deadline-acknowledged", cancellationToken);
                await Assert.That(await host.ReadAsync("deadline-acknowledged", "people", "acknowledged", cancellationToken)).IsEqualTo(text);
                await Assert.That(await host.ReadAsync("deadline-acknowledged", "people", "unacknowledged", cancellationToken)).IsNull();
            }
            finally
            {
                await shutdown.CancelAsync();
                release.TrySetResult();
                if (stop is not null)
                {
                    await stop.WaitAsync(TimeSpan.FromSeconds(8), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                host.S3.BeforePut = null;
            }
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            try
            {
                await host.DisposeAsync();
            }
            catch (Exception error) when (failure is not null)
            {
                // Host shutdown can rethrow Stop's cleanup cancellation; retain the original watchdog/assertion failure.
                failure.Data["FixtureCleanupFailure"] = error;
            }
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

    // Watches one database's tap: its reads and snapshot reads, and fails its change-log cleanup once when asked.
    private sealed class WatchedBackup(IWalBackup engine) : IWalBackup
    {
        private string? _watched;
        private int _failTrim;
        private int _trimFailures;
        // The shipping job owns and disposes the tap; the fixture only borrows its staging operation.
        private Func<CancellationToken, Task<WalRead[]>>? _stage;

        public ConcurrentQueue<long> SnapshotReads { get; } = new();

        public int TrimFailures => Volatile.Read(ref _trimFailures);

        public int DeliveredReads { get; set; }

        // Written by the file's job; the test reads it only after shipping has stopped.
        public WalRead LastRead { get; set; }

        public void Watch(string database) => Volatile.Write(ref _watched, database);

        public void FailNextTrim() => Volatile.Write(ref _failTrim, 1);

        public Task<WalRead[]> StageAsync(CancellationToken cancellationToken) =>
            (_stage ?? throw new InvalidOperationException("the watched tap is not open"))(cancellationToken);

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
                    if (Interlocked.Exchange(ref _failTrim, 0) == 1)
                    {
                        Interlocked.Increment(ref _trimFailures);
                        throw new IOException("the change log cleanup failed");
                    }

                    return Task.CompletedTask;
                };
            }

            var watched = new WatchedTap(tap, this);
            _stage = watched.StageAsync;
            return watched;
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
        private readonly Queue<(MemoryStream Content, WalRead Read)> _staged = new();

        // Manual hosting joins every pass before staging; this is the same tap, never a second CDC consumer.
        public async Task<WalRead[]> StageAsync(CancellationToken cancellationToken)
        {
            if (_staged.Count != 0 || owner.DeliveredReads != 0)
            {
                throw new InvalidOperationException("the fixture stages its backlog only once");
            }

            var reads = new List<WalRead>(2);
            for (var index = 0; index < 2; index++)
            {
                // Two non-growing buffers bound this fixture to 80 MiB; no plaintext spill files or pooled payloads.
                var content = new MemoryStream(new byte[40 << 20], writable: true);
                try
                {
                    content.SetLength(0);
                    var read = await tap.ReadAsync(content, cancellationToken);
                    await Assert.That(content.Length).IsEqualTo(read.Bytes);
                    _staged.Enqueue((content, read));
                    reads.Add(read);
                }
                catch
                {
                    content.Dispose();
                    throw;
                }

                if (!reads[^1].Remaining)
                {
                    return [.. reads];
                }
            }

            throw new InvalidOperationException("the fixture backlog exceeded two real reads");
        }

        public async ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken)
        {
            if (_staged.TryPeek(out var staged))
            {
                // A failed copy retains the chunk: the caller truncates its partial append and retries this same read.
                staged.Content.Position = 0;
                await staged.Content.CopyToAsync(destination, cancellationToken);
                _staged.Dequeue();
                staged.Content.Dispose();
                owner.DeliveredReads++;
                owner.LastRead = staged.Read;
                return staged.Read;
            }

            var read = await tap.ReadAsync(destination, cancellationToken);
            owner.LastRead = read;
            return read;
        }

        public async ValueTask<WalSnapshot> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken)
        {
            if (_staged.Count != 0)
            {
                throw new InvalidOperationException("a snapshot cannot pass staged log chunks");
            }

            var snapshot = await tap.SnapshotAsync(destination, snapshotPath, cancellationToken);
            owner.SnapshotReads.Enqueue(snapshot.Read.Bytes);
            return snapshot;
        }

        public ValueTask DisposeAsync()
        {
            while (_staged.TryDequeue(out var staged))
            {
                staged.Content.Dispose();
            }

            return tap.DisposeAsync();
        }
    }
}
