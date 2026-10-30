using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Usage;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed class AlertSource(IHttpClientFactory http, IDashboardIdentity identity, IAllowanceNotificationBackend allowances, TerraBaseRpcClient rpc) : IAlertSource
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        // The same document as the public /v1/projects/{project}/analytics, read over the dashboard's private TerraBase listener.
        public async Task<AlertAnalytics> ReadAnalyticsAsync(string projectId, CancellationToken cancellationToken)
        {
            JsonElement root;
            try { root = await rpc.Platform<IUsageService>().WithCancellationToken(cancellationToken).GetOperatorAnalyticsAsync(projectId); }
            catch (TerraBaseNotFoundException) { return new(DateTimeOffset.UnixEpoch, []); }
            catch (TerraBaseException) { throw new IOException("Project analytics are unavailable."); }
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

        // The allowance feed is the authoritative month-to-date operation units per organization.
        public async Task<IReadOnlyDictionary<string, long>> ReadOperationUnitsAsync(CancellationToken cancellationToken)
        {
            var units = new Dictionary<string, long>(StringComparer.Ordinal);
            string? after = null;
            do
            {
                var page = await allowances.ReadAsync(after, cancellationToken);
                if (page.OperationsContract != AllowanceNotifications.OperationsContract)
                {
                    throw new IOException("Operation units are not authoritative.");
                }

                foreach (var item in page.Items)
                {
                    if (item.Operations is { } used)
                    {
                        units[item.OrganizationId] = used;
                    }
                }

                after = page.Next is { } next && string.CompareOrdinal(next, after) > 0 ? next : null;
            } while (after is not null);
            return units;
        }

        public async Task<string> SendEmailAsync(AlertEmail email, CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.terrascale.tech/v1/terrascale/email/alert") { Content = JsonContent.Create(email, options: Json) };
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
                catch (Exception error) when (error is TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
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
