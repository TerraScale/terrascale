using TerraScale.TerraBase.Contracts;
using TerraScale.Dashboard.Platform.Observability;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.Features.Home
{
    public sealed record HomeAnalytics(long Requests, long Errors, double? WorstP95Ms, long? StorageBytes, long Operations, long ClientBytes);
    public sealed record HomeDirection(string FromRegion, string ToRegion, long WireBytes);
    public sealed record HomeReplication(int Healthy, int Lagging, int Diverged, int Stopped, double? WorstLagMs, long SentBytes, long ReceivedBytes, IReadOnlyList<HomeDirection> Directions)
    {
        public long WireBytes => Directions.Aggregate(0L, (sum, direction) => checked(sum + direction.WireBytes));
    }

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

                var totals = AnalyticsService.Summarize(selection, batch.Points);
                result.Analytics = new(totals.Requests, totals.Errors, totals.WorstP95Ms, totals.StorageBytes, totals.Operations, totals.ClientBytes);
                result.RequestPoints = [.. batch.Points.GroupBy(point => point.Start).OrderBy(group => group.Key).Select(group =>
                {
                    var counters = new SortedDictionary<string, long>(StringComparer.Ordinal);
                    foreach (var point in group) { foreach (var counter in point.Requests) { counters[counter.Key] = checked(counters.GetValueOrDefault(counter.Key) + counter.Value); } } return new HomeRequestPoint(group.Key, counters);
                })];
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
                if (report.Error.Length > 0)
                {
                    return;
                }

                if (report.Regions.Count == 0 && report.Pairs.Count == 0)
                {
                    result.Replication = null;
                    return;
                }

                static int CountStatus(IReadOnlyList<UsageReplica> regions, string status)
                {
                    return regions.Count(region => string.Equals(region.Status, status, StringComparison.OrdinalIgnoreCase));
                }

                var directions = report.Pairs.GroupBy(pair => (pair.FromRegion, pair.ToRegion))
                    .OrderBy(group => group.Key.FromRegion, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.ToRegion, StringComparer.Ordinal)
                    .Select(group => new HomeDirection(group.Key.FromRegion, group.Key.ToRegion,
                        group.Aggregate(0L, (total, pair) => checked(total + pair.WireBytes))))
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

        private static bool MeasurementFailure(Exception error, CancellationToken cancellationToken)
        {
            return error is AccountAccessException or TerraBaseException or HttpRequestException or IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
        }
    }
}
