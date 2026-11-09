using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Platform.Mcp
{
    public sealed class McpConsentEndpoint(IDashboardState state) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/oauth/authorize");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(McpOAuth.ConsentAsync(HttpContext, state));
    }
}
