namespace TerraScale.Auth.Observability;

using System.Security.Cryptography;
using System.Text;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Observability;
using TerraScale.DotNet;
using TerraScale.Endpoints;

internal sealed class MetricsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/metrics", Handle);

    private static IResult Handle(HttpRequest request, AuthOptions options, AuthMetrics metrics)
    {
        var expected = options.MetricsKey;
        var presented = request.Headers["X-TerraScale-Metrics-Key"].ToString();
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presented),
                Encoding.UTF8.GetBytes(expected)))
            return Results.Unauthorized();

        return Results.Text(metrics.RenderPrometheus(), "text/plain; version=0.0.4; charset=utf-8");
    }
}
