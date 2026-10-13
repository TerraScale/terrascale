using System.Text.Json;
using TerraScale.Dashboard.UI;

namespace TerraScale.Dashboard.Features.Analytics;

public sealed record AnalyticsTotals(long Requests, long Errors, long Operations, long? StorageBytes, long ClientBytes,
    long ReplicationSentBytes, long ReplicationReceivedBytes, double? LatestP95Ms);
public sealed record AnalyticsChart(string Title, AnalyticsLinePlot Plot);
public sealed record AnalyticsPage(ShellModel Shell, AnalyticsSelection Selection, AnalyticsTotals? Totals, IReadOnlyList<AnalyticsChart> Charts, string Error = "", string Empty = "No analytics measurements for these filters.");

public sealed class AnalyticsService(IAnalyticsBackend backend)
{
    public async Task<AnalyticsPage> LoadAsync(ShellModel shell, AnalyticsSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            var batch = await backend.ReadAsync(selection, cancellationToken);
            if (batch.State == AnalyticsSourceState.Unavailable) return new(shell, selection, null, [], Empty: "Analytics measurements are unavailable.");
            if (batch.Points.Count == 0 || batch.State == AnalyticsSourceState.NotMetered) return new(shell, selection, null, []);
            var (totals, charts) = Build(selection, batch.Points);
            return new(shell, selection, totals, charts);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { return new(shell, selection, null, [], "Could not load analytics measurements. Apply the filters to retry."); }
    }

    public static (AnalyticsTotals Totals, IReadOnlyList<AnalyticsChart> Charts) Build(AnalyticsSelection selection, IReadOnlyList<AnalyticsPoint> points)
    {
        var starts = points.Select(point => point.Start.ToUniversalTime()).Distinct().Order().ToArray();
        if (starts.Length == 0) throw new JsonException("No analytics buckets.");
        var indices = starts.Select((start, index) => (start, index)).ToDictionary(value => value.start, value => value.index);
        var requests = new SortedDictionary<string, long[]>(StringComparer.Ordinal);
        var operations = new SortedDictionary<string, long[]>(StringComparer.Ordinal);
        var latencies = new SortedDictionary<string, AnalyticsSeries>(StringComparer.Ordinal);
        var errors = new long[starts.Length]; var storage = new long[starts.Length]; var pointCounts = new int[starts.Length]; var storageSamples = new int[starts.Length]; var storageMissing = Enumerable.Repeat(true, starts.Length).ToArray(); var sent = new long[starts.Length]; var received = new long[starts.Length]; var replicationSent = new long[starts.Length]; var replicationReceived = new long[starts.Length];
        long requestTotal = 0, operationTotal = 0, errorTotal = 0, clientTotal = 0, replicationSentTotal = 0, replicationReceivedTotal = 0;
        double? p95 = null;
        foreach (var point in points)
        {
            var index = indices[point.Start.ToUniversalTime()];
            pointCounts[index] = checked(pointCounts[index] + 1);
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
            if (index == starts.Length - 1 && point.P95Ms is { } latest) p95 = Math.Max(p95 ?? 0, latest);
        }
        for (var index = 0; index < starts.Length; index++) storageMissing[index] = storageSamples[index] != pointCounts[index];
        var charts = new List<AnalyticsChart>();
        Add("Requests by type", requests.Select(item => new AnalyticsSeries(item.Key, item.Value)).ToArray());
        Add("Operations by type", operations.Select(item => new AnalyticsSeries(item.Key, item.Value)).ToArray());
        Add("Errors", new AnalyticsSeries("Errors", errors));
        Add("Approximate request latency percentiles (µs)", latencies.Values.ToArray());
        Add("Storage (bytes)", new AnalyticsSeries("Storage", storage, storageMissing));
        Add("Client bandwidth (bytes)", new AnalyticsSeries("Sent", sent), new AnalyticsSeries("Received", received));
        Add("Replication bandwidth (bytes)", new AnalyticsSeries("Sent", replicationSent), new AnalyticsSeries("Received", replicationReceived));
        return (new(requestTotal, errorTotal, operationTotal, storageMissing[^1] ? null : storage[^1], clientTotal, replicationSentTotal, replicationReceivedTotal, p95), charts);

        void Add(string title, params AnalyticsSeries[] series) => charts.Add(new(title, AnalyticsLinePlot.For(title + ", " + selection.Range, selection.Range, starts, series)));
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
}
