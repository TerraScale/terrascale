using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Memory;
using TerraBase.Fast.Platform;
using TerraBase.Fast.Documents;
using TerraBase.Features.Shapes;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Fast.Shapes;

public sealed class ShapeMetricsTests
{
    private const long Budget = 4 * 1024 * 1024;
    private const double HobbyShare = .0625;

    [Test]
    public async Task Reads_seals_bytes_and_redirects_are_scraped_from_the_host()
    {
        await using var server = await StartAsync();
        var metrics = Metrics(server);
        for (var read = 0; read < 3; read++)
        {
            metrics.RecordRead();
        }

        metrics.RecordSeal(4096, TimeSpan.FromMilliseconds(250));
        metrics.RecordSeal(1024, TimeSpan.FromMilliseconds(250));
        metrics.RecordRedirect();

        var scrape = await ScrapeAsync(server);
        await Assert.That(Sample(scrape, "terrabase_shape_reads_total")).IsEqualTo(3d);
        await Assert.That(Sample(scrape, "terrabase_shape_seals_total")).IsEqualTo(2d);
        await Assert.That(Sample(scrape, "terrabase_shape_sealed_bytes_total")).IsEqualTo(5120d);
        await Assert.That(Sample(scrape, "terrabase_shape_seals_duration_seconds_count")).IsEqualTo(2d);
        await Assert.That(Sample(scrape, "terrabase_shape_seals_duration_seconds_sum")).IsEqualTo(0.5d);
        await Assert.That(Sample(scrape, "terrabase_shape_redirects_total")).IsEqualTo(1d);
    }

    [Test]
    public async Task Mirror_lag_is_recorded_in_seconds_from_publication_to_completion()
    {
        await using var server = await StartAsync();
        Metrics(server).RecordMirrorLag(TimeSpan.FromMilliseconds(1500));

        var scrape = await ScrapeAsync(server);
        await Assert.That(Sample(scrape, "terrabase_shape_mirror_lag_seconds_count")).IsEqualTo(1d);
        await Assert.That(Sample(scrape, "terrabase_shape_mirror_lag_seconds_sum")).IsEqualTo(1.5d);
    }

    [Test]
    public async Task Negative_durations_and_bytes_are_refused_without_recording()
    {
        await using var server = await StartAsync();
        var metrics = Metrics(server);

        await Assert.That(async () =>
        {
            metrics.RecordSeal(-1, TimeSpan.Zero);
            await Task.CompletedTask;
        }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () =>
        {
            metrics.RecordSeal(0, TimeSpan.FromSeconds(-1));
            await Task.CompletedTask;
        }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () =>
        {
            metrics.RecordMirrorLag(TimeSpan.FromSeconds(-1));
            await Task.CompletedTask;
        }).Throws<ArgumentOutOfRangeException>();
        var scrape = await ScrapeAsync(server);
        await Assert.That(scrape).DoesNotContain("terrabase_shape_seals_total");
        await Assert.That(scrape).DoesNotContain("terrabase_shape_mirror_lag_seconds_count");
    }

    [Test]
    public async Task Waiting_and_flights_return_to_zero_after_error_and_cancellation()
    {
        await using var server = await StartAsync();
        var metrics = Metrics(server);
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var held = Flight(metrics, async ct =>
        {
            started.SetResult();
            await release.Task.WaitAsync(ct);
        }, token);
        await started.Task.WaitAsync(token);
        await Assert.That(Sample(await ScrapeAsync(server), "terrabase_shape_flights_active")).IsEqualTo(1d);

        using var waitCancellation = new CancellationTokenSource();
        var waiter = Waiting(metrics, new TaskCompletionSource().Task, waitCancellation.Token);
        await Assert.That(Sample(await ScrapeAsync(server), "terrabase_shape_requests_waiting")).IsEqualTo(1d);
        await waitCancellation.CancelAsync();
        await Assert.That(async () => await waiter).Throws<OperationCanceledException>();

        var failing = Flight(metrics, _ => throw new InvalidOperationException("synthetic flight failure"), token);
        await Assert.That(async () => await failing).Throws<InvalidOperationException>();

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var cancelledFlight = Flight(metrics, ct => new TaskCompletionSource().Task.WaitAsync(ct), canceled.Token);
        await Assert.That(async () => await cancelledFlight).Throws<OperationCanceledException>();
        await Assert.That(Sample(await ScrapeAsync(server), "terrabase_shape_flights_active")).IsEqualTo(1d);
        release.SetResult();
        await held;

        var scrape = await ScrapeAsync(server);
        await Assert.That(Sample(scrape, "terrabase_shape_flights_active")).IsEqualTo(0d);
        await Assert.That(Sample(scrape, "terrabase_shape_requests_waiting")).IsEqualTo(0d);
    }

    [Test]
    public async Task Process_memory_refuses_over_the_limit_and_releases_on_error_and_cancellation()
    {
        await using var server = await StartAsync();
        var metrics = Metrics(server);
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;

        using (var whole = metrics.TryReserve(MemoryAccounting.Available(server, Budget), null))
        {
            await Assert.That(whole).IsNotNull();
            await Assert.That(metrics.TryReserve(1, null)).IsNull();
            await Assert.That(Sample(await ScrapeAsync(server), "terrabase_memory_reserved_bytes")).IsEqualTo((double)Budget);
        }

        await Assert.That(async () => await Admitted(metrics, 1024, _ => throw new InvalidOperationException("synthetic failure"), token))
            .Throws<InvalidOperationException>();
        using (var canceled = new CancellationTokenSource())
        {
            await canceled.CancelAsync();
            await Assert.That(async () => await Admitted(metrics, 1024, ct => new TaskCompletionSource().Task.WaitAsync(ct), canceled.Token))
                .Throws<OperationCanceledException>();
        }
        using var all = metrics.TryReserve(MemoryAccounting.Available(server, Budget), null);
        await Assert.That(all).IsNotNull();
        await Assert.That(Sample(await ScrapeAsync(server), "terrabase_memory_reserved_bytes")).IsEqualTo((double)Budget);
    }

    [Test]
    public async Task Explicit_tenant_is_charged_its_plan_share_without_refusing_other_tenants()
    {
        await using var server = await StartAsync();
        var metrics = Metrics(server);
        var tenant = new TenantContext(DocumentApi.NewId(), PlanTier.Hobby);
        var other = new TenantContext(DocumentApi.NewId(), PlanTier.Hobby);
        var share = (long)(Budget * HobbyShare);

        using var first = metrics.TryReserve(share, tenant);
        await Assert.That(first).IsNotNull();
        await Assert.That(metrics.TryReserve(1, tenant)).IsNull();

        using var second = metrics.TryReserve(share, other);
        await Assert.That(second).IsNotNull();
        using var rest = metrics.TryReserve(MemoryAccounting.Available(server, Budget - 2 * share), null);
        await Assert.That(rest).IsNotNull();
        await Assert.That(metrics.TryReserve(1, null)).IsNull();
        await Assert.That(metrics.TryReserve(1, tenant)).IsNull();

        var scrape = await ScrapeAsync(server);
        await Assert.That(Sample(scrape, "terrabase_memory_reserved_bytes")).IsEqualTo((double)Budget);
    }

    [Test]
    public async Task Shape_and_storage_measurements_carry_no_tags_or_tenant_identity()
    {
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<IShapeMetrics, ShapeMetrics>());
        var server = api.Server;
        var metrics = Metrics(server);
        var tenant = await api.TenantOfAsync();
        var factory = server.Services.GetRequiredService<IMeterFactory>();

        var measured = new ConcurrentQueue<(string Name, string Keys, string Values)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, subscribe) =>
        {
            if (instrument.Meter.Name == Telemetry.MeterName && ReferenceEquals(instrument.Meter.Scope, factory)
                && (instrument.Name.StartsWith("terrabase.shape.", StringComparison.Ordinal)
                    || instrument.Name == "terrabase.storage.reads"))
            {
                subscribe.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => measured.Enqueue(Describe(instrument, tags)));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => measured.Enqueue(Describe(instrument, tags)));
        listener.Start();

        metrics.RecordRead();
        metrics.ChangeWaiting(1);
        metrics.ChangeWaiting(-1);
        metrics.ChangeFlights(1);
        metrics.ChangeFlights(-1);
        metrics.RecordSeal(64, TimeSpan.FromMilliseconds(1));
        metrics.RecordMirrorLag(TimeSpan.FromMilliseconds(1));
        metrics.RecordRedirect();
        using (metrics.TryReserve(1024, tenant))
        {
        }

        var token = TestContext.Current!.Execution.CancellationToken;
        var id = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(id, new { n = new Faker().Random.Int() })).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await api.QueryAsync(new { limit = 10 })).Status).IsEqualTo(HttpStatusCode.OK);
        var store = (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, token))!;
        await using (var transaction = await store.BeginTransactionAsync(write: false, token))
        {
            var page = await transaction.ReadFeedAsync(null, null, 100, 1024 * 1024, token);
            await Assert.That(page.Events.Count).IsGreaterThan(0);
        }
        var shapeStorage = new TursoShapeStorage(server.Services.GetRequiredService<TursoStorageEngine>());
        await using (var transaction = await shapeStorage.BeginTransactionAsync(store, write: false, token))
        {
            using var absent = await ((IShapeStorageTransaction)transaction).ReadShapeChunkAsync(
                "h1-" + Convert.ToHexStringLower(new Faker().Random.Bytes(8)), 0,
                server.Services.GetRequiredService<IMemoryBudget>(), token);
            await Assert.That(absent).IsNull();
        }

        var names = measured.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
        {
            "terrabase.shape.reads", "terrabase.shape.requests.waiting", "terrabase.shape.flights.active", "terrabase.shape.seals",
            "terrabase.shape.seals.duration", "terrabase.shape.sealed_bytes", "terrabase.shape.mirror.lag", "terrabase.shape.redirects",
        })
        {
            await Assert.That(names).Contains(expected);
        }
        await Assert.That(names).Contains("terrabase.storage.reads");
        var paths = measured.Where(item => item.Name == "terrabase.storage.reads").Select(item => item.Values).ToHashSet(StringComparer.Ordinal);
        await Assert.That(paths).Contains("point");
        await Assert.That(paths).Contains("query");
        await Assert.That(paths).Contains("feed");
        await Assert.That(paths).Contains("shape");
        await Assert.That(paths.All(path => path is "point" or "query" or "feed" or "shape" or "shape_body")).IsTrue();

        foreach (var (name, keys, values) in measured)
        {
            var allowed = name == "terrabase.storage.reads" ? "path" : string.Empty;
            await Assert.That(keys).IsEqualTo(allowed);
            await Assert.That(values).DoesNotContain(tenant.OrganizationId);
        }
    }

    [Test]
    public async Task Each_host_exports_only_its_own_shape_measurements()
    {
        await using var first = await StartAsync();
        await using var second = await StartAsync();
        Metrics(first).RecordRead();
        Metrics(second).RecordRead();
        Metrics(second).RecordRead();
        Metrics(second).RecordRedirect();
        using var reservation = Metrics(first).TryReserve(1024, null);

        var firstScrape = await ScrapeAsync(first);
        var secondScrape = await ScrapeAsync(second);
        await Assert.That(Sample(firstScrape, "terrabase_shape_reads_total")).IsEqualTo(1d);
        await Assert.That(Sample(secondScrape, "terrabase_shape_reads_total")).IsEqualTo(2d);
        await Assert.That(firstScrape).DoesNotContain("terrabase_shape_redirects_total");
        await Assert.That(Sample(secondScrape, "terrabase_shape_redirects_total")).IsEqualTo(1d);
        await Assert.That(Sample(firstScrape, "terrabase_memory_reserved_bytes")).IsEqualTo(1024d + MemoryAccounting.Retained(first));
        await Assert.That(Sample(secondScrape, "terrabase_memory_reserved_bytes")).IsEqualTo((double)MemoryAccounting.Retained(second));
    }

    [Test]
    public async Task Point_reads_count_a_storage_miss_and_skip_a_cache_hit()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var stored = DocumentApi.NewId();
        var absent = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(stored, new { a = 1 })).Status).IsEqualTo(HttpStatusCode.Created);
        var beforeMiss = PointReads(await ScrapeAsync(api.Server));
        await Assert.That((await api.GetAsync(absent)).Status).IsEqualTo(HttpStatusCode.NotFound);
        var afterMiss = PointReads(await ScrapeAsync(api.Server));
        await Assert.That(afterMiss - beforeMiss).IsGreaterThanOrEqualTo(1d);
        await Assert.That((await api.GetAsync(stored)).Status).IsEqualTo(HttpStatusCode.OK);
        var beforeHit = PointReads(await ScrapeAsync(api.Server));
        await Assert.That((await api.GetAsync(stored)).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(PointReads(await ScrapeAsync(api.Server))).IsEqualTo(beforeHit);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Repeated_writes_in_one_transaction_count_the_stored_body_read_only_once(bool ownerRules)
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        if (ownerRules)
        {
            await CollectionAccessRulesTests.DeclareAsync(api, CollectionAccessRulesTests.OwnerRules());
        }
        var id = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(id, new { a = 1, owner = "first" })).Status).IsEqualTo(HttpStatusCode.Created);
        var token = TestContext.Current!.Execution.CancellationToken;
        var tenant = await api.TenantOfAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, token))!;
        await using var transaction = await store.BeginTransactionAsync(write: true, token);

        var before = PointReads(await ScrapeAsync(api.Server));
        await transaction.PutAsync(DocumentApi.Collection, id, """{"a":2,"owner":"middle"}"""u8.ToArray(), token);
        var afterFirst = PointReads(await ScrapeAsync(api.Server));
        await Assert.That(afterFirst - before).IsEqualTo(4d);

        await transaction.PutAsync(DocumentApi.Collection, id, """{"a":3,"owner":"last"}"""u8.ToArray(), token);
        await Assert.That(PointReads(await ScrapeAsync(api.Server)) - afterFirst).IsEqualTo(2d);
        await transaction.CommitAsync(token);

        await using var read = await store.BeginTransactionAsync(write: false, token);
        var generation = (await read.GetCollectionAsync(DocumentApi.Collection, token))!.Id;
        var page = await read.ReadFeedAsync(null, new HashSet<string>([DocumentApi.Collection]), 100, 1024 * 1024, token);
        var change = page.Events.Last(change => change.Id == id);
        await Assert.That(change.CollectionGeneration).IsEqualTo(generation);
        await Assert.That(change.AccessBefore?.Subject).IsEqualTo(ownerRules ? "first" : null);
        await Assert.That(change.AccessAfter?.Subject).IsEqualTo(ownerRules ? "last" : null);
    }

    [Test]
    public async Task Pending_feed_proofs_use_current_collection_metadata()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var original = CollectionAccessRulesTests.OwnerRules();
        await CollectionAccessRulesTests.DeclareAsync(api, original);
        var id = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(id, new { owner = "first", nextOwner = "last" })).Status).IsEqualTo(HttpStatusCode.Created);
        var token = TestContext.Current!.Execution.CancellationToken;
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), token))!;
        var current = original with { Owner = "/nextOwner" };
        await using (var write = await store.BeginTransactionAsync(write: true, token))
        {
            await write.PutAsync(DocumentApi.Collection, id, """{"owner":"first","nextOwner":"last","value":2}"""u8.ToArray(), token);
            await Assert.That(await write.SetCollectionRulesAsync(DocumentApi.Collection, current, token)).IsTrue();
            await write.CommitAsync(token);
        }

        await using var read = await store.BeginTransactionAsync(write: false, token);
        var generation = (await read.GetCollectionAsync(DocumentApi.Collection, token))!.Id;
        var page = await read.ReadFeedAsync(null, new HashSet<string>([DocumentApi.Collection]), 100, 1024 * 1024, token);
        var change = page.Events.Last(change => change.Id == id);
        await Assert.That(change.CollectionGeneration).IsEqualTo(generation);
        await Assert.That(change.AccessBefore?.Subject).IsEqualTo("first");
        await Assert.That(change.AccessBefore?.Policy).IsEqualTo(original.Fingerprint());
        await Assert.That(change.AccessAfter?.Subject).IsEqualTo("last");
        await Assert.That(change.AccessAfter?.Policy).IsEqualTo(current.Fingerprint());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Pending_collection_schema_is_enforced_after_a_schema_change_or_recreation(bool recreate)
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        if (recreate)
        {
            await Assert.That((await api.SendAsync(HttpMethod.Delete, $"{api.Collections}/{DocumentApi.Collection}")).Status)
                .IsEqualTo(HttpStatusCode.OK);
        }

        var token = TestContext.Current!.Execution.CancellationToken;
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), token))!;
        await using var transaction = await store.BeginTransactionAsync(write: true, token);
        if (recreate)
        {
            await Assert.That(await transaction.CreateCollectionAsync(new CollectionInfo(DocumentApi.Collection, 0), token)).IsTrue();
        }

        using var schema = JsonDocument.Parse("""{"type":"object","required":["name"]}""");
        await Assert.That(await transaction.SetCollectionSchemaAsync(DocumentApi.Collection, schema.RootElement, token)).IsTrue();
        await Assert.That(async () => await transaction.PutAsync(DocumentApi.Collection, DocumentApi.NewId(), "{}"u8.ToArray(), token))
            .Throws<CollectionSchemaException>();
    }

    [Test]
    public async Task A_collection_metadata_body_without_its_state_refuses_document_writes()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var token = TestContext.Current!.Execution.CancellationToken;
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), token))!;
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(await api.DatabaseFileAsync())))
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM _tb_state WHERE collection = $collection AND id = $id;";
            command.Parameters.AddWithValue("$collection", DocumentDelta.Collections);
            command.Parameters.AddWithValue("$id", DocumentApi.Collection);
            await Assert.That(await command.ExecuteNonQueryAsync(token)).IsEqualTo(1);
        }

        await using var transaction = await store.BeginTransactionAsync(write: true, token);
        await Assert.That(async () => await transaction.PutAsync(DocumentApi.Collection, DocumentApi.NewId(), "{}"u8.ToArray(), token))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task Shape_seals_chunk_reads_and_floors_count_backend_reads_on_the_shape_path()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var token = TestContext.Current!.Execution.CancellationToken;
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), new { n = new Faker().Random.Int() })).Status)
            .IsEqualTo(HttpStatusCode.Created);
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>()
            .OpenDatabaseAsync(await api.TenantOfAsync(), token))!;
        ShapeSealDefinition shape;
        await using (var transaction = await store.BeginTransactionAsync(write: false, token))
        {
            var collection = (await transaction.GetCollectionAsync(DocumentApi.Collection, token))!;
            var random = new Faker().Random;
            shape = new ShapeSealDefinition(Convert.ToHexStringLower(random.Bytes(16)),
                "h1-" + Convert.ToHexStringLower(random.Bytes(8)), DocumentApi.Collection, collection.Id, random.Bytes(16),
                store.Incarnation);
        }

        var shapeStorage = new TursoShapeStorage(api.Server.Services.GetRequiredService<TursoStorageEngine>());
        var sealer = new ShapeSealer(api.Server.Services.GetRequiredService<IMemoryBudget>(), NullLogger<ShapeSealer>.Instance,
            shapeStorage);
        var before = ShapeStorageReads(await ScrapeAsync(api.Server));
        var head = await sealer.SealUpToHeadAsync(store, shape, 0, token);
        var sealedReads = ShapeStorageReads(await ScrapeAsync(api.Server));
        await Assert.That(head).IsGreaterThan(0L);
        await Assert.That(sealedReads - before).IsGreaterThan(2d).Because("a fresh seal reads feed rows and document bodies");

        await Assert.That(await sealer.SealUpToHeadAsync(store, shape, 0, token)).IsEqualTo(head);
        var reusedReads = ShapeStorageReads(await ScrapeAsync(api.Server));
        await Assert.That(reusedReads - sealedReads).IsEqualTo(3d).Because("a reused seal reads the collection generation, head and stored boundary");

        using var missing = await sealer.ReadChunkAsync(store, shape.Handle, head + 1, token);
        await Assert.That(missing).IsNull();
        var missedReads = ShapeStorageReads(await ScrapeAsync(api.Server));
        await Assert.That(missedReads - reusedReads).IsEqualTo(1d).Because("a missing chunk still executes its size SELECT");

        using var chunk = await sealer.ReadChunkAsync(store, shape.Handle, 0, token);
        await Assert.That(chunk).IsNotNull();
        var bodyReads = ShapeStorageReads(await ScrapeAsync(api.Server));
        await Assert.That(bodyReads - missedReads).IsEqualTo(3d).Because("a stored chunk executes size, body and frontier SELECTs");

        await using (var transaction = await shapeStorage.BeginTransactionAsync(store, write: false, token))
        {
            _ = await ((IShapeRetentionTransaction)transaction).ReadShapeFloorAsync(shape.Handle, token);
        }
        await Assert.That(ShapeStorageReads(await ScrapeAsync(api.Server)) - bodyReads).IsEqualTo(4d)
            .Because("the retained floor reads its sentinel, sequence, head and first feed row");
    }

    private static double ShapeStorageReads(string scrape) => scrape.Split('\n')
        .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#') && MetricName(line) == "terrabase_storage_reads_total"
            && (line.Contains("path=\"shape\"", StringComparison.Ordinal) || line.Contains("path=\"shape_body\"", StringComparison.Ordinal)))
        .Sum(line => double.Parse(line.Split(' ')[^1], CultureInfo.InvariantCulture));
    private static double PointReads(string scrape) => scrape.Split('\n')
        .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#') && MetricName(line) == "terrabase_storage_reads_total"
            && line.Contains("path=\"point\"", StringComparison.Ordinal))
        .Sum(line => double.Parse(line.Split(' ')[^1], CultureInfo.InvariantCulture));

    private static Task<TerraBaseTestServer> StartAsync() =>
        TerraBaseTestServer.StartAsync(
            settings => settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture),
            services => services.AddSingleton<IShapeMetrics, ShapeMetrics>());

    private static IShapeMetrics Metrics(TerraBaseTestServer server) => server.Services.GetRequiredService<IShapeMetrics>();

    private static Task<string> ScrapeAsync(TerraBaseTestServer server) =>
        server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), TUnit.Core.TestContext.Current!.Execution.CancellationToken);
    private static async Task Flight(IShapeMetrics metrics, Func<CancellationToken, Task> work, CancellationToken token)
    {
        metrics.ChangeFlights(1);
        try
        {
            await work(token);
        }
        finally
        {
            metrics.ChangeFlights(-1);
        }
    }

    private static async Task Waiting(IShapeMetrics metrics, Task gate, CancellationToken token)
    {
        metrics.ChangeWaiting(1);
        try
        {
            await gate.WaitAsync(token);
        }
        finally
        {
            metrics.ChangeWaiting(-1);
        }
    }

    private static async Task Admitted(IShapeMetrics metrics, long bytes, Func<CancellationToken, Task> work, CancellationToken token)
    {
        using var reservation = metrics.TryReserve(bytes, null) ?? throw new InvalidOperationException("admission refused");
        await work(token);
    }

    private static (string Name, string Keys, string Values) Describe(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var entries = tags.ToArray();
        return (instrument.Name,
            string.Join(',', entries.Select(entry => entry.Key)),
            string.Join(',', entries.Select(entry => Convert.ToString(entry.Value, CultureInfo.InvariantCulture))));
    }

    private static double Sample(string scrape, string name) =>
        double.Parse(
            scrape.Split('\n').Single(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#') && MetricName(line) == name).Split(' ')[^1],
            CultureInfo.InvariantCulture);

    private static string MetricName(string line) => line[..line.IndexOfAny(['{', ' '])];
}
