namespace TerraScale.Platform.Features.Shapes.Endpoints;

public sealed class GetShapeManifestEndpoint(ShapeReader reader)
    : Endpoint<ShapeRouteRequest, Result<ShapeMirrorManifest, ShapeRefetch, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Get(ShapeParameters.Route + "/manifest");
        Name("getShapeManifest");
        Summary("Lists a shape's mirrored sealed objects");
        Description("Requires sql:read. Lists up to 8 immutable TBS1 objects that begin at offset, each with its next_offset, CDN URL, epoch, key_id, sha256 and size. "
            + "Fetch, verify size and sha256, decrypt with GET …/shape/keys and apply; otherwise read the offset from GET …/shape. The list is empty when the offset is "
            + "up to date or a snapshot page. A stale handle, an offset below retention or an unpublished offset answers 409 must_refetch.");
        Tags("Live");
        AllowAnonymous();
    }

    public override async ValueTask<Result<ShapeMirrorManifest, ShapeRefetch, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        ShapeRouteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = HttpContext.Request.Query;
        if (ShapeParameters.Only(query, "handle", "offset", "where", "columns") is { } unknown)
        {
            return unknown;
        }

        var handle = ShapeParameters.Parameter(query, "handle");
        if (handle is null || ShapeParameters.CheckHandle(handle) is not null)
        {
            return new Validation("handle must be h1- and 16 lowercase hex characters");
        }

        var offsetText = ShapeParameters.Parameter(query, "offset");
        var offset = offsetText is null ? null : ShapeOffset.Parse(offsetText);
        if (offset is null or BootstrapOffset)
        {
            return new Validation("offset must be snap.<S>.<P> or a decimal commit boundary; -1 is refused");
        }

        return ShapeQuery.From(request.Table, query) switch
        {
            ShapeQuery shape => await reader.ResolveAsync(HttpContext, request, shape, cancellationToken) switch
            {
                ResolvedShape { Shape.Handle: var current } resolved when current != handle => ShapeReader.Refetch(resolved.Shape, "shape lineage changed"),
                ResolvedShape resolved => offset switch
                {
                    DecimalOffset at => await reader.ManifestAsync(resolved.Shape, at.Value, cancellationToken) switch
                    {
                        ShapeMirrorManifest manifest => manifest,
                        ShapeRefetch refetch => refetch,
                        Unavailable unavailable => unavailable,
                    },
                    SnapshotOffset => new ShapeMirrorManifest(resolved.Shape.Handle, []),
                    BootstrapOffset => new Validation("offset -1 is refused"),
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
