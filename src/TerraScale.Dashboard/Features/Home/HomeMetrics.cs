using TerraScale.Dashboard.Platform.Observability;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.Features.Home;

public sealed record HomeAnalytics(long Requests, long Errors, double? WorstP95Ms, long? StorageBytes, long Operations, long ClientBytes, long ReplicationBytes);
public sealed record HomeDirection(string FromRegion, string ToRegion, long SentBytes);
public sealed record HomeReplication(int Healthy, int Lagging, int Diverged, int Stopped, double? WorstLagMs, long SentBytes, long ReceivedBytes, IReadOnlyList<HomeDirection> Directions);

public sealed partial class TerraBaseHomeMeasurements
{
    private async Task AnalyticsAsync(AccountContext context, string range, HomeMeasurement result, CancellationToken cancellationToken)
    {
        if (context.Role.Length == 0) { result.AnalyticsError = "Organization membership is required for analytics."; return; }
        try
        {
            var selection = AnalyticsSelection.For(context, context.Project?.Id ?? "", "", "", range);
            var batch = await analyticsBackend.ReadAsync(selection, cancellationToken);
            if (batch.State == AnalyticsSourceState.Unavailable)
            {
                ServiceSloTelemetry.MarkBad();
                result.AnalyticsError = "Analytics measurements could not be loaded. Retry this page.";
                return;
            }

            result.AnalyticsError = "";
            if (batch.State == AnalyticsSourceState.NotMetered || batch.Points.Count == 0)
            {
                result.Analytics = null;
                result.RequestPoints = [];
                return;
            }

            long requests = 0, errors = 0, operations = 0, clients = 0, replication = 0;
            foreach (var point in batch.Points)
            {
                foreach (var count in point.Requests.Values) requests = checked(requests + count);
                foreach (var count in point.Operations.Values) operations = checked(operations + count);
                errors = checked(errors + point.Errors);
                clients = checked(clients + point.ClientSentBytes + point.ClientReceivedBytes);
                replication = checked(replication + point.ReplicationSentBytes);
            }

            var latest = batch.Points.Max(point => point.Start);
            long storage = 0;
            var storageComplete = true;
            double? p95 = null;
            foreach (var point in batch.Points.Where(point => point.Start == latest))
            {
                if (point.StorageBytes is { } measuredStorage) storage = checked(storage + measuredStorage);
                else storageComplete = false;
                if (point.P95Ms is { } measuredP95) p95 = Math.Max(p95 ?? 0, measuredP95);
            }

            result.Analytics = new(requests, errors, p95, storageComplete ? storage : null, operations, clients, replication);
            result.RequestPoints = batch.Points.GroupBy(point => point.Start).OrderBy(group => group.Key).Select(group =>
            {
                var counters = new SortedDictionary<string, long>(StringComparer.Ordinal);
                foreach (var point in group)
                    foreach (var counter in point.Requests)
                        counters[counter.Key] = checked(counters.GetValueOrDefault(counter.Key) + counter.Value);
                return new HomeRequestPoint(group.Key, counters);
            }).ToArray();
        }
        catch (Exception error) when (MeasurementFailure(error, cancellationToken))
        {
            ServiceSloTelemetry.MarkFailure(error);
            result.AnalyticsError = "Analytics measurements could not be loaded. Retry this page.";
        }
    }

    private async Task ReplicationAsync(AccountContext context, string range, HomeMeasurement result, CancellationToken cancellationToken)
    {
        if (context.Role.Length == 0) { result.ReplicationError = "Organization membership is required for replication metrics."; return; }
        try
        {
            var selection = UsageSelection.For(context, context.Project?.Id ?? "", "", range);
            var report = await replicationBackend.ReadAsync(selection, cancellationToken);
            result.ReplicationError = report.Error;
            result.ReplicationEmpty = report.Empty;
            if (report.Error.Length > 0) return;
            if (report.Regions.Count == 0 && report.Pairs.Count == 0)
            {
                result.Replication = null;
                return;
            }

            static int CountStatus(IReadOnlyList<UsageReplica> regions, string status) =>
                regions.Count(region => string.Equals(region.Status, status, StringComparison.OrdinalIgnoreCase));

            var directions = report.Pairs.GroupBy(pair => (pair.FromRegion, pair.ToRegion))
                .OrderBy(group => group.Key.FromRegion, StringComparer.Ordinal)
                .ThenBy(group => group.Key.ToRegion, StringComparer.Ordinal)
                .Select(group => new HomeDirection(group.Key.FromRegion, group.Key.ToRegion,
                    group.Aggregate(0L, (total, pair) => checked(total + pair.SentBytes))))
                .ToArray();
            result.Replication = new(
                CountStatus(report.Regions, "healthy"),
                CountStatus(report.Regions, "lagging"),
                CountStatus(report.Regions, "diverged"),
                CountStatus(report.Regions, "stopped"),
                report.WorstLagMs,
                report.SentBytes,
                report.ReceivedBytes,
                directions);
        }
        catch (Exception error) when (MeasurementFailure(error, cancellationToken))
        {
            ServiceSloTelemetry.MarkFailure(error);
            result.ReplicationError = "Replication measurements could not be loaded. Retry this page.";
        }
    }

    private static bool MeasurementFailure(Exception error, CancellationToken cancellationToken) =>
        error is AccountAccessException or HttpRequestException or IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException ||
        error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
}
