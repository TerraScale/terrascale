using System.Net;
using System.Text;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

/// <summary>
/// Shipping recovers without losing or overlapping history: a backlog written during a slow snapshot upload ships as
/// several segments, a segment upload the bucket stored before failing is retried under its own key, and a failed tap
/// read keeps the generation and its lag.
/// </summary>
public sealed class BackupShippingRecoveryTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] AmbiguousDocuments = ["seed", "b", "c", "d"];

    [Test]
    public async Task A_backlog_written_during_a_slow_snapshot_upload_ships_as_several_segments_that_restore_exactly(CancellationToken cancellationToken)
    {
        // About 43 MB of change log: more than one read budget (32 MiB), so it ships over two passes.
        await using var host = await BackupShippingHost.StartAsync();
        await BackupBacklogScenario.RunAsync(host, writes: 48, minimumSegments: 2, cancellationToken);
    }

    [Test]
    public async Task A_segment_upload_stored_then_failed_is_retried_under_its_key_and_every_restore_time_is_exact(CancellationToken cancellationToken)
    {
        var ambiguous = new AmbiguousPuts();
        await using var host = await BackupShippingHost.StartAsync(services =>
            services.AddHttpClient(S3ObjectStore.HttpClientName).AddHttpMessageHandler(() => new AmbiguousPutHandler(ambiguous)));
        var texts = AmbiguousDocuments.ToDictionary(name => name, _ => Faker.Lorem.Sentence(8), StringComparer.Ordinal);
        await host.PutAsync("people", "seed", texts["seed"], cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var generation = host.Status().Generation!;

        // B's segment is stored, and every attempt at it (the SDK retries 500s) is answered 500.
        ambiguous.Prefix = $"{host.File}/wal/";
        await host.PutAsync("people", "b", texts["b"], cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        ambiguous.Prefix = null;
        var stored = host.S3.Objects[ambiguous.Key!].ToArray();
        host.Time.Advance(TimeSpan.FromSeconds(1));
        var failing = host.Status();
        await Assert.That(ambiguous.Attempts).IsGreaterThan(0);
        await Assert.That(failing.LastError).IsNotNull();
        await Assert.That(failing.PendingBytes).IsGreaterThan(0);
        await Assert.That(failing.LagSeconds).IsGreaterThanOrEqualTo(1);

        // C commits before the retry: B's segment goes again as the same bytes under its key, and C starts the next segment.
        await host.PutAsync("people", "c", texts["c"], cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        await host.PutAsync("people", "d", texts["d"], cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);

        var recovered = host.Status();
        await Assert.That(recovered.Generation).IsEqualTo(generation);
        await Assert.That(recovered.LastError).IsNull();
        await Assert.That(recovered.LagSeconds).IsEqualTo(0);
        await Assert.That(recovered.PendingBytes).IsEqualTo(0);
        var segments = host.Segments(generation);
        await Assert.That(segments.Count).IsEqualTo(3);
        await Assert.That(segments[0].Key).IsEqualTo(ambiguous.Key);
        await Assert.That(host.S3.Objects[ambiguous.Key!].AsSpan().SequenceEqual(stored)).IsTrue();
        await BackupShippingHost.AssertContiguousAsync(segments);

        // Every restore time in the window restores exactly the writes read by it.
        string[][] restored = [["seed", "b"], ["seed", "b", "c"], ["seed", "b", "c", "d"]];
        for (var index = 0; index < segments.Count; index++)
        {
            var name = $"ambiguous-{index}";
            await host.RestoreAsync(segments[index].At, name, cancellationToken);
            foreach (var (document, text) in texts)
            {
                await Assert.That(await host.ReadAsync(name, "people", document, cancellationToken)).IsEqualTo(restored[index].Contains(document) ? text : null);
            }
        }
    }

    [Test]
    public async Task A_failed_tap_read_keeps_the_generation_and_its_lag_and_the_next_pass_ships_the_same_changes(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync(services =>
            services.AddSingleton<IWalBackup>(provider => new FailingBackup(provider.GetRequiredService<SqliteStorageEngine>())));
        var engine = (FailingBackup)host.Api.Server.Services.GetRequiredService<IWalBackup>();
        engine.Watch(host.Database.Id);
        var before = Faker.Lorem.Sentence(8);
        var after = Faker.Lorem.Sentence(8);
        await host.PutAsync("people", "before", before, cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var generation = host.Status().Generation!;

        // The read appends part of a record, then fails: the generation stays, and so does the lag of the unread write.
        engine.FailNextRead();
        await host.PutAsync("people", "after", after, cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        host.Time.Advance(TimeSpan.FromSeconds(1));
        var failed = host.Status();
        await Assert.That(engine.Failed).IsEqualTo(1);
        await Assert.That(failed.Generation).IsEqualTo(generation);
        await Assert.That(failed.LastError).IsEqualTo(nameof(IOException));
        await Assert.That(failed.LagSeconds).IsGreaterThanOrEqualTo(1);
        await Assert.That(failed.PendingBytes).IsEqualTo(0);

        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var shipped = host.Status();
        await Assert.That(shipped.Generation).IsEqualTo(generation);
        await Assert.That(shipped.LagSeconds).IsEqualTo(0);
        await host.RestoreAsync(shipped.LastShippedAt!.Value, "after-failed-read", cancellationToken);
        await Assert.That(await host.ReadAsync("after-failed-read", "people", "before", cancellationToken)).IsEqualTo(before);
        await Assert.That(await host.ReadAsync("after-failed-read", "people", "after", cancellationToken)).IsEqualTo(after);
        await Assert.That(await host.MetricsAsync(cancellationToken)).DoesNotContain("terrabase_backup_abandoned");
    }

    [Test]
    public async Task A_generation_started_after_a_failed_snapshot_upload_clears_the_error(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        var snapshots = $"{host.File}/snapshots/";
        host.S3.FailPut = key => key.StartsWith(snapshots, StringComparison.Ordinal);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        host.Time.Advance(TimeSpan.FromSeconds(1));
        var failed = host.Status();
        await Assert.That(failed.Generation).IsNull();
        await Assert.That(failed.LastError).IsNotNull();
        await Assert.That(failed.LagSeconds).IsGreaterThanOrEqualTo(1);

        host.S3.FailPut = _ => false;
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var started = host.Status();
        await Assert.That(started.Generation).IsNotNull();
        await Assert.That(started.LastError).IsNull();
    }

    [Test]
    public async Task Lag_counts_writes_acknowledged_during_a_held_snapshot_upload_and_its_restore_drill(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        var before = Faker.Lorem.Sentence(8);
        var during = Faker.Lorem.Sentence(8);
        await host.PutAsync("people", "before", before, cancellationToken);
        var snapshots = $"{host.File}/snapshots/";
        var upload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The first generation's snapshot upload, then its restore drill's download of that snapshot, wait for the test.
        host.S3.BeforePut = async (key, token) =>
        {
            if (key.StartsWith(snapshots, StringComparison.Ordinal))
            {
                upload.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var drilling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.S3.BeforeGet = async (key, token) =>
        {
            if (key.StartsWith(snapshots, StringComparison.Ordinal))
            {
                drill.TrySetResult();
                await drilling.Task.WaitAsync(token);
            }
        };
        var pass = Task.CompletedTask;
        try
        {
            host.Time.Advance(TimeSpan.FromSeconds(10));
            pass = host.StartPassAsync(cancellationToken);
            await upload.Task.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
            await host.PutAsync("people", "during", during, cancellationToken);
            host.Time.Advance(TimeSpan.FromSeconds(5));
            await Assert.That(host.Status().LagSeconds).IsGreaterThanOrEqualTo(5);

            host.S3.BeforePut = null;
            release.TrySetResult();
            await drill.Task.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
            host.Time.Advance(TimeSpan.FromSeconds(5));
            var drilled = host.Status();
            await Assert.That(drilled.Generation).IsNotNull();
            await Assert.That(drilled.LagSeconds).IsGreaterThanOrEqualTo(5);
        }
        finally
        {
            host.S3.BeforePut = null;
            host.S3.BeforeGet = null;
            release.TrySetResult();
            drilling.TrySetResult();
            await pass;
        }

        await Assert.That(host.Status().LastDrill!.Passed).IsTrue();
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var shipped = host.Status();
        await Assert.That(shipped.LagSeconds).IsEqualTo(0);
        await host.RestoreAsync(shipped.LastShippedAt!.Value, "after-drill", cancellationToken);
        await Assert.That(await host.ReadAsync("after-drill", "people", "during", cancellationToken)).IsEqualTo(during);
    }

    // The first PUT under Prefix chooses the key; every PUT of that key while Prefix is set is stored, then answered 500.
    private sealed class AmbiguousPuts
    {
        private readonly Lock _gate = new();
        private int _attempts;
        private string? _prefix;
        private string? _key;

        public string? Prefix
        {
            get
            {
                lock (_gate)
                {
                    return _prefix;
                }
            }

            set
            {
                lock (_gate)
                {
                    _prefix = value;
                }
            }
        }

        public string? Key
        {
            get
            {
                lock (_gate)
                {
                    return _key;
                }
            }
        }

        public int Attempts => Volatile.Read(ref _attempts);

        public bool Matches(string key)
        {
            lock (_gate)
            {
                if (_prefix is not { } prefix || !key.StartsWith(prefix, StringComparison.Ordinal) || _key is not null && _key != key)
                {
                    return false;
                }

                _key = key;
                _attempts++;
                return true;
            }
        }
    }

    private sealed class AmbiguousPutHandler(AmbiguousPuts puts) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimStart('/');
            var key = path[(path.IndexOf('/', StringComparison.Ordinal) + 1)..];
            if (request.Method != HttpMethod.Put || !response.IsSuccessStatusCode || !puts.Matches(key))
            {
                return response;
            }

            response.Dispose();
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("<Error><Code>InternalError</Code><Message>stored, then the response was lost</Message></Error>", Encoding.UTF8, "application/xml"),
            };
        }
    }

    private sealed class FailingBackup(IWalBackup engine) : IWalBackup
    {
        private string? _watched;
        private int _armed;
        private int _failed;

        public int Failed => Volatile.Read(ref _failed);

        public void Watch(string database) => Volatile.Write(ref _watched, database);

        public void FailNextRead() => Volatile.Write(ref _armed, 1);

        public async ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken)
        {
            var tap = await engine.OpenTapAsync(database, cancellationToken);
            return tap is not null && database?.DatabaseId == Volatile.Read(ref _watched) ? new FailingTap(tap, this) : tap;
        }

        public ValueTask<bool> RollForwardAsync(string file, IAsyncEnumerable<Stream> segments, CancellationToken cancellationToken) =>
            engine.RollForwardAsync(file, segments, cancellationToken);

        public ValueTask ImportDatabaseAsync(TenantContext database, string file, CancellationToken cancellationToken) =>
            engine.ImportDatabaseAsync(database, file, cancellationToken);

        public ValueTask<byte[]> ChecksumAsync(string file, CancellationToken cancellationToken) => engine.ChecksumAsync(file, cancellationToken);

        public bool TakeFailure()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
            {
                return false;
            }

            Interlocked.Increment(ref _failed);
            return true;
        }
    }

    // A read that fails after appending part of a record, without moving the tap: what an I/O error mid-read leaves.
    private sealed class FailingTap(IWalTap tap, FailingBackup owner) : IWalTap
    {
        public async ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken)
        {
            if (owner.TakeFailure())
            {
                await destination.WriteAsync(new byte[] { 1, 0xff }, cancellationToken);
                throw new IOException("the change log could not be read");
            }

            return await tap.ReadAsync(destination, cancellationToken);
        }

        public ValueTask<WalSnapshot> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken) =>
            tap.SnapshotAsync(destination, snapshotPath, cancellationToken);

        public ValueTask DisposeAsync() => tap.DisposeAsync();
    }
}
