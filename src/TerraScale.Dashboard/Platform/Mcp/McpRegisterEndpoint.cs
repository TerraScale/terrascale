using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Platform.Mcp
{
    public sealed class McpRegisterEndpoint(DashboardDb storage, TimeProvider time) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/oauth/register");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(McpOAuth.RegisterAsync(HttpContext, storage, time));
    }
}
