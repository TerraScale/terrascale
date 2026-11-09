using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Platform.Mcp
{
    public sealed class McpTokenEndpoint(IDashboardState state, AccessTokens tokens, TimeProvider time) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/oauth/token");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(McpOAuth.TokenAsync(HttpContext, state, tokens, time));
    }
}
