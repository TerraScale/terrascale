using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class MetricsExportTests
{
    [Test]
    public async Task Project_metrics_export_complete_minutes_as_prometheus_text_for_the_token_project_only()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 21, 30, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1", time: clock);
        var database = await ManagedDatabaseWithRequestsAsync(api);
        using var client = UsageAnalyticsTests.ProjectClient(api, database.Project);
        var route = ManagementApi.Uri($"/v1/projects/{database.Project}/metrics");
        var labels = $"{{database=\"{database.Name}\",database_id=\"{database.Id}\",region=\"gru-1\"}}";

        using (var partial = await client.GetAsync(route))
        {
            var text = await partial.Content.ReadAsStringAsync();
            await Assert.That(text).Contains($"terrascale_database_requests_per_second{labels} 0\n").Because("the current minute is partial");
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        using var response = await client.GetAsync(route);
        var body = await response.Content.ReadAsStringAsync();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(body);
        await Assert.That(response.Content.Headers.ContentType?.ToString()).IsEqualTo("text/plain; version=0.0.4; charset=utf-8");
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(body).Contains("# TYPE terrascale_database_requests_per_second gauge\n");
        var requests = body.Split('\n').Single(line => line.StartsWith("terrascale_database_requests_per_second" + labels, StringComparison.Ordinal));
        await Assert.That(double.Parse(requests.Split(' ')[^1], CultureInfo.InvariantCulture)).IsGreaterThan(0);
        await Assert.That(body).Contains($"terrascale_database_operations_per_second{labels[..^1]},kind=\"put\"}} ");
        await Assert.That(body).Contains($"terrascale_database_latency_milliseconds{labels[..^1]},quantile=\"0.95\"}} ");

        using var wrongProject = UsageAnalyticsTests.ProjectClient(api, EntityIds.New().ToString());
        using var crossed = await wrongProject.GetAsync(route);
        await Assert.That(crossed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var anonymous = api.WithToken(null);
        using var unauthorized = await anonymous.GetAsync(route);
        await Assert.That(unauthorized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Internal_dashboard_routes_read_project_analytics_and_database_logs_without_a_project_token()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        var database = await ManagedDatabaseWithRequestsAsync(api);

        var analytics = await ReadAsync(api.Server.Client, $"/v1/usage/projects/{database.Project}/analytics?range=1h&database={database.Id}");
        await Assert.That(analytics.GetProperty("projectId").GetString()).IsEqualTo(database.Project);
        await Assert.That(analytics.GetProperty("bucketSeconds").GetInt32()).IsEqualTo(60);
        var item = analytics.GetProperty("items").EnumerateArray().Single(entry => entry.GetProperty("region").GetString() == "gru-1");
        await Assert.That(item.GetProperty("databaseId").GetString()).IsEqualTo(database.Id);
        await Assert.That(item.GetProperty("series").EnumerateArray().Any(point => point.GetProperty("operations").TryGetProperty("put", out _))).IsTrue();

        var logs = await ReadAsync(api.Server.Client, $"/v1/usage/projects/{database.Project}/databases/{database.Id}/logs");
        await Assert.That(logs.GetProperty("databaseId").GetString()).IsEqualTo(database.Id);
        await Assert.That(logs.GetProperty("items").EnumerateArray().Any(row => row.GetProperty("method").GetString() == "PUT")).IsTrue();

        foreach (var path in new[]
        {
            $"/v1/usage/projects/{EntityIds.New()}/analytics",
            $"/v1/usage/projects/{EntityIds.New()}/databases/{database.Id}/logs",
            $"/v1/usage/projects/{database.Project}/databases/{EntityIds.New()}/logs",
        })
        {
            using var missing = await api.Server.Client.GetAsync(ManagementApi.Uri(path));
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Because(path);
        }

        using var client = UsageAnalyticsTests.ProjectClient(api, database.Project);
        using var outside = await client.GetAsync(ManagementApi.Uri($"/v1/usage/projects/{database.Project}/analytics"));
        await Assert.That(outside.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Because("only the internal listener serves it");
    }

    private static async Task<ManagedDatabaseRef> ManagedDatabaseWithRequestsAsync(ManagementApi api)
    {
        var database = await api.CreateManagedDatabaseAsync();
        using var client = UsageAnalyticsTests.ProjectClient(api, database.Project);
        await ManagementApi.CreateCollectionAsync(client, database, "people");
        var document = ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "one"));
        using var put = await client.PutAsJsonAsync(document, new { name = new Faker().Name.FullName() });
        put.EnsureSuccessStatusCode();
        using var get = await client.GetAsync(document);
        get.EnsureSuccessStatusCode();
        return database;
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(ManagementApi.Uri(path));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync());
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
