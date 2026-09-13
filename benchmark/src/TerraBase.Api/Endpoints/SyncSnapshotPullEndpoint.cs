using TerraScale.Endpoints;

namespace TerraBase.Api.Endpoints;

[Endpoint]
public sealed class SyncSnapshotPullEndpoint(TypedApi api) : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) => routes.MapPost("/v1/sync/sessions/{id}/snapshot", api.SyncSnapshotPullAsync);
}
