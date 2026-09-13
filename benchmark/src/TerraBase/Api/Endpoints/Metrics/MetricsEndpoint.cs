using TerraScale.Endpoints;

namespace TerraBase.Api.Endpoints;

[Endpoint]
public sealed class MetricsEndpoint(TypedApi api, MetricsRegistry metrics) : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet(PrometheusMetrics.Path, () => Results.Text(metrics.MetricsBody(), PrometheusMetrics.ContentType));
        routes.MapGet(PrometheusMetrics.LivePath, () => Probe(metrics.Snapshot().IsLive));
        routes.MapGet(PrometheusMetrics.StartupPath, () => Probe(metrics.Snapshot().Started));
        routes.MapGet(PrometheusMetrics.ReadyPath, () => Probe(metrics.Snapshot().Ready));
        return routes.MapGet("/v1/metrics", api.Metrics);
    }

    private static IResult Probe(bool healthy) => Results.StatusCode(
        healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
}
