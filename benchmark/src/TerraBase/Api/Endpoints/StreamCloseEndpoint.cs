using TerraScale.Endpoints;

namespace TerraBase.Api.Endpoints;

[Endpoint]
public sealed class StreamCloseEndpoint(TypedApi api) : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) => routes.MapDelete("/v1/streams/{id}", api.StreamCloseAsync);
}
