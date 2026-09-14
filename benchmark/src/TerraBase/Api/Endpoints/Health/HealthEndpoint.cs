using TerraScale.Endpoints;

namespace TerraBase.Api.Endpoints;

[Endpoint]
public sealed class HealthEndpoint(IMetricsSource metrics) : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) => routes.MapGet("/health", () =>
    {
        var health = metrics.Snapshot().Health;
        return Results.Json(health, statusCode: health.HealthStatusCode);
    });
}
