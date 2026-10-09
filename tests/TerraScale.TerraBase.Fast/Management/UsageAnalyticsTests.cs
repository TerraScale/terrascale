using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management;
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
            Ulid.NewUlid(), 3, 1, 2, true, 14, 0.25);
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
        var synced = new ReplicationDatabaseStatus(0, database.Organization, database.Project, database.Id, Ulid.NewUlid(), 3, 3, 0, false, 0, null);

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

        // A diverged peer goes silent beside a fresh healthy one: its old divergence is not current, so stopped, not diverged.
        peers.State = new(true, true, 1, 0, [new(0, 0, [synced], "sfo-1"), new(1, 0, [synced with { Diverged = true }], "iad-1")]);
        await Assert.That((await ObserveAsync(TimeSpan.FromSeconds(10))).GetProperty("status").GetString()).IsEqualTo("diverged");
        peers.State = new(true, true, 1, 0, [new(0, 0, [synced], "sfo-1")]);
        JsonElement stale = default;
        for (var tick = 0; tick < 4; tick++)
        {
            stale = await ObserveAsync(TimeSpan.FromSeconds(10));
        }

        await Assert.That(stale.GetProperty("status").GetString()).IsEqualTo("stopped");
        await Assert.That(stale.GetProperty("divergence").GetBoolean()).IsFalse();
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
    public async Task A_sparse_subscription_reports_its_bytes_into_the_minutes_they_flowed_in_while_open()
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
        var (snapshotBytes, _) = await FrameAsync();

        // Idle for three minutes, then one change: the snapshot stays in its own minute, the change lands in the later one.
        clock.Advance(TimeSpan.FromMinutes(3));
        var id = ManagementApi.DocumentId("later");
        using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "later")), new { name = new Faker().Name.FullName() }, deadline.Token);
        written.EnsureSuccessStatusCode();
        var putBytes = (await written.Content.ReadAsByteArrayAsync(deadline.Token)).Length;
        // Live waits on the fake clock may add empty up-to-date frames before or after the advance; all follow the snapshot.
        var (change, later) = (0L, 0L);
        while (change == 0)
        {
            var (bytes, text) = await FrameAsync();
            later += bytes;
            change = text.Contains(id, StringComparison.Ordinal) ? bytes : 0;
        }

        await api.Server.Services.GetServices<IHostedService>().OfType<UsageFlushService>().Single().FlushAsync(deadline.Token);
        var series = (await ReadAsync(data, ManagementApi.Uri($"/projects/{database.Project}/analytics?range=1h&region=gru-1")))
            .GetProperty("items")[0].GetProperty("series").EnumerateArray().ToArray();
        long Sent(DateTimeOffset minute)
        {
            return series.Single(point => point.GetProperty("start").GetDateTimeOffset() == minute)
                .GetProperty("clientBandwidth").GetProperty("bytesSent").GetInt64();
        }

        await Assert.That(Sent(start)).IsGreaterThanOrEqualTo(snapshotBytes);
        await Assert.That(Sent(start.AddMinutes(3))).IsGreaterThanOrEqualTo(putBytes + change);
        await Assert.That(Sent(start.AddMinutes(3))).IsLessThanOrEqualTo(putBytes + later);

        // One server-sent event: its lines through the blank line that ends it, and their UTF-8 bytes.
        async Task<(long Bytes, string Text)> FrameAsync()
        {
            var (bytes, text) = (0L, new StringBuilder());
            while (await events.ReadLineAsync(deadline.Token) is { } line)
            {
                bytes += Encoding.UTF8.GetByteCount(line) + 1;
                if (line.Length == 0)
                {
                    break;
                }

                text.AppendLine(line);
            }

            return (bytes, text.ToString());
        }
    }

    [Test]
    public async Task Observed_history_stays_readable_after_its_region_leaves_the_placement()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        await ManagementApi.CreateCollectionAsync(client, database, "people");
        using (var written = await client.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "one")), new { name = new Faker().Name.FullName() }))
        {
            written.EnsureSuccessStatusCode();
        }

        await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(CancellationToken.None);

        // The database moves to sfo-1 alone: gru-1 leaves its placement, but what gru-1 observed is retained history.
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using (var transaction = await system.BeginTransactionAsync(write: true, token))
        {
            var record = new ManagedDatabase(database.Id, database.Project, database.Name, null, DateTimeOffset.UtcNow,
                HomeRegion: "sfo-1", ReplicaRegions: [], PlacementVersion: 2);
            await transaction.PutAsync($"projects/{database.Project}/databases", database.Id,
                JsonSerializer.SerializeToUtf8Bytes(record, TerraBase.Hosting.TerraBaseJsonContext.Default.ManagedDatabase), token);
            await transaction.CommitAsync(token);
        }

        var items = (await ReadAsync(client, ManagementApi.Uri($"/projects/{database.Project}/analytics?range=1h&region=gru-1"))).GetProperty("items");
        await Assert.That(items.GetArrayLength()).IsEqualTo(1);
        await Assert.That(items[0].GetProperty("region").GetString()).IsEqualTo("gru-1");
        await Assert.That(items[0].GetProperty("series").EnumerateArray()
            .Sum(point => point.GetProperty("requests").TryGetProperty("put", out var puts) ? puts.GetInt64() : 0)).IsGreaterThan(0);
    }

    [Test]
    public async Task Storage_sampling_covers_more_databases_than_a_minute_samples_whatever_order_they_list_in()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 21, 30, 0, TimeSpan.Zero));
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1", hook.Install, clock);
        // Registered databases: a flush keeps only the observations of databases the catalog names.
        var first = await api.CreateManagedDatabaseAsync("db0");
        var databases = new List<TenantContext> { new(first.Organization, PlanTier.Hobby, first.Project, first.Id) };
        for (var i = 1; i < 140; i++)
        {
            var created = await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "db" + i.ToString(CultureInfo.InvariantCulture));
            databases.Add(new(created.Organization, PlanTier.Hobby, created.Project, created.Id));
        }

        // Two minutes sample 2 × 128 databases; the second pass lists them in reverse.
        var flush = api.Server.Services.GetServices<IHostedService>().OfType<UsageFlushService>().Single();
        foreach (var reverse in new[] { false, true })
        {
            hook.ReverseDatabases = reverse;
            clock.Advance(TimeSpan.FromMinutes(1));
            await flush.SampleStorageAsync(CancellationToken.None);
            await flush.FlushAsync(CancellationToken.None);
        }

        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        var window = new UsageWindow(UsageWindow.Hour, clock.GetUtcNow().ToUnixTimeSeconds() / 3600, 1);
        var unsampled = 0;
        foreach (var database in databases)
        {
            var (lifetime, _) = await meter.GetObservationsAsync(database, window, CancellationToken.None);
            unsampled += lifetime.Meters?.ContainsKey("storage_bytes|gru-1||sample") == true ? 0 : 1;
        }

        await Assert.That(unsampled).IsEqualTo(0);
    }

    [Test]
    public async Task A_storage_pass_that_outlasts_its_minute_samples_each_minute_once()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 21, 30, 0, TimeSpan.Zero));
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1", hook.Install, clock);
        var created = await api.CreateManagedDatabaseAsync();
        var database = new TenantContext(created.Organization, PlanTier.Hobby, created.Project, created.Id);

        // Sizing the database takes past the end of the minute its pass started in, once.
        var slow = 1;
        hook.BeforeSize = (_, _) =>
        {
            if (Interlocked.Exchange(ref slow, 0) == 1)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
            }

            return ValueTask.CompletedTask;
        };
        // The background sampler may start this minute's pass as soon as the clock reaches it.
        var started = (clock.GetUtcNow().ToUnixTimeSeconds() / 60) + 1;
        clock.Advance(TimeSpan.FromMinutes(1));
        var flush = api.Server.Services.GetServices<IHostedService>().OfType<UsageFlushService>().Single();
        await flush.SampleStorageAsync(CancellationToken.None);
        await flush.SampleStorageAsync(CancellationToken.None);
        await flush.FlushAsync(CancellationToken.None);

        // Its sample stays in that minute, so the next minute's pass samples the database again, once.
        var (_, series) = await api.Server.Services.GetRequiredService<UsageMeter>()
            .GetObservationsAsync(database, new UsageWindow(UsageWindow.Minute, started, 2), CancellationToken.None);
        await Assert.That(series.Select(point => point.Meters?.GetValueOrDefault("storage_bytes|gru-1||sample")?.Count ?? 0))
            .IsEquivalentTo([1L, 1L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_primary_refusing_measurements_still_receives_the_read_and_write_totals()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        await ManagementApi.CreateCollectionAsync(client, database, "people");
        using (var written = await client.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "one")), new { name = new Faker().Name.FullName() }))
        {
            written.EnsureSuccessStatusCode();
        }

        var reads = new Faker().Random.Int(1, 3);
        for (var i = 0; i < reads; i++)
        {
            (await client.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "one")))).EnsureSuccessStatusCode();
        }

        // A primary that predates minute scopes and measurements refuses any batch carrying them, as the older validator did.
        var received = new List<KeyValuePair<UsageKey, OpCounts>>();
        var refused = 0;
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        while (await meter.SendAsync((_, batch, _) =>
        {
            if (batch.Any(entry => entry.Key.Scope == TerraBase.Features.Management.Usage.UsageScope.Minute || entry.Value.Meters is not null))
            {
                refused++;
                return Task.FromResult<AddUsageResult>(new Validation("an older primary"));
            }

            received.AddRange(batch);
            return Task.FromResult<AddUsageResult>(new Done());
        }, CancellationToken.None))
        {
        }

        await Assert.That(refused).IsGreaterThan(0);
        var owned = received.Where(entry => entry.Key.Database.DatabaseId == database.Id).ToArray();
        await Assert.That(owned.Sum(entry => entry.Value.Reads)).IsEqualTo(reads);
        await Assert.That(owned.Sum(entry => entry.Value.Writes)).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Explain_is_metered_and_near_limit_byte_counters_total_without_overflow()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        await ManagementApi.CreateCollectionAsync(client, database, "people");
        var analytics = ManagementApi.Uri($"/projects/{database.Project}/analytics?range=1h&region=gru-1");
        async Task<long> PostsAsync()
        {
            return (await ReadAsync(client, analytics)).GetProperty("items")[0].GetProperty("series").EnumerateArray()
                .Sum(point => point.GetProperty("requests").TryGetProperty("post", out var posts) ? posts.GetInt64() : 0);
        }

        // EXPLAIN resolves its database itself; it is still one metered request of that database.
        var before = await PostsAsync();
        using (var explained = await client.PostAsJsonAsync(
            ManagementApi.Uri($"/projects/{database.Project}/databases/{database.Name}/sql/explain"), new { sql = "SELECT * FROM people LIMIT 1" }))
        {
            explained.EnsureSuccessStatusCode();
        }

        await Assert.That(await PostsAsync()).IsEqualTo(before + 1);

        // Two saturated byte counters of one database total at the 64-bit limit rather than overflowing the response.
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        meter.RecordReplicationBytes(tenant, "sfo-1", "delta", true, long.MaxValue);
        meter.RecordReplicationBytes(tenant, "sfo-1", "handoff", true, long.MaxValue);
        var series = (await ReadAsync(client, analytics)).GetProperty("items")[0].GetProperty("series").EnumerateArray().ToArray();
        await Assert.That(series.Max(point => point.GetProperty("replicationBandwidth").GetProperty("bytesSent").GetInt64())).IsEqualTo(long.MaxValue);
        var replication = await ReadAsync(client, ManagementApi.Uri($"/projects/{database.Project}/replication?range=1h&region=gru-1"));
        await Assert.That(replication.GetProperty("items")[0].GetProperty("bandwidth").EnumerateArray()
            .All(pair => pair.GetProperty("bytesSent").GetInt64() == long.MaxValue)).IsTrue();

        // Two saturated request readings fill one latency bin past the 64-bit range: percentiles still name that bin.
        var latency = new long[UsageReading.LatencyBounds.Count];
        latency[5] = long.MaxValue;
        var meters = new Dictionary<string, UsageReading>(StringComparer.Ordinal);
        foreach (var method in new[] { "get", "post" })
        {
            var dimension = new UsageDimension("requests", "gru-1", "", method);
            meters[dimension.Key] = new(dimension, long.MaxValue, 0, latency);
        }

        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var key = new UsageKey(new(database.Organization, database.Project, database.Id), minute / 60, TerraBase.Features.Management.Usage.UsageScope.Minute, (int)(minute % 60));
        _ = await meter.AcceptAsync("saturated", Ulid.NewUlid().ToString(), 1, [KeyValuePair.Create(key, new OpCounts(0, 0, meters))], CancellationToken.None);
        var saturated = (await ReadAsync(client, analytics)).GetProperty("items")[0].GetProperty("series").EnumerateArray()
            .Single(point => point.GetProperty("latency").GetProperty("p99Ms").ValueKind == JsonValueKind.Number
                && point.GetProperty("latency").GetProperty("p50Ms").GetDouble() == UsageReading.LatencyBounds[5]);
        await Assert.That(saturated.GetProperty("latency").GetProperty("p99Ms").GetDouble()).IsEqualTo(UsageReading.LatencyBounds[5]);
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
