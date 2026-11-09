using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Platform.Mcp
{
    public sealed class McpUnsupportedMethodEndpoint : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Verbs("GET", "DELETE");
            Route("/mcp");
        }

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DashboardMcp.UnsupportedStatelessMethod(HttpContext));
    }
}
