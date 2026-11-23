namespace TerraScale.Platform.Features.Shapes.Endpoints;

public sealed class GetShapeKeyEndpoint(ShapeReader reader)
    : Endpoint<ShapeRouteRequest, Result<ShapeKeyResponse, ShapeRefetch, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Get(ShapeParameters.Route + "/keys");
        Name("getShapeKey");
        Summary("Gets a shape epoch key");
        Description("Requires sql:read. Releases the AES-256 epoch key for the current handle and a served epoch/key_id pair; the key opens only that handle and epoch. "
            + "Epochs last 10 minutes. Future epochs are refused; stale handles and unretained pairs answer 409 must_refetch. Responses are private, no-store.");
        Tags("Live");
        AllowAnonymous();
    }

    public override async ValueTask<Result<ShapeKeyResponse, ShapeRefetch, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        ShapeRouteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = HttpContext.Request.Query;
        if (ShapeParameters.Only(query, "handle", "epoch", "key_id", "where", "columns") is { } unknown)
        {
            return unknown;
        }

        var handle = ShapeParameters.Parameter(query, "handle");
        var epoch = ShapeParameters.Parameter(query, "epoch");
        var keyId = ShapeParameters.Parameter(query, "key_id");
        if (handle is null || ShapeParameters.CheckHandle(handle) is not null || epoch is null || keyId is null)
        {
            return new Validation("handle, epoch and key_id are required");
        }

        return ShapeQuery.From(request.Table, query) switch
        {
            ShapeQuery shape => await reader.ResolveAsync(HttpContext, request, shape, cancellationToken) switch
            {
                ResolvedShape { Shape.Handle: var current } resolved when current != handle => ShapeReader.Refetch(resolved.Shape, "shape lineage changed"),
                ResolvedShape resolved => reader.Key(resolved.Shape, epoch, keyId) switch
                {
                    ShapeKeyResponse key => key,
                    ShapeRefetch refetch => refetch,
                    Validation invalid => invalid,
                },
                Validation invalid => invalid,
                Unauthorized unauthorized => unauthorized,
                Forbidden forbidden => forbidden,
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
        };
    }
}
