using System.Diagnostics.Metrics;
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
using TerraBase.Features.Memory;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class UsageAnalyticsTests
{
    [Test]
    [Arguments("analytics", nameof(IManagementService.ListDatabasesAsync))]
    [Arguments("analytics", nameof(IManagementService.GetDatabaseAsync))]
    [Arguments("analytics", nameof(IManagementService.GetDatabaseTenantAsync))]
    [Arguments("replication", nameof(IManagementService.ListDatabasesAsync))]
    [Arguments("replication", nameof(IManagementService.GetDatabaseAsync))]
    [Arguments("replication", nameof(IManagementService.GetDatabaseTenantAsync))]
    public async Task Catalog_refusals_reach_analytics_as_503_instead_of_empty_results(string feature, string failedRead)
    {
        var refuse = false;
        var unavailable = new Unavailable("the management catalog cannot be read");
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            var registration = services.Single(service => service.ServiceType == typeof(IManagementService));
            services.Remove(registration);
            services.AddSingleton(provider => UsageApiTests.SlowManagement.Wrap(
                (IManagementService)registration.ImplementationFactory!(provider), _ => { }, method =>
                    Volatile.Read(ref refuse) && method == failedRead ? method switch
                    {
                        nameof(IManagementService.ListDatabasesAsync) => new ValueTask<ListDatabasesResult>(unavailable),
                        nameof(IManagementService.GetDatabaseAsync) => new ValueTask<GetDatabaseResult>(unavailable),
                        nameof(IManagementService.GetDatabaseTenantAsync) => new ValueTask<DatabaseTenantResult>(unavailable),
                        _ => null,
                    } : null));
        });
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        var filter = failedRead == nameof(IManagementService.GetDatabaseAsync) ? $"?database={database.Id}" : "";
        Volatile.Write(ref refuse, true);

        await ErrorAssert.ErrorAsync(client.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/{feature}{filter}")),
            HttpStatusCode.ServiceUnavailable, "unavailable", unavailable.Message);
    }

    [Test]
    public async Task A_second_database_meter_catalog_refusal_returns_503_releases_memory_and_recovers_all_observations()
    {
        const long Budget = 512L * 1024 * 1024;
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 21, 30, 0, TimeSpan.Zero));
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["region"] = "gru-1";
            settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture);
        }, hook.Install, clock);
        var createdFirst = await api.CreateManagedDatabaseAsync("first");
        var createdSecond = await api.CreateManagedDatabaseAsync(createdFirst.Organization, createdFirst.Project, "second");
        var databases = new[] { createdFirst, createdSecond }.OrderBy(database => database.Id, StringComparer.Ordinal).ToArray();
        var first = databases[0];
        var second = databases[1];
        await api.VerifyProjectBindingAsync(first.Organization, first.Project);
        using var client = ProjectClient(api, first.Project);
        for (var index = 0; index < databases.Length; index++)
        {
            var database = databases[index];
            await ManagementApi.CreateCollectionAsync(client, database, "people", token);
            for (var write = 0; write <= index; write++)
            {
                using var put = await client.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "local-" + write)),
                    new { name = new Faker().Name.FullName() }, token);
                put.EnsureSuccessStatusCode();
            }
        }

        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        await meter.FlushAsync(token);
        var route = ManagementApi.Uri($"/v1/projects/{first.Project}/analytics?range=1h&region=gru-1");
        var baseline = ReservedBytes(api);
        var before = await ReadAsync(client, route);
        await AssertMemoryReleasedAsync(api, baseline);
        var firstCounts = await meter.GetObservationsAsync(new(first.Organization, PlanTier.Hobby, first.Project, first.Id),
            new UsageWindow(UsageWindow.Minute, clock.GetUtcNow().ToUnixTimeSeconds() / 60 - 59, 60), token) switch
        {
            UsageCounts counts => counts,
            Unavailable error => throw new InvalidOperationException(error.Message),
            null => throw new InvalidOperationException("usage observations returned no result"),
        };
        var retainedReadings = (firstCounts.Lifetime.Meters?.Count ?? 0) + firstCounts.Series.Sum(point => point.Meters?.Count ?? 0);
        await Assert.That(retainedReadings).IsGreaterThan(0);
        var catalog = api.Server.Services.GetRequiredService<SystemCatalog>();
        byte[]? supportedVersion = null;
        var firstMeterReads = 0;
        var firstSeriesReads = 0;
        var secondMeterReads = 0;
        var meterLookup = 0;
        var refusedLookups = 0;
        long heldBeforeRefusal = 0;
        long catalogReadBytes = 0;
        long catalogBytesBeforeRefusal = 0;
        // Catalog result buffers stay admitted through delivery, alongside the analytics observations. They are admitted at
        // their stored size, which wraps a scalar in an object (StoredStates.SystemBody).
        var scalarWrapper = StoredStates.SystemBody("0").Length - 1;
        hook.AfterRead = document => Interlocked.Add(ref catalogReadBytes,
            (document.Json.Length + (document.Json.Span[0] == (byte)'{' ? 0 : scalarWrapper)) * 3L + 2048);
        hook.BeforeList = collection =>
        {
            if (collection == SystemCatalog.DatabaseState(first.Id) + UsageWindow.Minute.Collection)
            {
                Interlocked.Increment(ref firstSeriesReads);
            }
            // Lifetime totals list every node's usage documents.
            if (collection == SystemCatalog.DatabaseState(first.Id) + "usage")
            {
                Interlocked.Increment(ref firstMeterReads);
            }
            if (collection == SystemCatalog.DatabaseState(second.Id) + "usage")
            {
                Interlocked.Increment(ref secondMeterReads);
            }
        };
        hook.AfterGet = async (collection, id, _) =>
        {
            if (collection == $"projects/{first.Project}/databases" && id == second.Id && Volatile.Read(ref firstSeriesReads) == 1 && supportedVersion is null)
            {
                // The second tenant keeps its accepted snapshot; only its following meter read sees the new version.
                heldBeforeRefusal = ReservedBytes(api);
                catalogBytesBeforeRefusal = Interlocked.Read(ref catalogReadBytes);
                supportedVersion = await catalog.WriteUncheckedAsync(async transaction =>
                {
                    var previous = (await transaction.GetAsync("catalog", "version", token))!.Json.ToArray();
                    await transaction.PutAsync("catalog", "version", Encoding.UTF8.GetBytes("\"99\""), token);
                    return previous;
                }, token);
                Volatile.Write(ref meterLookup, 1);
            }
            if (collection == "catalog" && id == "version" && Interlocked.Exchange(ref meterLookup, 0) == 1)
            {
                Interlocked.Increment(ref refusedLookups);
            }
        };

        try
        {
            using var response = await client.GetAsync(route, token);
            await ErrorAssert.ErrorAsync(response, HttpStatusCode.ServiceUnavailable, "unavailable",
                "the management catalog has an old or unsupported format; discard the affected data");
            var refused = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            await Assert.That(refused.TryGetProperty("items", out _)).IsFalse();
            await Assert.That(firstMeterReads).IsEqualTo(1);
            await Assert.That(firstSeriesReads).IsEqualTo(1);
            await Assert.That(secondMeterReads).IsEqualTo(0);
            await Assert.That(refusedLookups).IsEqualTo(1);
            await Assert.That(catalogBytesBeforeRefusal).IsGreaterThan(0);
            await Assert.That(heldBeforeRefusal).IsEqualTo(baseline + 16 * 1024 + 16L * 1024 * 1024 + 2 * 60 * 2048L
                + retainedReadings * 2048L + catalogBytesBeforeRefusal);
            await AssertMemoryReleasedAsync(api, baseline);
            using var available = api.Server.Services.GetRequiredService<IMemoryBudget>().TryReserve(MemoryLane.Foreground, Budget - baseline, tenant: null);
            await Assert.That(available).IsNotNull();
            await Assert.That(available!.Bytes).IsEqualTo(Budget - baseline);
        }
        finally
        {
            hook.BeforeList = null;
            hook.AfterGet = null;
            hook.AfterRead = null;
            if (supportedVersion is not null)
            {
                await catalog.WriteUncheckedAsync(async transaction =>
                {
                    await transaction.PutAsync("catalog", "version", supportedVersion, token);
                    return true;
                }, token);
            }
        }

        var recovered = await ReadAsync(client, route);
        await Assert.That(recovered.GetRawText()).IsEqualTo(before.GetRawText());
        var items = recovered.GetProperty("items");
        await Assert.That(items.EnumerateArray().Select(item => item.GetProperty("databaseId").GetString()!)).IsEquivalentTo([first.Id, second.Id]);
        for (var index = 0; index < databases.Length; index++)
        {
            var item = items.EnumerateArray().Single(item => item.GetProperty("databaseId").GetString() == databases[index].Id);
            await Assert.That(item.GetProperty("region").GetString()).IsEqualTo("gru-1");
            var series = item.GetProperty("series");
            await Assert.That(series.GetArrayLength()).IsEqualTo(60);
            await Assert.That(series.EnumerateArray().Sum(point => point.GetProperty("operations").TryGetProperty("put", out var puts) ? puts.GetInt64() : 0))
                .IsEqualTo(index + 1L);
            await Assert.That(series.EnumerateArray().Sum(point => point.GetProperty("requests").TryGetProperty("put", out var puts) ? puts.GetInt64() : 0))
                .IsEqualTo(index + 1L);
        }

        await AssertMemoryReleasedAsync(api, baseline);
    }

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

        var route = ManagementApi.Uri($"/v1/projects/{database.Project}/analytics?range={range}&region=gru-1");
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
            var result = await ReadAsync(client, ManagementApi.Uri($"/v1/projects/{database.Project}/{feature}?range=7d"));
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
        var visible = await ReadAsync(restricted, ManagementApi.Uri($"/v1/projects/{first.Project}/{feature}"));
        await Assert.That(visible.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("databaseId").GetString()!)).IsEquivalentTo([first.Id]);
        using var another = await restricted.GetAsync(ManagementApi.Uri($"/v1/projects/{first.Project}/{feature}?database={second.Id}"));
        await Assert.That(another.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var route = await restricted.GetAsync(ManagementApi.Uri($"/v1/projects/{first.Project}/databases/second/{feature}"));
        await Assert.That(route.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var wrongProject = ProjectClient(api, second.Id);
        using var crossed = await wrongProject.GetAsync(ManagementApi.Uri($"/v1/projects/{first.Project}/{feature}"));
        await Assert.That(crossed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var noScope = ProjectClient(api, first.Project, scope: "documents:read");
        using var forbidden = await noScope.GetAsync(ManagementApi.Uri($"/v1/projects/{first.Project}/{feature}"));
        await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var key = await api.CreateClientAsync(first.Organization, "documents:read");
        using var unauthorized = await key.GetAsync(ManagementApi.Uri($"/v1/projects/{first.Project}/{feature}"));
        await Assert.That(unauthorized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var admin = await api.Server.Client.GetAsync(ManagementApi.Uri($"/v1/projects/{first.Project}/{feature}"));
        await Assert.That(admin.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Empty_buckets_invalid_ranges_and_OpenAPI_have_an_explicit_contract()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        var empty = await ReadAsync(client, ManagementApi.Uri($"/v1/projects/{database.Project}/analytics?range=1h"));
        await Assert.That(empty.GetProperty("items")[0].GetProperty("series").EnumerateArray()
            .All(point => point.GetProperty("latency").GetProperty("p50Ms").ValueKind == JsonValueKind.Null)).IsTrue();
        using var invalid = await client.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/analytics?range=2h"));
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var status = await ReadAsync(client, ManagementApi.Uri($"/v1/projects/{database.Project}/replication"));
        await Assert.That(status.GetProperty("items")[0].GetProperty("status").GetString()).IsEqualTo("stopped");
        await Assert.That(status.GetProperty("items")[0].GetProperty("lagMs").ValueKind).IsEqualTo(JsonValueKind.Null);
        var document = await ReadAsync(client, ManagementApi.Uri("/openapi/v1.json"));
        foreach (var feature in new[] { "analytics", "replication" })
        {
            await Assert.That(document.GetProperty("paths").TryGetProperty($"/v1/projects/{{project}}/{feature}", out _)).IsTrue();
            await Assert.That(document.GetProperty("paths").TryGetProperty($"/v1/projects/{{project}}/databases/{{database}}/{feature}", out _)).IsTrue();
            using var oldProject = await client.GetAsync(ManagementApi.Uri($"/projects/{database.Project}/{feature}"));
            using var oldDatabase = await client.GetAsync(ManagementApi.Uri($"/projects/{database.Project}/databases/{database.Name}/{feature}"));
            await Assert.That(oldProject.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(oldDatabase.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
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
                var route = ManagementApi.Uri($"/v1/projects/{database.Project}/analytics?range=1h&region=gru-1");
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
            var after = await ReadAsync(reader, ManagementApi.Uri($"/v1/projects/{database.Project}/analytics?range=1h&region=gru-1"));
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
        peers.State = new(true, 1, 0, [new(0, 2, [lagging], "sfo-1"), new(1, 0, [healthy], "sfo-1")]);
        clock.Advance(TimeSpan.FromSeconds(10));
        var route = ManagementApi.Uri($"/v1/projects/{database.Project}/replication?range=1h&region=gru-1");
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            var result = await client.GetFromJsonAsync<JsonElement>(route, token);
            return result.GetProperty("items")[0].GetProperty("status").GetString() == "diverged";
        });
        var observed = (await ReadAsync(client, route)).GetProperty("items")[0];
        await Assert.That(observed.GetProperty("lagMs").GetDouble()).IsEqualTo(14000);
        await Assert.That(observed.GetProperty("divergence").GetBoolean()).IsTrue();
        await Assert.That(observed.GetProperty("convergenceMs").GetDouble()).IsEqualTo(1500);
        peers.State = new(true, 1, 0, [new(0, 0, [healthy], "sfo-1"), new(1, 0, [healthy], "sfo-1")]);
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
        var route = ManagementApi.Uri($"/v1/projects/{database.Project}/replication?range=1h&region=gru-1");
        var synced = new ReplicationDatabaseStatus(0, database.Organization, database.Project, database.Id, Ulid.NewUlid(), 3, 3, 0, false, 0, null);

        peers.State = new(true, 1, 0, [new(0, 0, [synced], "sfo-1"), new(1, 0, [synced], "iad-1")]);
        await Assert.That((await ObserveAsync(TimeSpan.FromSeconds(10))).GetProperty("status").GetString()).IsEqualTo("healthy");

        // iad-1 goes silent while sfo-1 keeps reporting: the fresh peer must not hide it.
        peers.State = new(true, 1, 0, [new(0, 0, [synced], "sfo-1")]);
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
        peers.State = new(true, 1, 0, [new(0, 0, [synced], "sfo-1"), new(1, 0, [synced with { Diverged = true }], "iad-1")]);
        await Assert.That((await ObserveAsync(TimeSpan.FromSeconds(10))).GetProperty("status").GetString()).IsEqualTo("diverged");
        peers.State = new(true, 1, 0, [new(0, 0, [synced], "sfo-1")]);
        JsonElement stale = default;
        for (var tick = 0; tick < 4; tick++)
        {
            stale = await ObserveAsync(TimeSpan.FromSeconds(10));
        }

        await Assert.That(stale.GetProperty("status").GetString()).IsEqualTo("stopped");
        await Assert.That(stale.GetProperty("divergence").GetBoolean()).IsFalse();
        await Assert.That((await ObserveAsync(TimeSpan.FromMinutes(10))).GetProperty("status").GetString()).IsEqualTo("healthy");

        // Every peer silent, the last one having diverged: stopped, with neither current lag nor divergence.
        peers.State = new(true, 1, 0, [new(0, 0, [synced with { Diverged = true }], "sfo-1")]);
        await Assert.That((await ObserveAsync(TimeSpan.FromSeconds(10))).GetProperty("status").GetString()).IsEqualTo("diverged");
        peers.State = new(true, 1, 0, []);
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

        var items = (await ReadAsync(client, ManagementApi.Uri($"/v1/projects/{database.Project}/analytics?range=1h&region=gru-1"))).GetProperty("items");
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
        // Hobby admission would throttle creating the databases below; the plan does not change sampling.
        await api.SetPlanAsync(api.User, first.Organization, "enterprise");
        var databases = new List<TenantContext> { new(first.Organization, PlanTier.Hobby, first.Project, first.Id) };
        // Create the real catalog in lifecycle commits, without 139 separate admission/publication cycles.
        foreach (var batch in Enumerable.Range(1, 139).Chunk(16))
        {
            using var response = await api.Client.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
            {
                id = EntityIds.New().ToString(),
                ops = batch.Select(i => new
                {
                    op = "create_database",
                    organization = first.Organization,
                    project = first.Project,
                    name = "db" + i.ToString(CultureInfo.InvariantCulture),
                }).ToArray(),
            });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var committed = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            foreach (var result in committed.GetProperty("results").EnumerateArray())
            {
                var database = result.GetProperty("value");
                databases.Add(new TenantContext(first.Organization, PlanTier.Hobby, first.Project, database.GetProperty("id").GetString()!));
            }
        }

        await Assert.That(databases.Count).IsEqualTo(140);

        // Two minutes sample 2 × 128 databases; the second pass lists them in reverse.
        var flush = api.Server.Services.GetServices<IHostedService>().OfType<UsageFlushService>().Single();
        foreach (var reverse in new[] { false, true })
        {
            hook.ReverseDatabases = reverse;
            // These explicit passes own sampling; a wall-clock correction does not also launch hosted passes.
            clock.AdjustTime(clock.GetUtcNow() + TimeSpan.FromMinutes(1));
            await flush.SampleStorageAsync(CancellationToken.None);
            await flush.FlushAsync(CancellationToken.None);
        }

        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        var window = new UsageWindow(UsageWindow.Hour, clock.GetUtcNow().ToUnixTimeSeconds() / 3600, 1);
        var unsampled = 0;
        foreach (var database in databases)
        {
            var (lifetime, _) = await meter.GetObservationsAsync(database, window, CancellationToken.None) switch
            {
                UsageCounts counts => counts,
                Unavailable error => throw new InvalidOperationException(error.Message),
                null => throw new InvalidOperationException("usage observations unavailable"),
            };
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
            .GetObservationsAsync(database, new UsageWindow(UsageWindow.Minute, started, 2), CancellationToken.None) switch
        {
            UsageCounts counts => counts,
            Unavailable error => throw new InvalidOperationException(error.Message),
        };
        await Assert.That(series.Select(point => point.Meters?.GetValueOrDefault("storage_bytes|gru-1||sample")?.Count ?? 0))
            .IsEquivalentTo([1L, 1L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Explain_is_metered_and_near_limit_byte_counters_total_without_overflow()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        await ManagementApi.CreateCollectionAsync(client, database, "people");
        var analytics = ManagementApi.Uri($"/v1/projects/{database.Project}/analytics?range=1h&region=gru-1");
        async Task<long> PostsAsync()
        {
            return (await ReadAsync(client, analytics)).GetProperty("items")[0].GetProperty("series").EnumerateArray()
                .Sum(point => point.GetProperty("requests").TryGetProperty("post", out var posts) ? posts.GetInt64() : 0);
        }

        // EXPLAIN resolves its database itself; it is still one metered request of that database.
        var before = await PostsAsync();
        using (var explained = await client.PostAsJsonAsync(
            ManagementApi.Uri($"/v1/projects/{database.Project}/databases/{database.Name}/sql/explain"), new { sql = "SELECT * FROM people LIMIT 1" }))
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
        var replication = await ReadAsync(client, ManagementApi.Uri($"/v1/projects/{database.Project}/replication?range=1h&region=gru-1"));
        await Assert.That(replication.GetProperty("items")[0].GetProperty("bandwidth").EnumerateArray()
            .All(pair => pair.GetProperty("bytesSent").GetInt64() == long.MaxValue)).IsTrue();

        // Two saturated request readings fill one latency bin past the 64-bit range: percentiles still interpolate within it.
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
        meter.Add(key, new OpCounts(0, 0, meters));
        var saturated = (await ReadAsync(client, analytics)).GetProperty("items")[0].GetProperty("series").EnumerateArray()
            .Single(point => point.GetProperty("latency").GetProperty("p99Ms").ValueKind == JsonValueKind.Number
                && Math.Abs(point.GetProperty("latency").GetProperty("p50Ms").GetDouble() - 6) < 1e-9);
        await Assert.That(Math.Abs(saturated.GetProperty("latency").GetProperty("p99Ms").GetDouble() - 7.96)).IsLessThan(1e-9);
    }

    [Test]
    public async Task Latency_percentiles_interpolate_within_their_bin_and_the_open_last_bin_reports_its_lower_bound()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 21, 30, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1", time: clock);
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = ProjectClient(api, database.Project);
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        var now = clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        // Minutes before the server started, so no real request shares their bins.
        void Requests(long minute, params (int Bin, long Count)[] bins)
        {
            var latency = new long[UsageReading.LatencyBounds.Count];
            foreach (var (bin, count) in bins)
            {
                latency[bin] = count;
            }

            var dimension = new UsageDimension("requests", "gru-1", "", "get");
            var key = new UsageKey(new(database.Organization, database.Project, database.Id), minute / 60, TerraBase.Features.Management.Usage.UsageScope.Minute, (int)(minute % 60));
            meter.Add(key, new OpCounts(0, 0, new Dictionary<string, UsageReading>(StringComparer.Ordinal) { [dimension.Key] = new(dimension, bins.Sum(bin => bin.Count), 0, latency) }));
        }

        // A hundred requests of 16 to 32 ms (a 20 ms request's bin): ranks spread across the bin, not all at its 32 ms bound.
        Requests(now - 30, (7, 100));
        // Half under 0.25 ms, half beyond 32768 ms: the open last bin reports its lower bound.
        Requests(now - 20, (0, 50), (UsageReading.LatencyBounds.Count - 1, 50));
        var series = (await ReadAsync(client, ManagementApi.Uri($"/v1/projects/{database.Project}/analytics?range=1h&region=gru-1")))
            .GetProperty("items")[0].GetProperty("series").EnumerateArray().ToArray();
        JsonElement Latency(long minute)
        {
            return series.Single(point => point.GetProperty("start").GetDateTimeOffset() == DateTimeOffset.FromUnixTimeSeconds(minute * 60)).GetProperty("latency");
        }

        async Task Near(JsonElement latency, string percentile, double expected)
        {
            await Assert.That(Math.Abs(latency.GetProperty(percentile).GetDouble() - expected)).IsLessThan(1e-9).Because($"{percentile} should be {expected}");
        }

        var middle = Latency(now - 30);
        await Near(middle, "p50Ms", 24);
        await Near(middle, "p95Ms", 31.2);
        await Near(middle, "p99Ms", 31.84);
        var split = Latency(now - 20);
        await Near(split, "p50Ms", 0.25);
        await Near(split, "p95Ms", 32768);
        await Near(split, "p99Ms", 32768);
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
        var (unknown, _) = await meter.GetObservationsAsync(new(database.Organization, PlanTier.Hobby, database.Project, missing), window, CancellationToken.None) switch
        {
            UsageCounts counts => counts,
            Unavailable error => throw new InvalidOperationException(error.Message),
        };
        await Assert.That(unknown.Meters).IsNull();
        var (known, _) = await meter.GetObservationsAsync(new(database.Organization, PlanTier.Hobby, database.Project, database.Id), window, CancellationToken.None) switch
        {
            UsageCounts counts => counts,
            Unavailable error => throw new InvalidOperationException(error.Message),
        };
        await Assert.That(known.Meters?.ContainsKey("requests|gru-1||get") ?? false).IsTrue();
    }

    private sealed class ObservedPeers : IReplicationStatus
    {
        public ReplicationStatus State { get; set; } = new(true, 0, 0, []);
        public ReplicationStatus Snapshot() => State;
    }

    private static async Task AssertMemoryReleasedAsync(ManagementApi api, long baseline)
    {
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(ReservedBytes(api) == baseline), TimeSpan.FromSeconds(10));
        await Assert.That(ReservedBytes(api)).IsEqualTo(baseline);
    }

    private static long ReservedBytes(ManagementApi api)
    {
        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long? reserved = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            // Observe only this server's gauge while tests run in parallel.
            if (instrument.Name == "terrabase.memory.reserved" && ReferenceEquals(instrument.Meter, meter))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => reserved = value);
        listener.Start();
        var usage = api.Server.Services.GetRequiredService<UsageMeter>();
        long result = 0;
        usage.ObserveMemory(() =>
        {
            listener.RecordObservableInstruments();
            result = (reserved ?? throw new InvalidOperationException("the server's memory budget gauge was not observed")) - usage.ReservedMeasurementBytes;
        });
        return result;
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
