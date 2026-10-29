using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Documents;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class WriterHoldTests
{

    [Test]
    public async Task Writes_across_databases_hold_no_writer_long_while_system_writes_run()
    {
        await using var api = await DocumentApi.StartAsync();
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        using var holds = new WriterHolds(api.Server);
        string[] names = [DocumentApi.Database, .. Enumerable.Range(0, 7).Select(i => $"hold{i}")];
        foreach (var name in names.Skip(1))
        {
            (await api.SendAsync(HttpMethod.Put, api.DatabasePath(name))).Status.ShouldBe(HttpStatusCode.Created);
            (await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath(name)}/collections", new { name = DocumentApi.Collection })).Status
                .ShouldBe(HttpStatusCode.Created);
        }

        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        var failures = new ConcurrentQueue<HttpStatusCode>();
        var slowest = TimeSpan.Zero;
        var clients = Task.WhenAll(names.Select(async name =>
        {
            for (var i = 0; i < 40; i++)
            {
                var response = await api.SendAsync(HttpMethod.Put,
                    $"{api.DatabasePath(name)}/collections/{DocumentApi.Collection}/documents/by-key?id={DocumentApi.NewId()}", new { n = i });
                if (response.Status is not (HttpStatusCode.Created or HttpStatusCode.OK))
                {
                    failures.Enqueue(response.Status);
                }
            }
        }));
        while (!clients.IsCompleted)
        {
            var started = Stopwatch.GetTimestamp();
            await using (var transaction = await system.BeginDurableTransactionAsync(CancellationToken.None))
            {
                _ = await transaction.PutAsync("writer_hold_probe", "anchor", Encoding.UTF8.GetBytes("""{"marker":1}"""), CancellationToken.None);
                _ = await transaction.CommitAsync(CancellationToken.None);
            }

            var took = Stopwatch.GetElapsedTime(started);
            slowest = took > slowest ? took : slowest;
        }

        await clients;
        await Assert.That(failures).IsEmpty();
        await Assert.That(slowest).IsLessThan(TimeSpan.FromSeconds(2));
        await Assert.That(holds.Holders).Contains("a group commit batch");
        await Assert.That(holds.Holders).Contains("a durable write transaction");
        await Assert.That(holds.Longest).IsLessThan(2);
        var scraped = await api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        await Assert.That(scraped).Contains("terrabase_storage_writer_held_seconds_bucket");
        await Assert.That(scraped).Contains("at=\"WriterHoldTests.cs:");
        await Assert.That(scraped).Contains("terrabase_storage_transactions_total");
        await Assert.That(scraped).Contains("kind=\"write\"");
        await Assert.That(scraped).Contains("terrabase_storage_queued");
    }

    [Test]
    public async Task A_writer_held_idle_by_its_transaction_is_measured_by_its_holder()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await DocumentApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock));
        using var holds = new WriterHolds(api.Server);
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);

        await using (var transaction = await system.BeginDurableTransactionAsync(CancellationToken.None))
        {
            _ = await transaction.PutAsync("writer_hold_probe", "idle", Encoding.UTF8.GetBytes("""{"idle":true}"""), CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(7));
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        await Assert.That(holds.Measured("a durable write transaction")).Contains(7);
    }

    [Test]
    public async Task Bulk_work_leaves_an_io_thread_to_reads()
    {
        await using var api = await DocumentApi.StartAsync(configure: settings => settings["storage-threads"] = "2");
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        using var release = new ManualResetEventSlim();
        var bulkStarted = 0;
        List<Task> busy = [];
        try
        {
            using (StorageWork.Bulk())
            {
                for (var i = 0; i < 2; i++)
                {
                    engine.Io.Admit();
                    busy.Add(engine.Io.RunAsync(() =>
                    {
                        Interlocked.Increment(ref bulkStarted);
                        release.Wait();
                    }, CancellationToken.None));
                }
            }
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(engine.Io.Running == 1 && engine.Io.Queued == 1));
            var read = await api.GetAsync(id).WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(Volatile.Read(ref bulkStarted)).IsEqualTo(1);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(busy);
            foreach (var _ in busy)
            {
                engine.Io.Release();
            }
        }
    }

    [Test]
    public async Task A_backup_snapshot_releases_the_writer_while_every_io_thread_is_busy()
    {
        await using var api = await DocumentApi.StartAsync(configure: settings => settings["storage-threads"] = "2");
        (await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var database = await api.TenantOfAsync();
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, CancellationToken.None))!;
        await using var tap = (TursoChangeTap)(await engine.OpenTapAsync(database, CancellationToken.None))!;
        var pinned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tap.AfterSnapshotPinned = _ =>
        {
            pinned.TrySetResult();
            return Task.CompletedTask;
        };

        var directory = Directory.CreateTempSubdirectory("terrabase-snapshot-hold-");
        using var release = new ManualResetEventSlim();
        List<Task> busy = [];
        Task<WalSnapshot>? snapshot = null;
        try
        {
            for (var i = 0; i < 2; i++)
            {
                engine.Io.Admit();
                busy.Add(engine.Io.RunAsync(release.Wait, CancellationToken.None));
            }

            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(engine.Io.Running == 2));
            snapshot = tap.SnapshotAsync(Stream.Null, Path.Combine(directory.FullName, "snapshot.db"), CancellationToken.None).AsTask();
            await pinned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(await store.WithWritersHeldAsync(() => Task.FromResult(true), CancellationToken.None, "a test taking the writer")
                .WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
            await Assert.That(snapshot.IsCompleted).IsFalse().Because("the copy itself still waits for an I/O thread");
        }
        finally
        {
            release.Set();
            await Task.WhenAll(busy);
            foreach (var _ in busy)
            {
                engine.Io.Release();
            }

            if (snapshot is not null)
            {
                _ = await snapshot;
            }

            directory.Delete(recursive: true);
        }
    }

    internal sealed class WriterHolds : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(string Holder, double Seconds)> _holds = new();

        public WriterHolds(TerraBaseTestServer server)
        {
            var meter = server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "terrabase.storage.writer_held")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((_, seconds, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "holder")
                    {
                        _holds.Enqueue(((string)tag.Value!, seconds));
                    }
                }
            });
            _listener.Start();
        }

        public IReadOnlySet<string> Holders => _holds.Select(hold => hold.Holder).ToHashSet();

        public double Longest => _holds.IsEmpty ? 0 : _holds.Max(hold => hold.Seconds);

        public IReadOnlyList<double> Measured(string holder) => [.. _holds.Where(hold => hold.Holder == holder).Select(hold => hold.Seconds)];

        public void Dispose() => _listener.Dispose();
    }
}
