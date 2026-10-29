using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
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
        foreach (var feature in new[] { "analytics" })
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
    public async Task Project_JWT_scope_and_database_narrowing_protect_every_observation(string feature)
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        var first = await api.CreateManagedDatabaseAsync("first");
        var second = await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "second");
        using var restricted = ProjectClient(api, first.Project, database: first.Id);
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
    public async Task Observed_history_stays_readable_after_its_region_leaves_the_placement()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        var database = await api.CreateManagedDatabaseAsync();
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
