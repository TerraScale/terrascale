using Microsoft.OpenApi;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Documents;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;
using static TerraBase.Features.Shapes.ShapeRoute;

namespace TerraBase.Features.Shapes;

/// <summary><c>GET …/tables/{table}/shape/manifest</c>: the mirrored objects of a shape's immutable offsets.</summary>
internal sealed class GetShapeManifestEndpoint(
    ITenantDirectory tenants, IShapeLineages lineages, IShapeSnapshots snapshots, IShapeChunks chunks, IShapeMirror mirror)
    : EndpointWithoutRequest<IResult>
{
    private static readonly Validation BadOffset = new("offset must be snap.<S>.<P> or a decimal commit boundary");

    /// <inheritdoc />
    public override void Configure()
    {
        Get(Shape + "/manifest");
        Name("getShapeManifest");
        Summary("Lists a shape's mirrored sealed objects");
        Description("""
            Requires documents:read. Lists up to 8 URLs of the immutable TBS1 objects that begin at offset, each followed by its next_offset; a URL is a stable Cloudflare CDN path for an R2 bucket, otherwise a presigned bucket URL. Fetch an object, check its size and sha256, decrypt it with the key from GET …/shape/keys, and apply it only if all of that passes. Otherwise read that offset from the origin with GET …/shape. The list is empty when the offset is not mirrored yet, or is a tail body that is up to date. A stale handle, an offset below retention or an unpublished offset answers 409 must_refetch with the current handle in TB-Shape-Handle and the body; restart at offset=-1 on GET …/shape. Responses are private, no-store; read the manifest again after expires_at, the start of the next epoch, when a presigned URL expires.
            """);
        Tags("Live");
        Produces<ShapeMirrorManifest>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ShapeErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(
            OriginNoStore.Instance,
            Query("handle", "The current handle: h1- and 16 lowercase hex characters.", required: true),
            Query("offset", "snap.<S>.<P> or a decimal commit boundary; -1 is refused.", required: true));
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(new ListenerMetadata(TerraBaseListener.Client));
    }

    private static OpenApiParameter Query(string name, string description, bool required = false) => new()
    {
        Name = name,
        In = ParameterLocation.Query,
        Description = description,
        Required = required,
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(HttpContext, tenants, lineages, cancellationToken);
        if (resolved is not ResolvedShape shape)
        {
            return Error(resolved);
        }

        if (DocumentRoute.CheckParameters(HttpContext, "handle", "offset") is Validation invalid)
        {
            return ErrorResponse.From(invalid);
        }

        var query = HttpContext.Request.Query;
        if (!query.TryGetValue("handle", out var handles) || !ShapeTables.IsHandle(handles.ToString()))
        {
            return ErrorResponse.From(new Validation("handle must be the current h1- handle of 16 lowercase hex characters"));
        }

        if (!query.TryGetValue("offset", out var offsets) || CheckOffset(offsets.ToString()) is not { } offset || offset is Bootstrap)
        {
            return ErrorResponse.From(BadOffset);
        }

        var (scope, lineage) = (shape.Scope, shape.Lineage);
        if (!string.Equals(handles.ToString(), lineage.Handle, StringComparison.Ordinal))
        {
            return Refetch(HttpContext, lineage, "shape lineage changed");
        }

        var offsetText = offsets.ToString();
        var mirrored = await mirror.ManifestAsync(scope.Database, lineage.ShapeId, lineage.Handle, offsetText, 0, cancellationToken);
        if (mirrored.Objects.Count > 0)
        {
            return TypedResults.Json(mirrored, ShapeMirrorJsonContext.Default.ShapeMirrorManifest);
        }

        var read = offset switch
        {
            SnapshotOffset page => await snapshots.PageAsync(scope, lineage, page.Snapshot, page.Page, cancellationToken),
            DecimalOffset at => await chunks.ReadAsync(scope, lineage, at.Value, cancellationToken),
            _ => throw new System.Diagnostics.UnreachableException(),
        };
        if (read is not ShapeBody body)
        {
            return Error(read, HttpContext, lineage);
        }

        using (body.Lease)
        {
            if (body.UpToDate)
            {
                return TypedResults.Json(new ShapeMirrorManifest(lineage.Handle, []), ShapeMirrorJsonContext.Default.ShapeMirrorManifest);
            }
        }

        var manifest = await mirror.ManifestAsync(scope.Database, lineage.ShapeId, lineage.Handle, offsetText, 0, cancellationToken);
        return TypedResults.Json(manifest, ShapeMirrorJsonContext.Default.ShapeMirrorManifest);
    }
}
