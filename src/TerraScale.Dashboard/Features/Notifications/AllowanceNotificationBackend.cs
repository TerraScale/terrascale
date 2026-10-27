using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Notifications;

public sealed class AllowanceNotificationBackend(IHttpClientFactory http, IDashboardIdentity identity, IHostEnvironment environment) : IAllowanceNotificationBackend
{
    private readonly Uri origin = TerraBaseDashboardState.Origin(environment);

    public async Task<AllowanceUsagePage> ReadAsync(string? after, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "v1/usage/allowances" + (after is null ? "" : "?after=" + Uri.EscapeDataString(after))))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        using var response = await http.CreateClient(TerraBaseDashboardState.ClientName).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new IOException("Allowance producer is unavailable.");
        return await response.Content.ReadFromJsonAsync<AllowanceUsagePage>(cancellationToken) ?? throw new IOException("Allowance producer response is empty.");
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
        if (!response.IsSuccessStatusCode) throw new IOException($"Allowance email service answered {(int)response.StatusCode}.");
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
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
            {
                logger.LogWarning("Allowance notifications are unavailable ({ErrorType}); retrying next minute.", error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public static class AllowanceNotificationRegistration
{
    public static IServiceCollection AddAllowanceNotifications(this IServiceCollection services) => services
        .AddSingleton<IAllowanceNotificationBackend, AllowanceNotificationBackend>()
        .AddSingleton<AllowanceNotifications>().AddHostedService<AllowanceNotificationWorker>();
}
