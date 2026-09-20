using System.Security.Cryptography;
using System.Text;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Observability;
using TerraScale.DotNet;

namespace TerraScale.Auth;

/// <summary>Health and private operator metrics for the auth feature slice.</summary>
internal static class ObservabilityEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapGet("/healthz", (AuthMetrics metrics) =>
        {
            metrics.SetLiveness(true);
            return Results.Ok(new { status = "ok" });
        });
        app.MapGet("/readyz", async (TerraScaleClient client, AuthMetrics metrics, CancellationToken ct) =>
        {
            try
            {
                var health = await client.Regions.HealthAsync(ct);
                metrics.SetReady(health.Healthy);
                metrics.SetDependencyReady("terrabase", health.Healthy);
                return health.Healthy
                    ? Results.Ok(new { status = "ready", region = health.Region })
                    : Results.Json(new { status = "not_ready", region = health.Region }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TerraScaleApiException or System.Text.Json.JsonException)
            {
                metrics.SetReady(false);
                metrics.SetDependencyReady("terrabase", false);
                return Results.Json(new { status = "not_ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        app.MapGet("/metrics", (HttpRequest request, AuthOptions options, AuthMetrics metrics) =>
        {
            var expected = options.MetricsKey;
            var presented = request.Headers["X-TerraScale-Metrics-Key"].ToString();
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(presented),
                    Encoding.UTF8.GetBytes(expected)))
                return Results.Unauthorized();

            return Results.Text(metrics.RenderPrometheus(), "text/plain; version=0.0.4; charset=utf-8");
        });
    }
}
