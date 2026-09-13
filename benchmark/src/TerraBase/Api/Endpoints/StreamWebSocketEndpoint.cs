using TerraScale.Endpoints;

namespace TerraBase.Api.Endpoints;

[Endpoint]
public sealed class StreamWebSocketEndpoint(TypedApi api) : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) => routes.MapGet("/v1/streams/ws", api.StreamWebSocketAsync);
}
