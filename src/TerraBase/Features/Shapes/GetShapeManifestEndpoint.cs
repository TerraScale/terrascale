using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Microsoft.OpenApi;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Documents;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Shapes;

/// <summary><c>GET …/collections/{collection}/shape/manifest</c>: the mirrored objects of a shape's immutable offsets (FR-22, section 14; T2 in free-reads-cdn.md).</summary>
internal sealed class GetShapeManifestEndpoint(
    IDocumentService documents, ITenantDirectory tenants, IShapeLineages lineages, IShapeSnapshots snapshots, IShapeChunks chunks, IShapeMirror mirror)
    : EndpointWithoutRequest<IResult>
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Validation BadOffset = new("offset must be snap.<S>.<P> or a decimal commit boundary");

    /// <inheritdoc />
    public override void Configure()
    {
        Get(DocumentRoute.Collections + "/{collection}/shape/manifest");
        Name("getShapeManifest");
        Summary("Lists a shape's mirrored sealed objects");
        Description("""
            Requires documents:read. End-user tokens cannot retrieve mirror manifests; use the origin shape endpoint. Lists up to 8 URLs of the immutable TBS1 objects that begin at offset, each followed by its next_offset; a URL is a stable Cloudflare CDN path for an R2 bucket, otherwise a presigned bucket URL. Fetch an object, check its size and sha256, decrypt it with the key from GET …/shape/keys, and apply it only if all of that passes. Otherwise read that offset from the origin with GET …/shape. The list is empty when the offset is not mirrored yet, or is a tail body that is up to date. A stale handle, an offset below retention or an unpublished offset answers 409 must_refetch with the current handle in TB-Shape-Handle and the body; restart at offset=-1 on GET …/shape. Responses are private, no-store; read the manifest again after expires_at, the start of the next epoch, when a presigned URL expires.
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
            Query("offset", "snap.<S>.<P> or a decimal commit boundary; -1 is refused.", required: true),
            Query("where", "JSON filter in the grammar of POST …/query."));
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
        if (CollectionRuleAccess.EndUser)
        {
            return ErrorResponse.From(ShapeAccess.Denied);
        }

        return await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => DocumentRoute.CheckCollectionName(HttpContext) switch
            {
                Validation error => ErrorResponse.From(error),
                string name => await documents.GetCollectionAsync(database, name, cancellationToken) switch
                {
                    NotFound error => ErrorResponse.From(error),
                    Conflict error => ErrorResponse.From(error),
                    Unavailable error => ErrorResponse.From(error),
                    CollectionInfo collection => CheckRequest(database, collection) switch
                    {
                        Validation error => ErrorResponse.From(error),
                        ManifestRequest request => await ServeAsync(request, cancellationToken),
                    },
                },
            },
        };
    }

    private async ValueTask<IResult> ServeAsync(ManifestRequest request, CancellationToken cancellationToken)
    {
        var resolved = await lineages.ResolveAsync(request.Scope, cancellationToken);
        if (resolved is not ShapeLineage lineage)
        {
            return resolved switch
            {
                QuotaExceeded error => ErrorResponse.From(error),
                Forbidden denied => ErrorResponse.From(denied),
                Evicted evicted => Refetch(evicted.Lineage, "shape lineage changed or filter evicted"),
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
                ShapeLineage => throw new System.Diagnostics.UnreachableException(),
            };
        }

        if (!string.Equals(request.Handle, lineage.Handle, StringComparison.Ordinal))
        {
            return Refetch(lineage, "shape lineage changed");
        }

        // A mirrored object exists only for a sealed offset of this handle, and the listing rechecks the retention floor, so a
        // listed offset needs no payload read: T1 readers cost the origin no storage read once the mirror holds the object.
        ShapeMirrorManifest mirrored;
        try
        {
            mirrored = await mirror.ManifestAsync(request.Scope.Database, lineage.ShapeId, lineage.Handle, request.OffsetText, 0, cancellationToken);
        }
        catch (ShapeCleanupSaturatedException error)
        {
            return ErrorResponse.From(new Unavailable(error.Message));
        }

        if (mirrored.Objects.Count > 0)
        {
            return TypedResults.Json(mirrored, ShapeMirrorJsonContext.Default.ShapeMirrorManifest);
        }

        // Otherwise verify that the offset is published and retained, so an unknown offset still answers 409.
        var read = request.Offset switch
        {
            SnapshotOffset page => await snapshots.PageAsync(request.Scope, lineage, page.Snapshot, page.AfterId, cancellationToken),
            DecimalOffset at => await chunks.ReadAsync(request.Scope, lineage, at.Value, cancellationToken),
            _ => throw new System.Diagnostics.UnreachableException(),
        };
        if (read is not ShapeBody body)
        {
            return read switch
            {
                MustRefetch refetch => Refetch(refetch.Lineage ?? lineage, refetch.Message),
                QuotaExceeded error => ErrorResponse.From(error),
                Forbidden denied => ErrorResponse.From(denied),
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
                ShapeBody => throw new System.Diagnostics.UnreachableException(),
            };
        }

        using (body.Lease)
        {
            // Fresh tails have no immutable T1 object.
            if (body.UpToDate)
            {
                return TypedResults.Json(new ShapeMirrorManifest(lineage.Handle, []), ShapeMirrorJsonContext.Default.ShapeMirrorManifest);
            }
        }

        // The read may have adopted the object's metadata in the meantime.
        try
        {
            var manifest = await mirror.ManifestAsync(request.Scope.Database, lineage.ShapeId, lineage.Handle, request.OffsetText, 0, cancellationToken);
            return TypedResults.Json(manifest, ShapeMirrorJsonContext.Default.ShapeMirrorManifest);
        }
        catch (ShapeCleanupSaturatedException error)
        {
            return ErrorResponse.From(new Unavailable(error.Message));
        }
    }

    private Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ShapeErrorBody> Refetch(ShapeLineage lineage, string message)
    {
        HttpContext.Response.Headers["TB-Shape-Handle"] = lineage.Handle;
        return TypedResults.Json(new ShapeErrorBody(new ShapeErrorDetail("must_refetch", message, lineage.Handle)),
            ShapeJsonContext.Default.ShapeErrorBody, statusCode: StatusCodes.Status409Conflict);
    }

    // Same parameter rules as GetShapeEndpoint: no live, cursor or resume, and handle and offset are required.
    private Valid<ManifestRequest> CheckRequest(TenantContext database, CollectionInfo collection)
    {
        if (DocumentRoute.CheckParameters(HttpContext, "where", "handle", "offset") is Validation invalid)
        {
            return invalid;
        }

        var query = HttpContext.Request.Query;
        string? Parameter(string name)
        {
            return query.TryGetValue(name, out var value) ? value.ToString() : null;
        }

        if (Parameter("handle") is not { } handle || !IsHandle(handle))
        {
            return new Validation("handle must be the current h1- handle of 16 lowercase hex characters");
        }

        if (Parameter("offset") is not { } offsetText || CheckOffset(offsetText) is not { } offset)
        {
            return BadOffset;
        }

        var filter = ShapeFilter.All;
        if (Parameter("where") is { } where)
        {
            switch (ShapeFilter.Parse(where))
            {
                case Validation error:
                    return error;
                case ShapeFilter parsed:
                    filter = parsed;
                    break;
            }
        }

        return new ManifestRequest(new ShapeScope(database, collection, filter.Canonical, filter), offset, offsetText, handle);
    }

    private static bool IsHandle(string handle) =>
        handle.Length == 19 && handle.StartsWith("h1-", StringComparison.Ordinal) && !handle.AsSpan(3).ContainsAnyExcept(LowerHex);

    // Canonical decimals: no sign, no leading zeros, at most long.MaxValue. A bare -1 is neither decimal nor snapshot, so it is refused.
    private static long? Decimal(string text) =>
        text.Length is > 0 and <= 19 && (text == "0" || text[0] != '0') && text.All(char.IsAsciiDigit)
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static Offset? CheckOffset(string text)
    {
        if (Decimal(text) is { } at)
        {
            return new DecimalOffset(at);
        }

        var parts = text.Split('.');
        if (parts is not ["snap", var snapshotText, var after] || Decimal(snapshotText) is not { } snapshot)
        {
            return null;
        }

        if (after == "0")
        {
            return new SnapshotOffset(snapshot, null);
        }

        // P is the canonical unpadded base64url of a document id's UTF-8 bytes.
        if (after.Length > Base64Url.GetEncodedLength(256 * 3) || !Base64Url.IsValid(after, out _))
        {
            return null;
        }

        var bytes = Base64Url.DecodeFromChars(after);
        if (Base64Url.EncodeToString(bytes) != after)
        {
            return null;
        }

        try
        {
            return DocumentRoute.CheckStoredId(StrictUtf8.GetString(bytes)) is string id ? new SnapshotOffset(snapshot, id) : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private sealed record ManifestRequest(ShapeScope Scope, Offset Offset, string OffsetText, string Handle);

    private abstract record Offset;

    private sealed record SnapshotOffset(long Snapshot, string? AfterId) : Offset;

    private sealed record DecimalOffset(long Value) : Offset;
}
