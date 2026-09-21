namespace TerraScale.Auth.Observability;

using System.Security.Cryptography;
using System.Text;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Observability;
using TerraScale.DotNet;
using TerraScale.Endpoints;

internal sealed class ReadinessEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/readyz", HandleAsync);

    private static async Task<IResult> HandleAsync(TerraScaleClient client, AuthMetrics metrics, CancellationToken ct)
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
    }
}
