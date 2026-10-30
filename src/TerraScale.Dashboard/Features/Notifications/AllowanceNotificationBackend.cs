using TerraScale.Dashboard.Platform.Persistence;
using System.Text.Json.Serialization;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Usage;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed class AllowanceNotificationBackend(IHttpClientFactory http, IDashboardIdentity identity, TerraBaseRpcClient rpc) : IAllowanceNotificationBackend
    {

        public async Task<AllowanceUsagePage> ReadAsync(string? after, CancellationToken cancellationToken)
        {
            try
            {
                var answer = await rpc.Platform<IUsageService>().WithCancellationToken(cancellationToken).ListAllowancesAsync(after);
                return answer.Deserialize(AllowanceBackendJson.Default.AllowanceUsagePage) ?? throw new IOException("Allowance producer response is empty.");
            }
            catch (TerraBaseException) { throw new IOException("Allowance producer is unavailable."); }
        }

        public async Task<string> SendAsync(AllowanceAlert alert, CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.terrascale.tech/v1/terrascale/email/allowance")
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
                catch (Exception error) when (error is TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
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
