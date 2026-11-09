using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Platform.Mcp
{
    public sealed class McpProtectedResourceEndpoint : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/.well-known/oauth-protected-resource/{**resource}");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(McpOAuth.ProtectedResource(HttpContext));
    }
}
