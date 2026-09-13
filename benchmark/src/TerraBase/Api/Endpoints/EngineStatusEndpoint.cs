using TerraScale.Endpoints;

namespace TerraBase.Api.Endpoints;

[Endpoint]
public sealed class EngineStatusEndpoint(TypedApi api) : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) => routes.MapGet("/v1/engine/status", api.EngineStatusAsync);
}
