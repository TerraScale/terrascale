using TerraScale.Endpoints;

namespace TerraBase.Api.Endpoints;

[Endpoint]
public sealed class MigrationApplyEndpoint(TypedApi api) : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) => routes.MapPost("/v1/migrations/{collection}/apply", api.MigrationApplyAsync);
}
