namespace TerraScale.Platform.Features.Shapes.Endpoints;

public sealed class GetShapeEndpoint(ShapeReader reader) : Endpoint<ShapeRouteRequest, ShapeReply>
{
    public override void Configure()
    {
        Get(ShapeParameters.Route);
        Name("getShape");
        Summary("Syncs a table shape");
        Description("Requires sql:read. A shape is the rows of one ordinary rowid table that match where (every row when omitted), with the listed columns, read "
            + "through TerraBase as the caller so Rules apply. offset=-1 answers 307 to the first page of the newest snapshot; follow next_offset: snap.<S>.<P> pages, "
            + "then decimal commit boundaries, then live=true long-polls. A changed lineage, a stale handle, an offset below retention or above the head answers "
            + "409 must_refetch with the current handle in TB-Shape-Handle; restart at offset=-1.");
        Tags("Live");
        AllowAnonymous();
    }

    public override async ValueTask<ShapeReply> HandleAsync(ShapeRouteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = HttpContext.Request.Query;
        return ShapeParameters.Parse(query) switch
        {
            ShapeParameters parameters => ShapeQuery.From(request.Table, query) switch
            {
                ShapeQuery shape => await ServeAsync(request, parameters, shape, cancellationToken),
                PlatformInvalidRequest invalid => invalid,
            },
            PlatformInvalidRequest invalid => invalid,
        };
    }

    private async ValueTask<ShapeReply> ServeAsync(ShapeRouteRequest request, ShapeParameters parameters, ShapeQuery query, CancellationToken cancellationToken)
    {
        var outcome = await reader.ResolveAsync(HttpContext, request, query, cancellationToken);
        return outcome switch
        {
            ResolvedShape resolved => await AnswerAsync(request, parameters, resolved, cancellationToken),
            PlatformInvalidRequest invalid => invalid,
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ShapeReply> AnswerAsync(ShapeRouteRequest request, ShapeParameters parameters, ResolvedShape resolved, CancellationToken cancellationToken)
    {
        var shape = resolved.Shape;
        if (parameters.Handle is { } handle)
        {
            if (parameters.Offset is BootstrapOffset)
            {
                return ShapeReader.Refetch(shape, "bootstrap cannot carry a handle");
            }

            if (!string.Equals(handle, shape.Handle, StringComparison.Ordinal))
            {
                return ShapeReader.Refetch(shape, "shape lineage changed");
            }
        }

        return parameters.Offset switch
        {
            BootstrapOffset => new ShapeRedirect(reader.Location(resolved, $"snap.{shape.Head}.0"), shape.Handle),
            SnapshotOffset snapshot => Reply(await reader.PageAsync(resolved, snapshot, parameters.OffsetText, cancellationToken)),
            DecimalOffset at when parameters.Live =>
                await reader.LiveAsync(HttpContext, request, resolved, at.Value, parameters.OffsetText, parameters.Cursor ?? 0, cancellationToken),
            DecimalOffset at => Reply(await reader.ChangesAsync(resolved, at.Value, parameters.OffsetText, null, cancellationToken)),
        };
    }

    private static ShapeReply Reply(ReadShapePageOutcome read) => read switch
    {
        ShapeAnswer answer => answer,
        ShapeRefetch refetch => refetch,
        PlatformUnavailable unavailable => unavailable,
    };
}
