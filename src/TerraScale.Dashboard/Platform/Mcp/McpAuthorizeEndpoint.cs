using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Platform.Mcp
{
    public sealed class McpAuthorizeEndpoint(IDbContextFactory<DashboardDbContext> storage) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/oauth/authorize");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(McpOAuth.AuthorizeAsync(HttpContext, storage));
    }
}
