using System.Collections.Concurrent;
using Dapper;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Clients;
using TerraScale.Dashboard.Platform.TsAuth;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed class AlertSource(IHttpClientFactory http, IDashboardIdentity identity, DashboardDb storage, OrganizationUsage usage, TerraBaseHttpClient terraBase,
        TimeProvider clock) : IAlertSource
    {
        private const string UnitRuleOrganizations = "SELECT DISTINCT organization_id FROM alert_rules WHERE metric = 'operation_units_percent' ORDER BY organization_id";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public async Task<AlertAnalytics> ReadAnalyticsAsync(string projectId, CancellationToken cancellationToken)
        {
            var answer = await terraBase.GetOperatorAnalyticsAsync(projectId, cancellationToken);
            if (answer is not JsonElement root || root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("bucketSeconds", out _))
            {
                return answer switch
                {
                    JsonElement or TerraBaseRefusal { Status: StatusCodes.Status404NotFound } => new(DateTimeOffset.UnixEpoch, []),
                    TerraBaseRefusal => throw new IOException("Project analytics are unavailable."),
                };
            }
            if (root.GetProperty("projectId").GetString() != projectId || root.GetProperty("bucketSeconds").GetInt32() != 60)
            {
                throw new JsonException("Analytics returned a different project or window.");
            }

            return new(root.GetProperty("to").GetDateTimeOffset(), [.. root.GetProperty("items").EnumerateArray().Select(item => new AlertSeries(
                item.GetProperty("databaseId").GetString() ?? "", item.GetProperty("databaseName").GetString() ?? "", item.GetProperty("region").GetString() ?? "",
                [.. item.GetProperty("series").EnumerateArray().Select(series => new AlertBucket(series.GetProperty("start").GetDateTimeOffset(),
                    series.GetProperty("requests").EnumerateObject().Sum(count => count.Value.GetInt64()), series.GetProperty("errors").GetInt64(),
                    series.GetProperty("latency").TryGetProperty("p95Ms", out var p95) && p95.ValueKind == JsonValueKind.Number ? p95.GetDouble() : null,
                    series.GetProperty("storageBytes") is { ValueKind: JsonValueKind.Number } storage ? storage.GetInt64() : null))]))]);
        }

        public async Task<IReadOnlyDictionary<string, long>> ReadOperationUnitsAsync(CancellationToken cancellationToken)
        {
            List<string> organizationIds;
            await using (var connection = await storage.OpenAsync(cancellationToken))
            {
                organizationIds = (await connection.QueryAsync<string>(UnitRuleOrganizations)).AsList();
            }

            var month = OrganizationUsage.MonthOf(clock.GetUtcNow());
            var units = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
            await Parallel.ForEachAsync(organizationIds, new ParallelOptions { MaxDegreeOfParallelism = OrganizationUsage.Concurrency, CancellationToken = cancellationToken },
                async (organizationId, token) =>
                {
                    var read = await usage.ReadAsync(organizationId, month, token);
                    if (read switch { MeasuredUsage measured => measured.OperationUnits, PartialUsage { OperationsComplete: true } partial => partial.OperationUnits, PartialUsage or UsageUnavailable => (long?)null } is { } used)
                    {
                        units[organizationId] = used;
                    }
                });
            return units;
        }

        public async Task<string> SendEmailAsync(AlertEmail email, CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(TsAuthOrigin.Value, "v1/terrascale/email/alert")) { Content = JsonContent.Create(email, options: Json) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                Uri.EscapeDataString(credentials.AuthClientId) + ":" + Uri.EscapeDataString(credentials.AuthClientSecret))));
            using var response = await http.CreateClient("platform").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (int)response.StatusCode is >= 500 or 429 ? "retryable" : "rejected";
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return body.TryGetProperty("status", out var value) && value.GetString() is "accepted" or "retryable" or "unknown" or "rejected" or "no_recipients"
                ? value.GetString()! : throw new IOException("Alert email response is invalid.");
        }
    }

    public sealed class AlertWorker(AlertRules rules, TimeProvider clock, ILogger<AlertWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
            do
            {
                try { await rules.RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is TerraBaseException or TimeoutException or HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
                {
                    logger.AlertRulesAreUnavailableRetryingNext(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }

    public static class AlertRegistration
    {
        public static IServiceCollection AddAlerts(this IServiceCollection services)
        {
            return services
            .AddSingleton<IAlertSource, AlertSource>().AddSingleton<AlertRules>().AddHostedService<AlertWorker>();
        }
    }
}
