using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Platform.Mcp
{
    public sealed class McpRegisterEndpoint(IDashboardState state, TimeProvider time) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/oauth/register");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(McpOAuth.RegisterAsync(HttpContext, state, time));
    }
}
