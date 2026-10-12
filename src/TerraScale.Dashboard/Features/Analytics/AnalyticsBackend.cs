using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Analytics;

// This existing Go producer reporting contract is not exposed by the pinned
// official SDK. The shared endpoint resolver discovers the producer, and only
// that producer receives the user's JWT.
public sealed class AnalyticsBackend(IHttpClientFactory factory, IProjectAccessTokens projectTokens, IDatabaseBackend databases) : IAnalyticsBackend
{
    public async Task<AnalyticsBatch> ReadAsync(AnalyticsSelection selection, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        var client = factory.CreateClient("platform");
        var origin = await new TerraBaseDiscovery(client).ResolveAsync(token);
        var context = selection.Context;
        var project = context.Project!;
        var organization = context.Organization ?? throw new InvalidOperationException("Analytics requires an organization scope.");
        var regions = await databases.RegionsAsync(token);
        var regionCodes = regions.ToDictionary(region => region.Id, region => region.Code, StringComparer.Ordinal);
        var requestedRegion = selection.Region.Length == 0 ? "" :
            regions.SingleOrDefault(region => region.Code == selection.Region)?.Id ?? throw new HttpRequestException("The selected region is unavailable.");
        var resourceOrigin = new Uri(origin.GetLeftPart(UriPartial.Authority));
        var scopedToken = await projectTokens.GetAsync(context.Session, organization.Id, project.Id, resourceOrigin, token);
        var query = $"?range={selection.Range}" +
            (selection.DatabaseId.Length > 0 ? "&database=" + Uri.EscapeDataString(selection.DatabaseId) : "") +
            (requestedRegion.Length > 0 ? "&region=" + Uri.EscapeDataString(requestedRegion) : "");
        var path = $"/v1/projects/{Uri.EscapeDataString(project.Id)}/analytics{query}";
        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, path)))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scopedToken);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(AnalyticsSourceState.NotMetered, []);
            response.EnsureSuccessStatusCode();
            using var document = await PlatformJson.ReadAsync(response.Content, token);
            var root = document.RootElement;
            var bucketSeconds = selection.Range switch { "1h" => 60, "24h" => 3600, "7d" => 21600, "30d" => 86400, _ => throw new JsonException("Unknown analytics range.") };
            if (root.GetProperty("projectId").GetString() != project.Id || root.GetProperty("range").GetString() != selection.Range ||
                root.GetProperty("bucketSeconds").GetInt32() != bucketSeconds) throw new JsonException("Analytics returned a different project or reporting window.");
            var from = root.GetProperty("from").GetDateTimeOffset().ToUniversalTime();
            var to = root.GetProperty("to").GetDateTimeOffset().ToUniversalTime();
            var seconds = (to - from).TotalSeconds;
            if (from >= to || from.ToUnixTimeSeconds() % bucketSeconds != 0 || to.ToUnixTimeSeconds() % bucketSeconds != 0 ||
                seconds != (selection.Range switch { "1h" => 3600, "24h" => 86400, "7d" => 604800, "30d" => 2592000, _ => 0 }))
                throw new JsonException("Analytics returned an invalid UTC window.");
            var points = new List<AnalyticsPoint>();
            foreach (var database in root.GetProperty("items").EnumerateArray())
            {
                var databaseId = database.GetProperty("databaseId").GetString() ?? "";
                var region = database.GetProperty("region").GetString() ?? "";
                if (!project.Databases.TryGetValue(databaseId, out var record) || record.ProjectId != project.Id || !ValidRegionId(region) ||
                    selection.DatabaseId.Length > 0 && databaseId != selection.DatabaseId ||
                    requestedRegion.Length > 0 && region != requestedRegion)
                    throw new JsonException("Analytics returned measurements outside the selected scope.");
                var displayRegion = regionCodes.GetValueOrDefault(region, region);
                DateTimeOffset? previousStart = null;
                foreach (var series in database.GetProperty("series").EnumerateArray())
                {
                    var start = series.GetProperty("start").GetDateTimeOffset().ToUniversalTime();
                    if (start < from || start >= to || start.ToUnixTimeSeconds() % bucketSeconds != 0 || previousStart is { } previous && start <= previous)
                        throw new JsonException("Analytics returned an invalid or unordered UTC bucket.");
                    previousStart = start;
                    var latency = series.GetProperty("latency");
                    var clientBandwidth = series.GetProperty("clientBandwidth");
                    var replicationBandwidth = series.GetProperty("replicationBandwidth");
                    var requests = Counters(series.GetProperty("requests"));
                    var p50 = Observation(latency, "p50Ms"); var p95 = Observation(latency, "p95Ms"); var p99 = Observation(latency, "p99Ms");
                    if (p50 is { } median && p95 is { } percentile95 && median > percentile95 ||
                        p95 is { } high && p99 is { } percentile99 && high > percentile99 ||
                        (p50 is not null || p95 is not null || p99 is not null) && !requests.Values.Any(count => count > 0))
                        throw new JsonException("Analytics returned inconsistent latency observations.");
                    points.Add(new(databaseId, displayRegion, start, requests,
                        Counters(series.GetProperty("operations")), Count(series.GetProperty("errors")), p50, p95, p99,
                        OptionalCount(series.GetProperty("storageBytes")), Count(clientBandwidth.GetProperty("bytesSent")), Count(clientBandwidth.GetProperty("bytesReceived")),
                        Count(replicationBandwidth.GetProperty("bytesSent")), Count(replicationBandwidth.GetProperty("bytesReceived"))));
                }
            }
            return new(AnalyticsSourceState.Available, points);
        }
    }

    private static bool ValidRegionId(string region) => region.Length is >= 1 and <= 63 && region[0] is >= 'a' and <= 'z' && region[^1] != '-' &&
        region.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static long Count(JsonElement value) => value.GetInt64() is >= 0 and var count ? count : throw new JsonException("Negative measurement.");
    private static long? OptionalCount(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Count(value);
    private static IReadOnlyDictionary<string, long> Counters(JsonElement value) => value.EnumerateObject().ToDictionary(property => property.Name, property => Count(property.Value), StringComparer.Ordinal);
    private static double? Observation(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var number = value.GetDouble();
        return double.IsFinite(number) && number >= 0 && number <= long.MaxValue / 1000d ? number : throw new JsonException("Invalid latency observation.");
    }
}
