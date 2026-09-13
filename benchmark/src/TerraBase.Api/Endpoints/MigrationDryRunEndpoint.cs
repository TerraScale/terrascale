using TerraScale.Endpoints;

namespace TerraBase.Api.Endpoints;

[Endpoint]
public sealed class MigrationDryRunEndpoint(TypedApi api) : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) => routes.MapPost("/v1/migrations/{collection}/dry-run", api.MigrationDryRunAsync);
}
