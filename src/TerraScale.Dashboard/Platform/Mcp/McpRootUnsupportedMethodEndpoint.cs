using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Platform.Mcp
{
    public sealed class McpRootUnsupportedMethodEndpoint : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Verbs("GET", "DELETE");
            Route("/");
            Metadata(new HostAttribute(McpOAuth.McpHost));
        }

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DashboardMcp.UnsupportedStatelessMethod(HttpContext));
    }
}
