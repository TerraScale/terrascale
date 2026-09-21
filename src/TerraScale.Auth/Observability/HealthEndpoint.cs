namespace TerraScale.Auth.Observability;

using System.Security.Cryptography;
using System.Text;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Observability;
using TerraScale.DotNet;
using TerraScale.Endpoints;

internal sealed class HealthEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/healthz", Handle);

    private static IResult Handle(AuthMetrics metrics)
    {
        metrics.SetLiveness(true);
        return Results.Ok(new { status = "ok" });
    }
}
