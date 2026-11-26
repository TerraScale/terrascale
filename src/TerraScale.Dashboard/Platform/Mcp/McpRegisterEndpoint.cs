using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Platform.Mcp
{
    public sealed class McpRegisterEndpoint(DashboardDb storage, TimeProvider time, EntryRateLimits limits) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/oauth/register").AllowAnonymous();

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            limits.AdmitRegistration(HttpContext) ? new(McpOAuth.RegisterAsync(HttpContext, storage, time)) : new(EntryRateLimits.Refuse(HttpContext));
    }
}
