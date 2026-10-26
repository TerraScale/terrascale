using TerraScale.Dashboard.Platform.Observability;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.Features.Home;

public sealed class HomeMeasurement
{
    public IReadOnlyList<ActivityPoint> Activity { get; set; } = [];
    public bool ActivityMeasured { get; set; }
    public string ActivityLabel { get; set; } = "Operations";
    public IReadOnlyList<string> ActivityTicks { get; set; } = [];
    public IReadOnlyList<string> ActivityLabels { get; set; } = [];
    public DateTimeOffset ObservedAt { get; set; }
    public string ActivityNote { get; set; } = "Reads and writes are metered by TerraBase for browser sessions signed in through TerraScale.";
    public string AnalyticsError { get; set; } = "Analytics scope is unavailable.";
    public string AnalyticsEmpty { get; set; } = "No analytics measurements for this period.";
    public string ReplicationError { get; set; } = "Replication measurements could not be loaded.";
    public string ReplicationEmpty { get; set; } = "No replication measurements for this period.";
    public string RegionError { get; set; } = "";
    public bool RegionsPending { get; set; }
    public IReadOnlyList<HomeRequestPoint> RequestPoints { get; set; } = [];
    public HomeAnalytics? Analytics { get; set; }
    public HomeReplication? Replication { get; set; }
    public IReadOnlyList<HomeRegion> Regions { get; set; } = [];
    public int? ActiveCredentials { get; set; }
    public string CredentialsError { get; set; } = "";
    public long Reads => Activity.Sum(item => item.Reads);
    public long Writes => Activity.Sum(item => item.Writes);
}
public sealed record HomeRegion(string Code, string Status, string Latency, int Databases);
public interface IHomeMeasurements
{
    Task<HomeMeasurement> ReadAsync(AccountContext context, string range, CancellationToken cancellationToken);
    Task<HomeMeasurement> ReadPageAsync(AccountContext context, string range, CancellationToken cancellationToken) => ReadAsync(context, range, cancellationToken);
    Task<HomeMeasurement> ReadRegionsAsync(AccountContext context, CancellationToken cancellationToken) => ReadAsync(context, "24h", cancellationToken);
}

// These producer-owned read contracts are present in ts-go but not yet in the pinned ts-dotnet candidate.
// Keep the adapter at this boundary until the official SDK exposes usage/metrics/status clients.
public sealed partial class TerraBaseHomeMeasurements(IHttpClientFactory http, TimeProvider time,
    TerraScale.Dashboard.Features.ApiKeys.ApiKeyService keys, IAnalyticsBackend analyticsBackend, IUsageReplication replicationBackend,
    TerraScale.Dashboard.Features.Auth.IProjectAccessTokens tokens) : IHomeMeasurements
{
    public async Task<HomeMeasurement> ReadAsync(AccountContext context, string range, CancellationToken cancellationToken)
    {
        var result = new HomeMeasurement { ObservedAt = time.GetUtcNow() };
        await Task.WhenAll(ActivityAsync(context, range, result, cancellationToken), RegionsAsync(context, result, cancellationToken),
            AnalyticsAsync(context, range, result, cancellationToken), ReplicationAsync(context, range, result, cancellationToken), CredentialsAsync(context, result, cancellationToken));
        return result;
    }

    public async Task<HomeMeasurement> ReadPageAsync(AccountContext context, string range, CancellationToken cancellationToken)
    {
        var result = new HomeMeasurement { ObservedAt = time.GetUtcNow(), RegionsPending = true };
        await Task.WhenAll(ActivityAsync(context, range, result, cancellationToken), AnalyticsAsync(context, range, result, cancellationToken),
            ReplicationAsync(context, range, result, cancellationToken), CredentialsAsync(context, result, cancellationToken));
        return result;
    }

    public async Task<HomeMeasurement> ReadRegionsAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var result = new HomeMeasurement { ObservedAt = time.GetUtcNow() };
        await RegionsAsync(context, result, cancellationToken);
        return result;
    }

    private async Task CredentialsAsync(AccountContext context, HomeMeasurement result, CancellationToken cancellationToken)
    {
        if (context.Role.Length == 0) { result.CredentialsError = "Organization membership is required to view application credential counts."; return; }
        try { result.ActiveCredentials = (await keys.ListAsync(context, cancellationToken)).Count(key => key.IsActive); }
        catch (Exception error) when (error is AccountAccessException || MeasurementFailure(error, cancellationToken))
        { ServiceSloTelemetry.MarkFailure(error); result.CredentialsError = "Application credentials could not be loaded. Open API keys to retry."; }
    }

    private async Task ActivityAsync(AccountContext context, string range, HomeMeasurement result, CancellationToken cancellationToken)
    {
        if (context.Organization is null || context.Project is null) return;
        // TerraBase registers a project with its first database, so an empty project has no activity (and is 404 there).
        if (context.Project.Databases.Count == 0)
        {
            result.ActivityMeasured = true;
            result.ActivityNote = "No activity yet. Create a database to start metering reads and writes.";
            return;
        }
        try
        {
            var client = http.CreateClient("platform");
            var origin = await new TerraBaseDiscovery().ResolveAsync(cancellationToken);
            var now = time.GetUtcNow().UtcDateTime;
            var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
            var start = range switch { "1h" => hour, "24h" => hour.AddHours(-23), "30d" => now.Date.AddDays(-29), _ => hour.AddHours(-167) };
            // The project usage route with a project token, as the Usage page reads it (UsageBackend).
            var token = await tokens.GetAsync(context.Session, context.Organization.Id, context.Project.Id, ["databases:read"], cancellationToken);
            var path = $"/v1/projects/{Uri.EscapeDataString(context.Project.Id)}/usage?granularity={(range == "30d" ? "day" : "hour")}&from={Uri.EscapeDataString(start.ToString("O"))}";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                result.ActivityNote = "Activity for this project isn't available yet. It appears once TerraBase reports its reads and writes.";
                return;
            }
            response.EnsureSuccessStatusCode();
            using var data = await PlatformJson.ReadAsync(response.Content, cancellationToken);
            if (!data.RootElement.TryGetProperty("period", out var period) || period.ValueKind != JsonValueKind.Object) return;
            if (data.RootElement.GetProperty("projectId").GetString() != context.Project.Id ||
                data.RootElement.TryGetProperty("databaseId", out var databaseId) && !string.IsNullOrEmpty(databaseId.GetString()))
                throw new JsonException("Activity returned a different resource.");
            var from = period.GetProperty("from").GetDateTimeOffset();
            var to = period.GetProperty("to").GetDateTimeOffset();
            var granularity = range == "30d" ? "day" : "hour";
            // TerraBase rounds the period's end up to the end of the bucket holding now.
            if (from != new DateTimeOffset(start) || to <= from || to > time.GetUtcNow() + (range == "30d" ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1)) ||
                period.GetProperty("granularity").GetString() != granularity)
                throw new JsonException("Activity returned an invalid period.");
            var points = new List<ActivityPoint>();
            long total = 0;
            foreach (var item in data.RootElement.GetProperty("series").EnumerateArray())
            {
                var point = new ActivityPoint(item.GetProperty("start").GetDateTimeOffset(), item.GetProperty("reads").GetInt64(), item.GetProperty("writes").GetInt64());
                if (point.Reads < 0 || point.Writes < 0 || point.Start < from || point.Start >= to ||
                    point.Start.Offset != TimeSpan.Zero || point.Start.TimeOfDay.Ticks %
                    (granularity == "day" ? TimeSpan.TicksPerDay : TimeSpan.TicksPerHour) != 0)
                    throw new JsonException("Invalid metered activity.");
                _ = checked(point.Reads + point.Writes);
                total = checked(total + point.Reads + point.Writes);
                points.Add(point);
            }
            if (points.Select(point => point.Start).Distinct().Count() != points.Count)
                throw new JsonException("Activity repeated a UTC bucket.");
            result.Activity = points.OrderBy(point => point.Start).ToArray();
            result.ActivityMeasured = true;
            result.ActivityNote = "Reads and writes metered by TerraBase.";
        }
        catch (Exception error) when (error is AccountAccessException or HttpRequestException or IOException or JsonException or OverflowException or FormatException or KeyNotFoundException or InvalidOperationException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            ServiceSloTelemetry.MarkFailure(error);
            result.ActivityNote = "Metered reads and writes could not be loaded. Try again shortly.";
        }
    }

    private async Task RegionsAsync(AccountContext context, HomeMeasurement result, CancellationToken cancellationToken)
    {
        var counts = new SortedDictionary<string, int>();
        foreach (var project in context.Organization?.Projects.Values.AsEnumerable() ?? Enumerable.Empty<ProjectRecord>())
        {
            if (context.Role.Length == 0 && project.Id != context.Project?.Id) continue;
            if (project.OrganizationId != context.Organization!.Id) continue;
            foreach (var database in project.Databases.Values)
                foreach (var code in database.Regions.Append(database.Region).Where(value => value.Length > 0).Distinct()) counts[code] = counts.GetValueOrDefault(code) + 1;
        }
        var measurements = new Dictionary<string, (string Status, string Latency)>();
        if (counts.Count > 0)
        {
            try
            {
                using var response = await http.CreateClient("platform").GetAsync("https://status.terrascale.tech/api/latency", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                using var data = await PlatformJson.ReadAsync(response.Content, cancellationToken);
                foreach (var item in data.RootElement.GetProperty("regions").EnumerateArray())
                {
                    var code = item.GetProperty("code").GetString()!.ToUpperInvariant();
                    var latency = "Not reported";
                    if (item.TryGetProperty("consensus", out var consensus) && consensus.ValueKind == JsonValueKind.Object && consensus.GetProperty("samples").GetInt32() > 0 &&
                        consensus.TryGetProperty("median_ms", out var median) && median.ValueKind == JsonValueKind.Number)
                    {
                        var value = median.GetDouble();
                        if (!double.IsFinite(value) || value < 0) throw new JsonException("Invalid region latency.");
                        latency = value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " ms";
                    }
                    measurements[code] = (item.GetProperty("status").GetString() ?? "Not reported", latency);
                }
            }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { ServiceSloTelemetry.MarkFailure(error); result.RegionError = "Could not load region health and latency. Retry this page."; }
        }
        result.Regions = counts.Select(pair => new HomeRegion(pair.Key, measurements.GetValueOrDefault(pair.Key).Status ?? "Not reported", measurements.GetValueOrDefault(pair.Key).Latency ?? "Not reported", pair.Value)).ToArray();
    }
}
