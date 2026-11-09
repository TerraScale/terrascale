using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Platform.Mcp
{
    public sealed class McpAuthorizationServerEndpoint : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/.well-known/oauth-authorization-server");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(McpOAuth.AuthorizationServer(HttpContext));
    }
}
