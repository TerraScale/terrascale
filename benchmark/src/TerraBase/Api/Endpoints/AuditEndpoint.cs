using TerraScale.Endpoints;

namespace TerraBase.Api.Endpoints;

[Endpoint]
public sealed class AuditEndpoint(TypedApi api) : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) => routes.MapGet("/v1/audit", api.Audit);
}
