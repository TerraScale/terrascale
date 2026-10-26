using TerraScale.Dashboard.Platform.Observability;
using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Analytics;

public sealed record AnalyticsTotals(long Requests, long Errors, long Operations, long? StorageBytes, long ClientBytes,
    long ReplicationSentBytes, long ReplicationReceivedBytes, double? LatestP95Ms, IReadOnlyList<AnalyticsRegionRow> Regions);
public sealed record AnalyticsRegionRow(string Code, string Flag, long Requests, double? LatestP95Ms);
public sealed record AnalyticsChart(string Title, AnalyticsLinePlot Plot, AnalyticsSelection Selection);
public sealed record AnalyticsPage(ShellModel Shell, AnalyticsSelection Selection, AnalyticsTotals? Totals, IReadOnlyList<AnalyticsChart> Charts, string Error = "", string Empty = "No analytics measurements for these filters.")
{
    // OAuth2 client credentials: the client ID is the API key's prefix before its first underscore, the secret is the whole key.
    public string PrometheusConfig => $"""
        scrape_configs:
          - job_name: terrascale
            scheme: https
            metrics_path: /v1/projects/{Selection.Context.Project!.Id}/metrics
            static_configs:
              - targets: [api.terrascale.tech]
            oauth2:
              client_id: <API key up to its first _>
              client_secret: <API key>
              token_url: https://auth.terrascale.tech/terrascale/oauth/token
        """;
}

public sealed class AnalyticsService(IAnalyticsBackend backend)
{
    public async Task<AnalyticsPage> LoadAsync(ShellModel shell, AnalyticsSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            var batch = await backend.ReadAsync(selection, cancellationToken);
            if (batch.State == AnalyticsSourceState.Unavailable) { ServiceSloTelemetry.MarkBad(); return new(shell, selection, null, [], Empty: "Analytics measurements are unavailable."); }
            if (batch.State == AnalyticsSourceState.NotMetered)
                return new(shell, selection, null, [], Empty: "TerraBase has no analytics for this project or database yet. They start with its first database.");
            if (batch.Points.Count == 0) return new(shell, selection, null, []);
            selection = selection with { ObservedRegions = batch.Points.Select(point => point.Region).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
            var (totals, charts) = Build(selection, batch.Points);
            return new(shell, selection, totals, charts);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { ServiceSloTelemetry.MarkFailure(error); return new(shell, selection, null, [], "Could not load analytics measurements. Apply the filters to retry."); }
    }

    public static (AnalyticsTotals Totals, IReadOnlyList<AnalyticsChart> Charts) Build(AnalyticsSelection selection, IReadOnlyList<AnalyticsPoint> points)
    {
        var reported = points.Select(point => point.Start.ToUniversalTime()).Distinct().Order().ToArray();
        if (reported.Length == 0) throw new JsonException("No analytics buckets.");
        var bucket = selection.Range switch { "1h" => 60, "24h" => 3600, "7d" => 21600, "30d" => 86400, _ => throw new JsonException("Unknown analytics range.") };
        var starts = Enumerable.Range(0, checked((int)((reported[^1] - reported[0]).TotalSeconds / bucket) + 1))
            .Select(index => reported[0].AddSeconds((long)index * bucket)).ToArray();
        var indices = starts.Select((start, index) => (start, index)).ToDictionary(value => value.start, value => value.index);
        var requests = new SortedDictionary<string, long[]>(StringComparer.Ordinal);
        var operations = new SortedDictionary<string, long[]>(StringComparer.Ordinal);
        var latencies = new SortedDictionary<string, AnalyticsSeries>(StringComparer.Ordinal);
        var errors = new long[starts.Length]; var storage = new long[starts.Length]; var storageSamples = new int[starts.Length]; var storageMissing = Enumerable.Repeat(true, starts.Length).ToArray(); var sent = new long[starts.Length]; var received = new long[starts.Length]; var replicationSent = new long[starts.Length]; var replicationReceived = new long[starts.Length];
        long requestTotal = 0, operationTotal = 0, errorTotal = 0, clientTotal = 0, replicationSentTotal = 0, replicationReceivedTotal = 0;
        var p95ByBucket = new double?[starts.Length];
        var regions = new Dictionary<string, (long Requests, int Bucket, double? LatestP95Ms)>(StringComparer.Ordinal);
        var identities = points.Select(point => (point.DatabaseId, point.Region)).Distinct().Count();
        foreach (var point in points)
        {
            var index = indices[point.Start.ToUniversalTime()];
            AddCounters(requests, point.Requests, index, ref requestTotal);
            AddCounters(operations, point.Operations, index, ref operationTotal);
            errors[index] = checked(errors[index] + Nonnegative(point.Errors)); errorTotal = checked(errorTotal + point.Errors);
            if (point.StorageBytes is { } storageBytes)
            {
                storage[index] = checked(storage[index] + Nonnegative(storageBytes));
                storageSamples[index] = checked(storageSamples[index] + 1);
            }
            sent[index] = checked(sent[index] + Nonnegative(point.ClientSentBytes)); received[index] = checked(received[index] + Nonnegative(point.ClientReceivedBytes));
            replicationSent[index] = checked(replicationSent[index] + Nonnegative(point.ReplicationSentBytes));
            replicationReceived[index] = checked(replicationReceived[index] + Nonnegative(point.ReplicationReceivedBytes));
            clientTotal = checked(clientTotal + point.ClientSentBytes + point.ClientReceivedBytes);
            replicationSentTotal = checked(replicationSentTotal + point.ReplicationSentBytes);
            replicationReceivedTotal = checked(replicationReceivedTotal + point.ReplicationReceivedBytes);
            var group = selection.Context.Project!.Databases.GetValueOrDefault(point.DatabaseId)?.Name ?? point.DatabaseId;
            if (selection.Context.Project.Databases.Values.Count(database => database.Name == group) > 1) group += " (" + point.DatabaseId + ")";
            if (point.Region.Length > 0) group += " / " + point.Region;
            foreach (var percentile in new[] { (Name: "p50", Value: point.P50Ms), (Name: "p95", Value: point.P95Ms), (Name: "p99", Value: point.P99Ms) })
            {
                if (percentile.Value is not { } value) continue;
                if (!double.IsFinite(value) || value < 0) throw new JsonException("Invalid latency observation.");
                var name = (percentile.Name + " " + group).Trim();
                if (!latencies.TryGetValue(name, out var series)) latencies[name] = series = new(name, new long[starts.Length], Enumerable.Repeat(true, starts.Length).ToArray());
                series.Values[index] = checked((long)Math.Round(value * 1000, MidpointRounding.AwayFromZero));
                series.Missing![index] = false;
            }
            if (point.P95Ms is { } observed) p95ByBucket[index] = Math.Max(p95ByBucket[index] ?? 0, observed);
            var regionCode = point.Region.ToLowerInvariant();
            if (regionCode.Length > 0)
            {
                var row = regions.GetValueOrDefault(regionCode);
                var pointRequests = point.Requests.Values.Aggregate(0L, (sum, count) => checked(sum + Nonnegative(count)));
                // The current bucket is partial, so p95 comes from each region's latest bucket that observed requests.
                regions[regionCode] = point.P95Ms is null || index < row.Bucket ? row with { Requests = checked(row.Requests + pointRequests) }
                    : (checked(row.Requests + pointRequests), index, index == row.Bucket ? MaxLatency(row.LatestP95Ms, point.P95Ms) : point.P95Ms);
            }
        }
        var regionRows = regions.Select(item => new AnalyticsRegionRow(item.Key, Regions.Country(item.Key), item.Value.Requests, item.Value.LatestP95Ms))
            .OrderBy(row => Regions.Rank(row.Code)).ThenBy(row => row.Code, StringComparer.Ordinal).ToArray();
        // A partial set of database-region gauges is not a project storage total.
        for (var index = 0; index < starts.Length; index++) storageMissing[index] = storageSamples[index] != identities;
        var charts = new List<AnalyticsChart>();
        Add("Requests by type", requests.Select(item => new AnalyticsSeries(item.Key, item.Value)).ToArray());
        Add("Operations by type", operations.Select(item => new AnalyticsSeries(item.Key, item.Value)).ToArray());
        Add("Errors", new AnalyticsSeries("Errors", errors));
        Add("Approximate request latency percentiles (µs)", latencies.Values.ToArray());
        Add("Storage (bytes)", new AnalyticsSeries("Storage", storage, storageMissing));
        Add("Client bandwidth (bytes)", new AnalyticsSeries("Sent", sent), new AnalyticsSeries("Received", received));
        Add("Replication bandwidth (bytes)", new AnalyticsSeries("Sent", replicationSent), new AnalyticsSeries("Received", replicationReceived));
        var latestStorage = Array.FindLastIndex(storageMissing, missing => !missing);
        var latestLatency = Array.FindLastIndex(p95ByBucket, value => value is not null);
        return (new(requestTotal, errorTotal, operationTotal, latestStorage < 0 ? null : storage[latestStorage], clientTotal, replicationSentTotal, replicationReceivedTotal,
            latestLatency < 0 ? null : p95ByBucket[latestLatency], regionRows), charts);

        void Add(string title, params AnalyticsSeries[] series) => charts.Add(new(title, AnalyticsLinePlot.For(title + ", " + selection.Range, selection.Range, starts, series), selection));
        void AddCounters(SortedDictionary<string, long[]> target, IReadOnlyDictionary<string, long> values, int index, ref long total)
        {
            foreach (var counter in values)
            {
                if (!target.TryGetValue(counter.Key, out var row)) target[counter.Key] = row = new long[starts.Length];
                row[index] = checked(row[index] + Nonnegative(counter.Value)); total = checked(total + counter.Value);
            }
        }
    }
    private static long Nonnegative(long value) => value >= 0 ? value : throw new JsonException("Negative analytics measurement.");
    private static double? MaxLatency(double? left, double? right) => left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);
}
