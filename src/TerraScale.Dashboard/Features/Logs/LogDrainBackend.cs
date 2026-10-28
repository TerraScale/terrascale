using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Logs;

// The same document as the public database logs route, read over the dashboard's private TerraBase listener.
public sealed class LogDrainSource(IHttpClientFactory http, IHostEnvironment environment) : ILogDrainSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    private readonly Uri origin = TerraBaseDashboardState.Origin(environment);

    public async Task<DatabaseLogBatch> ReadAsync(string projectId, string databaseId, string after, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin,
            $"v1/usage/projects/{Uri.EscapeDataString(projectId)}/databases/{Uri.EscapeDataString(databaseId)}/logs?after={Uri.EscapeDataString(after)}"))
        { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        using var response = await http.CreateClient(TerraBaseDashboardState.ClientName).SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(databaseId, [], false, false, after);
        if (!response.IsSuccessStatusCode) throw new IOException("Database request logs are unavailable.");
        var batch = await response.Content.ReadFromJsonAsync<DatabaseLogBatch>(Json, cancellationToken) ?? throw new JsonException("Request logs are missing.");
        DatabaseLogBackend.Validate(batch, databaseId, "", cursor: true);
        return batch;
    }
}

public sealed class LogDrainWorker(LogDrains drains, TimeProvider clock, ILogger<LogDrainWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        do
        {
            try { await drains.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
            {
                logger.LogWarning("Log drains are unavailable ({ErrorType}); retrying shortly.", error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
