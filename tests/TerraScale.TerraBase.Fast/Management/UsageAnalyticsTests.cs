using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class UsageAnalyticsTests
{
    [Test]
    [Arguments("1h", 60, 60)]
    [Arguments("24h", 24, 3600)]
    [Arguments("7d", 28, 21600)]
    [Arguments("30d", 30, 86400)]
    public async Task Analytics_buckets_real_requests_errors_and_committed_operations_and_survives_a_flush(string range, int points, int bucketSeconds)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 21, 30, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1", time: clock);
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        await ManagementApi.CreateCollectionAsync(client, database, "people");
        var document = ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "one"));
        using var put = await client.PutAsJsonAsync(document, new { name = new Faker().Name.FullName() });
        put.EnsureSuccessStatusCode();
        using var get = await client.GetAsync(document);
        get.EnsureSuccessStatusCode();
        using var missing = await client.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "missing")));
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var patch = await client.PatchAsync(document, new StringContent(JsonSerializer.Serialize(new { name = new Faker().Name.FullName() }), Encoding.UTF8, "application/merge-patch+json"));
        patch.EnsureSuccessStatusCode();
        using var delete = await client.DeleteAsync(document);
        delete.EnsureSuccessStatusCode();

        var route = ManagementApi.Uri($"/projects/{database.Project}/analytics?range={range}&region=gru-1");
        var before = await ReadAsync(client, route);
        var series = before.GetProperty("items")[0].GetProperty("series");
        var point = series.EnumerateArray().Single(value => value.GetProperty("operations").TryGetProperty("put", out _));
        await Assert.That(before.GetProperty("bucketSeconds").GetInt32()).IsEqualTo(bucketSeconds);
        await Assert.That(series.GetArrayLength()).IsEqualTo(points);
        await Assert.That(point.GetProperty("requests").GetProperty("get").GetInt64()).IsEqualTo(2);
        await Assert.That(point.GetProperty("errors").GetInt64()).IsEqualTo(1);
        await Assert.That(point.GetProperty("operations").GetProperty("put").GetInt64()).IsEqualTo(1);
        await Assert.That(point.GetProperty("operations").GetProperty("patch").GetInt64()).IsEqualTo(1);
        await Assert.That(point.GetProperty("operations").GetProperty("delete").GetInt64()).IsEqualTo(1);
        await Assert.That(point.GetProperty("latency").GetProperty("p50Ms").GetDouble()).IsGreaterThan(0);
        await Assert.That(point.GetProperty("latency").GetProperty("p99Ms").GetDouble()).IsGreaterThanOrEqualTo(point.GetProperty("latency").GetProperty("p95Ms").GetDouble());
        await Assert.That(point.GetProperty("clientBandwidth").GetProperty("bytesReceived").GetInt64()).IsGreaterThan(0);
        await Assert.That(point.GetProperty("clientBandwidth").GetProperty("bytesSent").GetInt64()).IsGreaterThan(0);
        await Assert.That(point.GetProperty("replicationBandwidth").GetProperty("bytesSent").GetInt64()).IsEqualTo(0);

        await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(CancellationToken.None);
        var after = await ReadAsync(client, route);
        await Assert.That(after.GetRawText()).IsEqualTo(before.GetRawText());
    }

    [Test]
    public async Task Seven_day_buckets_exclude_older_hourly_records_before_the_first_boundary()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 21, 30, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1", time: clock);
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        await ManagementApi.CreateCollectionAsync(client, database, "people");
        using var older = await client.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "old")),
            new { name = new Faker().Name.FullName() });
        older.EnsureSuccessStatusCode();
        await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromDays(7));
        using var current = await client.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "current")),
            new { name = new Faker().Name.FullName() });
        current.EnsureSuccessStatusCode();
        await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(CancellationToken.None);
        foreach (var feature in new[] { "analytics", "replication" })
        {
            var result = await ReadAsync(client, ManagementApi.Uri($"/projects/{database.Project}/{feature}?range=7d"));
            var series = result.GetProperty("items")[0].GetProperty("series");
            await Assert.That(series.GetArrayLength()).IsEqualTo(28);
            if (feature == "analytics")
            {
                await Assert.That(series.EnumerateArray().Sum(point => point.GetProperty("operations").TryGetProperty("put", out var count)
                    ? count.GetInt64() : 0)).IsEqualTo(1);
            }
        }
    }

    [Test]
    [Arguments("analytics")]
    [Arguments("replication")]
    public async Task Project_JWT_scope_and_database_narrowing_protect_every_observation(string feature)
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        var first = await api.CreateManagedDatabaseAsync("first");
        var second = await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "second");
        await api.VerifyProjectBindingAsync(first.Organization, first.Project);
        using var restricted = ProjectClient(api, first.Project, database: "first");
        var visible = await ReadAsync(restricted, ManagementApi.Uri($"/projects/{first.Project}/{feature}"));
        await Assert.That(visible.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("databaseId").GetString()!)).IsEquivalentTo([first.Id]);
        using var another = await restricted.GetAsync(ManagementApi.Uri($"/projects/{first.Project}/{feature}?database={second.Id}"));
        await Assert.That(another.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var route = await restricted.GetAsync(ManagementApi.Uri($"/projects/{first.Project}/databases/second/{feature}"));
        await Assert.That(route.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var wrongProject = ProjectClient(api, second.Id);
        using var crossed = await wrongProject.GetAsync(ManagementApi.Uri($"/projects/{first.Project}/{feature}"));
        await Assert.That(crossed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var noScope = ProjectClient(api, first.Project, scope: "documents:read");
        using var forbidden = await noScope.GetAsync(ManagementApi.Uri($"/projects/{first.Project}/{feature}"));
        await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var key = await api.CreateClientAsync(first.Organization, "documents:read");
        using var unauthorized = await key.GetAsync(ManagementApi.Uri($"/projects/{first.Project}/{feature}"));
        await Assert.That(unauthorized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var admin = await api.Server.Client.GetAsync(ManagementApi.Uri($"/projects/{first.Project}/{feature}"));
        await Assert.That(admin.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Empty_buckets_invalid_ranges_and_OpenAPI_have_an_explicit_contract()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        var empty = await ReadAsync(client, ManagementApi.Uri($"/projects/{database.Project}/analytics?range=1h"));
        await Assert.That(empty.GetProperty("items")[0].GetProperty("series").EnumerateArray()
            .All(point => point.GetProperty("latency").GetProperty("p50Ms").ValueKind == JsonValueKind.Null)).IsTrue();
        using var invalid = await client.GetAsync(ManagementApi.Uri($"/projects/{database.Project}/analytics?range=2h"));
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var status = await ReadAsync(client, ManagementApi.Uri($"/projects/{database.Project}/replication"));
        await Assert.That(status.GetProperty("items")[0].GetProperty("status").GetString()).IsEqualTo("stopped");
        await Assert.That(status.GetProperty("items")[0].GetProperty("lagMs").ValueKind).IsEqualTo(JsonValueKind.Null);
        var document = await ReadAsync(client, ManagementApi.Uri("/openapi/v1.json"));
        foreach (var feature in new[] { "analytics", "replication" })
        {
            await Assert.That(document.GetProperty("paths").TryGetProperty($"/projects/{{project}}/{feature}", out _)).IsTrue();
            await Assert.That(document.GetProperty("paths").TryGetProperty($"/projects/{{project}}/databases/{{database}}/{feature}", out _)).IsTrue();
        }
    }

    [Test]
    public async Task Concurrent_duplicate_streams_count_every_frame_and_apply_one_operation()
    {
        using var tls = new PeerTls();
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            tls.Apply(settings);
            settings["region"] = "gru-1";
        });
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        await ManagementApi.CreateCollectionAsync(client, database, "people");
        var tenant = new TerraBase.Tenancy.TenantContext(database.Organization, TerraBase.Tenancy.PlanTier.Hobby, database.Project, database.Id);
        var origin = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var id = ManagementApi.DocumentId("duplicate");
        var batch = new SyncRequest
        {
            TrafficKind = ReplicationTrafficKind.Handoff,
            CommittedGroup = ReplicationProtocol.CreateBatch(tenant, origin, 1,
            [new DocumentChange("people", id, 1, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { name = new Faker().Name.FullName() })))])
        };
        using var channel = tls.CreateChannel(api.Server.PeerAddress!);
        var peer = new Features.Replication.V1.Replication.ReplicationClient(channel);
        using var first = peer.Sync();
        using var second = peer.Sync();
        foreach (var stream in new[] { first, second })
        {
            await stream.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(tenant, origin, "sfo-1") });
            await Assert.That(await stream.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
            await Assert.That(stream.ResponseStream.Current.Welcome.Frontiers[0].DurableSequence).IsEqualTo(0UL);
        }

        await Task.WhenAll(first.RequestStream.WriteAsync(batch), second.RequestStream.WriteAsync(batch));
        foreach (var stream in new[] { first, second })
        {
            await Assert.That(await stream.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
            await Assert.That(stream.ResponseStream.Current.Durable.DurablePrefix).IsEqualTo(1UL);
            await Assert.That(await stream.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
            await stream.RequestStream.CompleteAsync();
        }

        using var retry = peer.Sync();
        await retry.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(tenant, origin, "sfo-1") });
        await Assert.That(await retry.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await retry.RequestStream.WriteAsync(batch);
        await Assert.That(await retry.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(await retry.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await retry.RequestStream.CompleteAsync();
        using var document = await client.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "duplicate")));
        document.EnsureSuccessStatusCode();
        var usage = await ReadAsync(client, ManagementApi.Uri($"/projects/{database.Project}/replication?range=1h&region=gru-1"));
        var item = usage.GetProperty("items")[0];
        await Assert.That(item.GetProperty("replicatedOps").GetInt64()).IsEqualTo(1);
        var traffic = item.GetProperty("bandwidth").EnumerateArray().Single(pair =>
            pair.GetProperty("kind").GetString() == "handoff" && pair.GetProperty("sourceRegion").GetString() == "sfo-1");
        await Assert.That(traffic.GetProperty("bytesReceived").GetInt64()).IsEqualTo(3 * (batch.CalculateSize() + 5L));
        await Assert.That(traffic.GetProperty("sourceRegion").GetString()).IsEqualTo("sfo-1");
        await Assert.That(traffic.GetProperty("destinationRegion").GetString()).IsEqualTo("gru-1");
    }

    [Test]
    public async Task Shutdown_preserves_request_histograms_and_storage_samples()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-analytics-restart-").FullName;
        var instant = new DateTimeOffset(2026, 10, 8, 21, 30, 0, TimeSpan.Zero);
        ManagedDatabaseRef database;
        string actor;
        JsonElement before;
        try
        {
            var clock = new FakeTimeProvider(instant);
            await using (var api = await ManagementApi.StartAsync(settings =>
            {
                settings["data-directory"] = directory;
                settings["region"] = "gru-1";
            }, time: clock))
            {
                database = await api.CreateManagedDatabaseAsync();
                actor = api.User;
                await api.VerifyProjectBindingAsync(database.Organization, database.Project);
                using var client = ProjectClient(api, database.Project);
                await ManagementApi.CreateCollectionAsync(client, database, "people");
                using var written = await client.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "one")), new { name = new Faker().Name.FullName() });
                written.EnsureSuccessStatusCode();
                clock.Advance(TimeSpan.FromSeconds(10));
                var route = ManagementApi.Uri($"/projects/{database.Project}/analytics?range=1h&region=gru-1");
                await TerraBaseTestServer.EventuallyAsync(async token =>
                {
                    var result = await client.GetFromJsonAsync<JsonElement>(route, token);
                    return result.GetProperty("items")[0].GetProperty("series").EnumerateArray()
                        .Any(point => point.GetProperty("storageBytes").ValueKind == JsonValueKind.Number);
                });
                before = await ReadAsync(client, route);
            }

            await using var restarted = await ManagementApi.StartAsync(settings =>
            {
                settings["data-directory"] = directory;
                settings["region"] = "gru-1";
            }, time: new FakeTimeProvider(instant.AddSeconds(10)));
            using var reader = restarted.WithToken(restarted.Issuer.Mint(actor, token =>
            {
                token.Claims["project_id"] = database.Project;
                token.Claims["scope"] = "databases:read";
            }));
            var after = await ReadAsync(reader, ManagementApi.Uri($"/projects/{database.Project}/analytics?range=1h&region=gru-1"));
            await Assert.That(after.GetRawText()).IsEqualTo(before.GetRawText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Regional_health_keeps_a_diverged_peer_visible_when_another_peer_is_healthy()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 21, 30, 0, TimeSpan.Zero));
        var peers = new ObservedPeers();
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1",
            services => services.AddSingleton<IReplicationStatus>(peers), clock);
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        var lagging = new ReplicationDatabaseStatus(0, database.Organization, database.Project, database.Id,
            Guid.NewGuid(), 3, 1, 2, true, 14, 0.25);
        var healthy = lagging with { Acknowledged = 3, DurableLag = 0, Diverged = false, CatchupSeconds = 0, ConvergenceSeconds = 1.5 };
        peers.State = new(true, true, 1, 0, [new(0, 2, [lagging], "sfo-1"), new(1, 0, [healthy], "sfo-1")]);
        clock.Advance(TimeSpan.FromSeconds(10));
        var route = ManagementApi.Uri($"/projects/{database.Project}/replication?range=1h&region=gru-1");
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            var result = await client.GetFromJsonAsync<JsonElement>(route, token);
            return result.GetProperty("items")[0].GetProperty("status").GetString() == "diverged";
        });
        var observed = (await ReadAsync(client, route)).GetProperty("items")[0];
        await Assert.That(observed.GetProperty("lagMs").GetDouble()).IsEqualTo(14000);
        await Assert.That(observed.GetProperty("divergence").GetBoolean()).IsTrue();
        await Assert.That(observed.GetProperty("convergenceMs").GetDouble()).IsEqualTo(1500);
        peers.State = new(true, true, 1, 0, [new(0, 0, [healthy], "sfo-1"), new(1, 0, [healthy], "sfo-1")]);
        clock.Advance(TimeSpan.FromSeconds(10));
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            var result = await client.GetFromJsonAsync<JsonElement>(route, token);
            return result.GetProperty("items")[0].GetProperty("status").GetString() == "healthy";
        });
    }

    [Test]
    public async Task A_silent_peer_stops_regional_health_beside_a_fresh_one_until_it_is_forgotten()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 21, 30, 0, TimeSpan.Zero));
        var peers = new ObservedPeers();
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1",
            services => services.AddSingleton<IReplicationStatus>(peers), clock);
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        var flush = api.Server.Services.GetServices<IHostedService>().OfType<UsageFlushService>().Single();
        var route = ManagementApi.Uri($"/projects/{database.Project}/replication?range=1h&region=gru-1");
        var synced = new ReplicationDatabaseStatus(0, database.Organization, database.Project, database.Id, Guid.NewGuid(), 3, 3, 0, false, 0, null);

        peers.State = new(true, true, 1, 0, [new(0, 0, [synced], "sfo-1"), new(1, 0, [synced], "iad-1")]);
        await Assert.That((await ObserveAsync(TimeSpan.FromSeconds(10))).GetProperty("status").GetString()).IsEqualTo("healthy");

        // iad-1 goes silent while sfo-1 keeps reporting: the fresh peer must not hide it.
        peers.State = new(true, true, 1, 0, [new(0, 0, [synced], "sfo-1")]);
        JsonElement mixed = default;
        for (var tick = 0; tick < 4; tick++)
        {
            mixed = await ObserveAsync(TimeSpan.FromSeconds(10));
        }

        await Assert.That(mixed.GetProperty("status").GetString()).IsEqualTo("stopped");
        await Assert.That(mixed.GetProperty("lagMs").ValueKind).IsEqualTo(JsonValueKind.Number);

        // Silent past ten minutes, iad-1 counts as removed.
        await Assert.That((await ObserveAsync(TimeSpan.FromMinutes(10))).GetProperty("status").GetString()).IsEqualTo("healthy");

        // Every peer silent, the last one having diverged: stopped, with neither current lag nor divergence.
        peers.State = new(true, true, 1, 0, [new(0, 0, [synced with { Diverged = true }], "sfo-1")]);
        await Assert.That((await ObserveAsync(TimeSpan.FromSeconds(10))).GetProperty("status").GetString()).IsEqualTo("diverged");
        peers.State = new(true, true, 1, 0, []);
        var silent = await ObserveAsync(TimeSpan.FromSeconds(40));
        await Assert.That(silent.GetProperty("status").GetString()).IsEqualTo("stopped");
        await Assert.That(silent.GetProperty("lagMs").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(silent.GetProperty("divergence").ValueKind).IsEqualTo(JsonValueKind.Null);

        async Task<JsonElement> ObserveAsync(TimeSpan advance)
        {
            clock.Advance(advance);
            await flush.FlushAsync(CancellationToken.None);
            return (await ReadAsync(client, route)).GetProperty("items")[0];
        }
    }

    [Test]
    public async Task An_open_subscription_reports_its_bytes_into_the_minute_they_flow_in_before_it_ends()
    {
        // Tokens are checked against real time: start at this real minute.
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1", time: clock);
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var data = ProjectClient(api, database.Project);
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        using var subscriber = api.WithToken(api.Issuer.Mint(api.User, descriptor =>
        {
            descriptor.Claims["project_id"] = database.Project;
            descriptor.Claims["scope"] = "shapes:read";
            descriptor.Expires = clock.GetUtcNow().AddMinutes(10).UtcDateTime;
        }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var subscription = await subscriber.GetAsync(
            ManagementApi.Uri($"/projects/{database.Project}/databases/{database.Name}/collections/items/subscribe"), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        subscription.EnsureSuccessStatusCode();
        using var events = new StreamReader(await subscription.Content.ReadAsStreamAsync(deadline.Token));
        while (await events.ReadLineAsync(deadline.Token) is { Length: > 0 })
        {
        }

        // A minute later a change reaches the still-open subscription; the write that delivers it reports what it sent.
        clock.Advance(TimeSpan.FromMinutes(1));
        using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "later")), new { name = new Faker().Name.FullName() }, deadline.Token);
        written.EnsureSuccessStatusCode();
        var putBytes = (await written.Content.ReadAsByteArrayAsync(deadline.Token)).Length;
        while (await events.ReadLineAsync(deadline.Token) is { } line && !line.StartsWith("data:", StringComparison.Ordinal))
        {
        }

        await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(deadline.Token);
        var analytics = await ReadAsync(data, ManagementApi.Uri($"/projects/{database.Project}/analytics?range=1h&region=gru-1"));
        var minute = analytics.GetProperty("items")[0].GetProperty("series").EnumerateArray()
            .Single(point => point.GetProperty("start").GetDateTimeOffset() == start.AddMinutes(1));
        // More than the PUT's own response: the subscription's bytes are there while it is still open.
        await Assert.That(minute.GetProperty("clientBandwidth").GetProperty("bytesSent").GetInt64()).IsGreaterThan(putBytes);
    }

    [Test]
    public async Task A_request_for_an_unknown_database_name_records_no_measurements()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        var missing = "missing-" + new Faker().Random.AlphaNumeric(8).ToLowerInvariant();
        using var response = await client.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/{missing}"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var found = await client.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/{database.Name}"));
        found.EnsureSuccessStatusCode();

        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        var window = new UsageWindow(UsageWindow.Hour, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600, 1);
        var (unknown, _) = await meter.GetObservationsAsync(new(database.Organization, PlanTier.Hobby, database.Project, missing), window, CancellationToken.None);
        await Assert.That(unknown.Meters).IsNull();
        var (known, _) = await meter.GetObservationsAsync(new(database.Organization, PlanTier.Hobby, database.Project, database.Id), window, CancellationToken.None);
        await Assert.That(known.Meters?.ContainsKey("requests|gru-1||get") ?? false).IsTrue();
    }

    private sealed class ObservedPeers : IReplicationStatus
    {
        public ReplicationStatus State { get; set; } = new(true, true, 0, 0, []);
        public ReplicationStatus Snapshot() => State;
    }

    internal static HttpClient ProjectClient(ManagementApi api, string project, string? database = null, string scope = "databases:read documents:read documents:write") =>
        api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = scope;
            if (database is not null)
            {
                token.Claims["database"] = database;
            }
        }));

    internal static async Task<JsonElement> ReadAsync(HttpClient client, Uri uri)
    {
        using var response = await client.GetAsync(uri);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
