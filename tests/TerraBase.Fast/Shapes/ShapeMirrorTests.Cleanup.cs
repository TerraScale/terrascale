using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Shapes;
using TerraBase.Fast.Documents;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

public sealed partial class ShapeMirrorTests
{
    [Test]
    public async Task An_outage_past_lifecycle_expiry_purges_from_the_durable_inventory_after_restart_without_listing_the_bucket()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("terrabase-cleanup-");
        try
        {
            using var cloudflare = new FakeCloudflare();
            await using var nats = await NatsTestServer.StartAsync(token);
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var (s3, handle, key) = await PublishOneAsync(time, cloudflare, nats, directory.FullName);
            await Assert.That(s3.Expire(key)).IsTrue().Because("the bucket's lifecycle rule removed the object during the outage");
            time.Advance(TimeSpan.FromDays(8));
            var listings = 0;
            s3.BeforeList = (prefix, _) =>
            {
                if (prefix.StartsWith("v1/shapes/", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref listings);
                }

                return Task.CompletedTask;
            };

            await using var restarted = await RestartAsync(directory.FullName, s3, cloudflare, nats, time);
            var cleanup = restarted.Server.Services.GetRequiredService<IShapeCleanup>();
            _ = await cleanup.DrainAsync(token);
            _ = await cleanup.DrainAsync(token);

            await Assert.That(cloudflare.Purged.ToArray()).IsEquivalentTo([ShapeCleanupLimits.Prefix(handle)])
                .Because("the overdue handle prefix is purged although the bucket no longer holds the object");
            await Assert.That(Volatile.Read(ref listings)).IsEqualTo(0).Because("recovery drains the inventory without listing the bucket");
            await Assert.That(await restarted.Server.MetricAsync("terrabase_shape_cleanup_pending", cancellationToken: token)).IsEqualTo(0);
            await Assert.That(await restarted.Server.MetricAsync("terrabase_shape_cleanup_objects", cancellationToken: token)).IsEqualTo(0)
                .Because("acknowledged rows past their deadline are collected");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task A_throttled_purge_honors_retry_after_in_the_shared_budget_and_completes_after_it()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var cloudflare = new FakeCloudflare();
        var throttled = 0;
        cloudflare.PurgeAnswer = _ => Interlocked.Exchange(ref throttled, 1) == 0
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(40)) },
                Content = new StringContent("""{"success":false,"errors":[{"code":10000}]}""", Encoding.UTF8, "application/json"),
            }
            : null;
        await using var nats = await NatsTestServer.StartAsync(token);
        await using var harness = await Harness.StartAsync(cloudflare: cloudflare, nats: nats);
        var start = await harness.Db.HeadAsync();
        for (var commit = 0; commit < 3; commit++)
        {
            _ = await harness.CommitLargeAsync();
        }

        var chunks = await harness.SealAllAsync(start);
        await Assert.That(harness.Queue(chunks[0])).IsTrue();
        await Assert.That(harness.Queue(chunks[1])).IsTrue();
        await harness.MirroredAsync(2);
        var objects = (await harness.ManifestAsync(start)).Objects;
        var throttledAt = DateTimeOffset.FromUnixTimeMilliseconds(harness.Time.GetUtcNow().ToUnixTimeMilliseconds());

        await Assert.That(await harness.Mirror.TrimAsync(harness.Tenant, harness.Shape.Handle, chunks[2].Offset, harness.Cancellation)).IsEqualTo(2);
        _ = await harness.DrainAsync();
        var prefix = ShapeCleanupLimits.Prefix(harness.Shape.Handle);
        await Assert.That(cloudflare.Attempts.Single().Prefixes).IsEquivalentTo([prefix]).Because("both objects of the handle coalesce into one prefix");
        await Assert.That(objects.All(entry => !harness.S3.Objects.ContainsKey(entry.Object))).IsTrue().Because("R2 deletion completes before the purge");
        await Assert.That(await harness.Server.MetricAsync("terrabase_shape_cleanup_pending", cancellationToken: token)).IsEqualTo(2);
        await Assert.That(await nats.NextPurgeAsync(token)).IsGreaterThanOrEqualTo(throttledAt + TimeSpan.FromSeconds(40))
            .Because("Retry-After defers the shared account budget for every node");

        _ = await harness.DrainAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(39));
        _ = await harness.DrainAsync();
        await Assert.That(cloudflare.Attempts.Count).IsEqualTo(1).Because("no purge is sent before Retry-After passes");

        harness.Time.Advance(TimeSpan.FromSeconds(2));
        _ = await harness.DrainAsync();
        await Assert.That(cloudflare.Attempts.Count).IsEqualTo(2);
        await Assert.That(cloudflare.Purged.ToArray()).IsEquivalentTo([prefix]);
        await Assert.That(await harness.Server.MetricAsync("terrabase_shape_cleanup_pending", cancellationToken: token)).IsEqualTo(0)
            .Because("completion is acknowledged only after success:true");
    }

    [Test]
    public async Task Refused_shape_credentials_park_cleanup_durably_until_a_restart_with_repaired_credentials()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("terrabase-cleanup-");
        try
        {
            using var cloudflare = new FakeCloudflare();
            await using var nats = await NatsTestServer.StartAsync(token);
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var (s3, handle, key) = await PublishOneAsync(time, cloudflare, nats, directory.FullName);
            time.Advance(TimeSpan.FromDays(8));

            await using (var revoked = await RestartAsync(directory.FullName, s3, cloudflare, nats, time, s3.RejectShapeCredentials))
            {
                var cleanup = revoked.Server.Services.GetRequiredService<IShapeCleanup>();
                _ = await cleanup.DrainAsync(token);
                time.Advance(ShapeCleanupLimits.Cycle * 2);
                _ = await cleanup.DrainAsync(token);
                await Assert.That(s3.Objects.ContainsKey(key)).IsTrue().Because("a refused deletion never drops or completes the work");
                await Assert.That(await revoked.Server.MetricAsync("terrabase_shape_cleanup_parked", cancellationToken: token)).IsEqualTo(1);
                await Assert.That(await revoked.Server.MetricAsync("terrabase_shape_cleanup_attempts_total", "outcome=\"parked\"", token)).IsEqualTo(1)
                    .Because("parked work is not retried until credentials are repaired");
                await Assert.That(cloudflare.Attempts.Count).IsEqualTo(0);
            }

            await using var repaired = await RestartAsync(directory.FullName, s3, cloudflare, nats, time);
            _ = await repaired.Server.Services.GetRequiredService<IShapeCleanup>().DrainAsync(token);
            await Assert.That(s3.Objects.ContainsKey(key)).IsFalse();
            await Assert.That(cloudflare.Purged.ToArray()).IsEquivalentTo([ShapeCleanupLimits.Prefix(handle)]);
            await Assert.That(await repaired.Server.MetricAsync("terrabase_shape_cleanup_parked", cancellationToken: token)).IsEqualTo(0);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task A_full_inventory_refuses_publication_visibly_and_recovers_once_cleanup_frees_capacity()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var logs = new CleanupLogs();
        await using var harness = await Harness.StartAsync(services: collection => TerraBaseTestServer.CaptureDebugLogs(collection, logs));
        var start = await harness.Db.HeadAsync();
        _ = await harness.CommitLargeAsync();
        var chunk = await harness.SealAsync(start);
        var store = harness.Server.Services.GetRequiredService<IShapeCleanupStore>();
        var now = harness.Time.GetUtcNow().ToUnixTimeMilliseconds();
        var faker = new Faker();
        for (var handle = 0; handle < ShapeCleanupLimits.Handles; handle++)
        {
            await Assert.That(await store.ReserveAsync(Placeholder(faker, harness, now), now, token)).IsEqualTo(ShapeCleanupAdmission.Live);
        }

        await Assert.That(await store.ReserveAsync(Placeholder(faker, harness, now), now, token)).IsEqualTo(ShapeCleanupAdmission.Saturated);
        await Assert.That(harness.Queue(chunk)).IsTrue();
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(logs.Entries.Any(entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("publication refused", StringComparison.Ordinal))));
        await Assert.That(harness.Puts.Values.Sum()).IsEqualTo(0).Because("nothing is uploaded before its cleanup intent is durable");
        using (var refused = await harness.GetManifestAsync(start))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            using var body = JsonDocument.Parse(await refused.Content.ReadAsByteArrayAsync(token));
            await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unavailable");
        }

        _ = await harness.DrainAsync();
        await Assert.That(await harness.Server.MetricAsync("terrabase_shape_cleanup_saturated", cancellationToken: token)).IsEqualTo(1);

        harness.Time.Advance(TimeSpan.FromMinutes(2));
        _ = await harness.DrainAsync();
        _ = await harness.DrainAsync();
        await Assert.That(await harness.Server.MetricAsync("terrabase_shape_cleanup_saturated", cancellationToken: token)).IsEqualTo(0);
        await Assert.That(await harness.Server.MetricAsync("terrabase_shape_cleanup_objects", cancellationToken: token)).IsEqualTo(0);

        await Assert.That(harness.Queue(chunk)).IsTrue();
        await harness.MirroredAsync(1);
        using var recovered = await harness.GetManifestAsync(start);
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK).Because("admission recovers without a restart once capacity is free");
        await Assert.That((await harness.ManifestAsync(start)).Objects.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Deleting_a_database_cleans_its_objects_and_keeps_a_sibling_database_readable()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var cloudflare = new FakeCloudflare();
        await using var nats = await NatsTestServer.StartAsync(token);
        await using var harness = await Harness.StartAsync(cloudflare: cloudflare, nats: nats);
        var start = await harness.Db.HeadAsync();
        _ = await harness.CommitLargeAsync();
        var chunk = await harness.SealAsync(start);
        await Assert.That(harness.Queue(chunk)).IsTrue();
        await harness.MirroredAsync(1);
        var deleted = (await harness.ManifestAsync(start)).Objects.Single();

        (await harness.Api.SendAsync(HttpMethod.Put, harness.Api.DatabasePath("sibling"), cancellationToken: token)).Status.ShouldBe(HttpStatusCode.Created);
        (await harness.Api.SendAsync(HttpMethod.Post, harness.Api.DatabasePath("sibling") + "/collections", new { name = Collection }, cancellationToken: token))
            .Status.ShouldBe(HttpStatusCode.Created);
        var siblingTenant = await harness.Api.TenantOfAsync("sibling", token);
        var sibling = await Database.OpenAsync(harness.Server, siblingTenant);
        var siblingShape = await Harness.ShapeAsync(sibling);
        var siblingStart = await sibling.HeadAsync();
        _ = await sibling.CommitAsync(async transaction => _ = await transaction.PutAsync(Collection, DocumentApi.NewId(),
            JsonSerializer.SerializeToUtf8Bytes(new { pad = new string('y', LargeBody) }), CancellationToken.None));
        _ = await sibling.SealAsync(siblingShape, siblingStart);
        var siblingChunk = await sibling.ChunkAsync(siblingShape.Handle, siblingStart);
        await Assert.That(harness.Mirror.Queue(siblingTenant, siblingShape.ShapeId, siblingShape.Handle, Text(siblingChunk.Offset), Text(siblingChunk.EndOffset),
            siblingChunk.EndOffset, siblingChunk.ValuesAt, siblingChunk.Body, harness.Time.GetUtcNow())).IsTrue();
        await harness.MirroredAsync(1);
        var kept = (await harness.Mirror.ManifestAsync(siblingTenant, siblingShape.ShapeId, siblingShape.Handle, Text(siblingStart), 0, token)).Objects.Single();

        await harness.Api.DeleteDatabaseAsync(cancellationToken: token);
        await harness.Server.Services.GetRequiredService<EngineManagementService>().ConvergeAsync(token);
        _ = await harness.DrainAsync();

        await Assert.That(harness.S3.Objects.ContainsKey(deleted.Object)).IsFalse();
        await Assert.That(cloudflare.Purged.ToArray()).IsEquivalentTo([ShapeCleanupLimits.Prefix(harness.Shape.Handle)])
            .Because("only the deleted database's handle is purged");
        await Assert.That(harness.S3.Objects.ContainsKey(kept.Object)).IsTrue();
        var siblingManifest = await harness.Mirror.ManifestAsync(siblingTenant, siblingShape.ShapeId, siblingShape.Handle, Text(siblingStart), 0, token);
        await Assert.That(siblingManifest.Objects.Single().Object).IsEqualTo(kept.Object);
        await Assert.That(await harness.Server.Services.GetRequiredService<IShapeKeyRetention>().HasObjectAsync(siblingTenant, siblingShape.Handle,
            long.Parse(kept.Epoch, System.Globalization.CultureInfo.InvariantCulture), kept.KeyId, token)).IsTrue();
        var store = harness.Server.Services.GetRequiredService<IShapeCleanupStore>();
        var now = harness.Time.GetUtcNow().ToUnixTimeMilliseconds();
        await Assert.That(await store.ReserveAsync(new ShapeCleanupObject(ShapeObjectStore.Key(harness.Shape.ShapeId, harness.Shape.Handle, "9"), harness.Tenant,
            harness.Db.Store.Incarnation, 9, 0, Ulid.NewUlid().ToString(), now + 60_000), now, token)).IsEqualTo(ShapeCleanupAdmission.Refused)
            .Because("the deletion fence refuses new publication for the deleted database");
    }

    [Test]
    public async Task Nodes_sharing_a_real_nats_server_take_at_most_five_purge_slots_a_minute()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var nats = await NatsTestServer.StartAsync(token);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var nodes = new TerraBaseTestServer[3];
        try
        {
            for (var node = 0; node < nodes.Length; node++)
            {
                nodes[node] = await TerraBaseTestServer.StartAsync(nats.Configure, services => services.AddSingleton<TimeProvider>(time));
            }

            var budgets = nodes.Select(node => node.Services.GetRequiredService<IShapePurgeBudget>()).ToArray();
            List<DateTimeOffset> granted = [];
            for (var step = 0; step < 72; step++)
            {
                var slots = await Task.WhenAll(budgets.Select(budget => budget.TakeAsync(token).AsTask()));
                await Assert.That(slots.Count(slot => slot.Granted)).IsLessThanOrEqualTo(1);
                granted.AddRange(slots.Where(slot => slot.Granted).Select(slot => slot.NotBefore));
                time.Advance(TimeSpan.FromMilliseconds(2500));
            }

            await Assert.That(granted.Count).IsEqualTo(15).Because("one grant every 12.5 s across all nodes over three minutes");
            await Assert.That(granted.Zip(granted.Skip(1)).All(pair => pair.Second - pair.First >= ShapePurgeBudget.Spacing)).IsTrue();
            await Assert.That(granted.All(at => granted.Count(other => other >= at && other < at + TimeSpan.FromMinutes(1)) <= 5)).IsTrue();

            var until = time.GetUtcNow() + TimeSpan.FromMinutes(1);
            await budgets[0].DeferAsync(until, token);
            var deferred = await Task.WhenAll(budgets.Select(budget => budget.TakeAsync(token).AsTask()));
            await Assert.That(deferred.All(slot => !slot.Granted && slot.NotBefore == DateTimeOffset.FromUnixTimeMilliseconds(until.ToUnixTimeMilliseconds()))).IsTrue()
                .Because("one node's Retry-After holds every node back");
        }
        finally
        {
            foreach (var node in nodes.Where(node => node is not null))
            {
                await node.DisposeAsync();
            }
        }
    }

    private static async Task<(FakeS3 S3, string Handle, string Key)> PublishOneAsync(FakeTimeProvider time, FakeCloudflare cloudflare, NatsTestServer nats,
        string directory)
    {
        await using var harness = await Harness.StartAsync(time, cloudflare, nats, settings => settings["data-directory"] = directory);
        var start = await harness.Db.HeadAsync();
        _ = await harness.CommitLargeAsync();
        var chunk = await harness.SealAsync(start);
        await Assert.That(harness.Queue(chunk)).IsTrue();
        await harness.MirroredAsync(1);
        var entry = (await harness.ManifestAsync(start)).Objects.Single();
        return (harness.S3, harness.Shape.Handle, entry.Object);
    }

    private static Task<ManagementApi> RestartAsync(string directory, FakeS3 s3, FakeCloudflare cloudflare, NatsTestServer nats, FakeTimeProvider time,
        Action<IServiceCollection>? services = null) =>
        ManagementApi.StartAsync(settings =>
        {
            s3.Configure(settings);
            nats.Configure(settings);
            settings["data-directory"] = directory;
        }, collection =>
        {
            s3.Route(collection);
            cloudflare.Route(collection);
            ManualBackupHosting.Configure(collection);
            collection.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, time);
            services?.Invoke(collection);
        }, time);

    private static ShapeCleanupObject Placeholder(Faker faker, Harness harness, long now) =>
        new("v1/shapes/h1-" + faker.Random.Hexadecimal(16, prefix: string.Empty).ToLowerInvariant() + "/"
            + Convert.ToHexStringLower(SHA256.HashData(faker.Random.Bytes(32))), harness.Tenant, harness.Db.Store.Incarnation, 0, 0,
            Ulid.NewUlid().ToString(), now + 60_000);

    private sealed class CleanupLogs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));

        public void Dispose()
        {
        }
    }
}
