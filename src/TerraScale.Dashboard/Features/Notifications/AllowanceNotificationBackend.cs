using System.Globalization;
using TerraScale.Dashboard.Platform.Persistence;
using System.Text.Json.Serialization;
using TerraScale.Dashboard.Platform.Clients;
using TerraScale.Dashboard.Platform.TsAuth;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed class AllowanceNotificationBackend(IHttpClientFactory http, IDashboardIdentity identity, TerraBaseHttpClient terraBase, TimeProvider clock) : IAllowanceNotificationBackend
    {
        public async Task<AllowanceUsagePage?> ReadAsync(string? after, DateTimeOffset? month, CancellationToken cancellationToken)
        {
            var outcome = await terraBase.ListAllowancesAsync(cancellationToken);
            return outcome switch
            {
                JsonElement answer when answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("items", out _) =>
                    answer.Deserialize(AllowanceBackendJson.Default.AllowanceUsagePage) ?? throw new IOException("Allowance producer response is empty."),
                JsonElement answer when answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("plans", out var plans) && plans.ValueKind == JsonValueKind.Array =>
                    EmptyPage(clock.GetUtcNow(), month),
                JsonElement => throw new IOException("Allowance producer response is invalid."),
                TerraBaseRefusal => throw new IOException("Allowance producer is unavailable."),
            };
        }

        private static AllowanceUsagePage EmptyPage(DateTimeOffset now, DateTimeOffset? month) =>
            new(month ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero), now, [], null,
                AllowanceNotifications.OperationsContract, AllowanceNotifications.StorageContract);

        public async Task<string> SendAsync(AllowanceAlert alert, CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(TsAuthOrigin.Value, "v1/terrascale/email/allowance"))
            {
                Content = JsonContent.Create(new { alert.OrganizationId, alert.BillingMonth, alert.Metric, alert.Threshold, alert.Used, alert.Allowance })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                Uri.EscapeDataString(credentials.AuthClientId) + ":" + Uri.EscapeDataString(credentials.AuthClientSecret))));
            using var response = await http.CreateClient("platform").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new IOException($"Allowance email service answered {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return body.TryGetProperty("status", out var value) && value.GetString() is "accepted" or "retryable" or "unknown" or "rejected" or "no_recipients"
                ? value.GetString()! : throw new IOException("Allowance email response is invalid.");
        }
    }

    public sealed class AllowanceNotificationWorker(AllowanceNotifications notifications, TimeProvider clock, ILogger<AllowanceNotificationWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
            do
            {
                try { await notifications.RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is TerraBaseException or TimeoutException or HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
                {
                    logger.AllowanceNotificationsAreUnavailableRetryingNext(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }

    public static class AllowanceNotificationRegistration
    {
        public static IServiceCollection AddAllowanceNotifications(this IServiceCollection services)
        {
            return services
            .AddSingleton<IAllowanceNotificationBackend, AllowanceNotificationBackend>()
            .AddSingleton<AllowanceNotifications>().AddHostedService<AllowanceNotificationWorker>();
        }
    }
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(AllowanceUsagePage))]
    internal sealed partial class AllowanceBackendJson : JsonSerializerContext;

}
