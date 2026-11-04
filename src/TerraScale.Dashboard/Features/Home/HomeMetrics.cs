using TerraScale.TerraBase.Contracts;
using TerraScale.Dashboard.Platform.Observability;
using TerraScale.Dashboard.Features.Analytics;

namespace TerraScale.Dashboard.Features.Home
{
    public sealed record HomeAnalytics(long Requests, long Errors, double? WorstP95Ms, long? StorageBytes, long Operations, long ClientBytes);

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

        private static bool MeasurementFailure(Exception error, CancellationToken cancellationToken)
        {
            return error is AccountAccessException or TerraBaseException or HttpRequestException or IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
        }
    }
}
