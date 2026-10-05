using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Net.Http.Headers;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>
/// <c>GET …/collections/{collection}/shape?where=&amp;handle=&amp;offset=&amp;live=&amp;cursor=</c>: syncs a
/// collection and filter as an immutable snapshot plus change log (docs/shape-reads.md).
/// </summary>
public sealed class GetShapeEndpoint(IShapeService shapes, ITenantDirectory tenants, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    private const string Revalidate = "private, max-age=0, must-revalidate";
    private const long LiveBucketSeconds = 20;

    /// <inheritdoc />
    public override void Configure()
    {
        Get(DocumentRoute.Collections + "/{collection}/shape");
        Name("getShape");
        Summary("Reads a shape: a snapshot and change log of a collection and filter");
        Description("""
            where is a query v1 filter as JSON (optional; every document without it). offset -1 answers 307 to the first snapshot page snap.<S>.0 of head S, with the shape's handle. Snapshot pages hold at most 1000 documents or 1 MiB in id order (one document may exceed 1 MiB with framing, up to 8 MiB); the last one continues at log position S. A log position O returns the commits after O, at most 4096 of them and 1 MiB, grouped by commit offset; a document that is deleted or stops matching is a delete op, and a delete may name a document the client never received. handle is required unless offset is -1; a stale handle or an offset outside the available history answers 409 must_refetch with the current handle: discard the shape and restart at -1. live=true (log positions only) waits up to 20 seconds for a commit when O is the head, then returns no changes; send the TB-Shape-Cursor of the previous response as cursor; each response advances beyond that cursor, even within one time bucket. Snapshot pages and closed chunks have immutable bytes and stable ETags. Origin responses use private, max-age=0, must-revalidate so every reuse checks current authorization; every response varies on Authorization. Shared CDN caching requires authorization before every cache lookup and is not enabled by these headers. TB-Shape-Handle, TB-Shape-Next-Offset and TB-Shape-Up-To-Date mirror the body; If-None-Match with the ETag returns 304. Treat client state as consistent only when up_to_date is true. At capacity, requests return 429 shape_busy; filters whose canonical continuation URL cannot fit an 8 KiB request line return 414 uri_too_long before redirecting.
            """);
        Tags("Shapes");
        Produces<ShapeBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status414UriTooLong);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        RequireAuthorization(ApiKeyScopes.ShapesRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var headers = HttpContext.Response.Headers;
        // Errors are never cached; successful responses replace this.
        headers.CacheControl = "no-store";
        var collection = DocumentRoute.CollectionName(HttpContext);
        // Malformed route values first (400), then the tenant: another tenant's database is 404 whatever the query.
        var database = await DocumentRoute.TenantAsync(HttpContext, tenants, cancellationToken);
        var query = DocumentRoute.Parameters(HttpContext, "where", "handle", "offset", "live", "cursor");
        var offset = (query.TryGetValue("offset", out var rawOffset) ? ShapeOffset.Parse(rawOffset.ToString()) : null)
            ?? throw new ApiException("offset is required: -1, snap.<S>.<P> or a decimal log position");
        var handle = query.TryGetValue("handle", out var rawHandle) ? rawHandle.ToString() : null;
        if (handle is null && offset.Kind != ShapeOffsetKind.Initial)
        {
            throw new ApiException("handle is required unless offset is -1");
        }

        var live = query.TryGetValue("live", out var rawLive);
        if (live && (rawLive != "true" || offset.Kind != ShapeOffsetKind.Log))
        {
            throw new ApiException("live must be true and needs a log position");
        }

        long inputCursor = -1;
        if (query.TryGetValue("cursor", out var cursor)
            && (!live || !long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out inputCursor) || inputCursor == long.MaxValue))
        {
            throw new ApiException("cursor is a decimal live bucket and needs live=true");
        }

        var (filter, canonical) = Where(query);
        var result = await shapes.ReadAsync(
            new ShapeRequest(database, collection, filter, canonical, offset, handle, live), cancellationToken);
        headers[ShapeHeaders.Handle] = result.Handle;
        if (result.Status == ShapeStatus.MustRefetch)
        {
            return TypedResults.Json(
                new ErrorBody(new ErrorDetail("must_refetch", "the shape handle is stale; restart at offset -1", Handle: result.Handle)),
                TerraBaseJsonContext.Default.ErrorBody,
                statusCode: StatusCodes.Status409Conflict);
        }

        headers.Vary = HeaderNames.Authorization;
        if (result.Status == ShapeStatus.Redirect)
        {
            headers.CacheControl = Revalidate;
            return TypedResults.Redirect(Location(canonical, result.Handle, result.NextOffset), permanent: false, preserveMethod: true);
        }

        var now = time.GetUtcNow().ToUnixTimeSeconds();
        // Stored credentials can be revoked between reads. Every cache reuse must reach current
        // authorization; a future edge may cache these immutable bytes only after its own check.
        headers.CacheControl = Revalidate;
        if (live)
        {
            headers[ShapeHeaders.Cursor] = DocumentRoute.Decimal(Math.Max(now / LiveBucketSeconds, inputCursor + 1));
        }

        headers[ShapeHeaders.NextOffset] = result.NextOffset;
        headers[ShapeHeaders.UpToDate] = result.UpToDate ? "true" : "false";
        // Historical snapshots and closed log chunks keep the same bytes and validator after writes.
        var version = $"{result.ShapeId}:{result.Handle}:{offset}:{result.NextOffset}:{result.Digest}";
        headers.ETag = $"\"{version}\"";
        return ETags.NotModified(HttpContext, live: true, version)
            ? TypedResults.StatusCode(StatusCodes.Status304NotModified)
            : TypedResults.Bytes(result.Body, DocumentRoute.JsonMediaType);
    }

    // Unpadded base64url of the longest id storage admits (1024 UTF-8 bytes): ceil(1024 * 4 / 3) characters.
    private static readonly string LongestEncodedId = new('A', ((1024 * 4) + 2) / 3);

    private (Engine.DocumentFilter? Filter, string Canonical) Where(IQueryCollection query)
    {
        if (!query.TryGetValue("where", out var where))
        {
            return (null, "");
        }

        // JsonDocument accepts lone surrogate escapes such as "\uD800"; reject them as a 400, like query bodies.
        RequestJson.RequireText(Encoding.UTF8.GetBytes(where.ToString()), "invalid_query");
        try
        {
            using var json = JsonDocument.Parse(where.ToString());
            var filter = QueryParser.Filter(json.RootElement);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                json.RootElement.WriteTo(writer);
            }

            var canonical = Encoding.UTF8.GetString(buffer.WrittenSpan);
            // The relaxed encoder still escapes some Unicode. Check the encoded target, reserving
            // the longest snapshot continuation and 64 bytes for the request line: the longest head
            // and the base64url of the longest stored id (1024 UTF-8 bytes, legacy ids included).
            // A live log position with cursor is shorter than this snapshot offset.
            var resume = Location(canonical, "h1-0000000000000000", $"snap.{long.MaxValue}.{LongestEncodedId}");
            if (resume.Length > (8 * 1024) - 64)
            {
                throw new ApiException(StatusCodes.Status414UriTooLong, "uri_too_long", "the shape filter exceeds the supported continuation URL size");
            }

            return (filter, canonical);
        }
        catch (JsonException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_query", "where must be a JSON filter");
        }
    }

    // The canonical filter makes every client of a shape converge on the same URLs, and so on the same cache entries.
    private string Location(string canonical, string handle, string offset)
    {
        var parameters = canonical.Length == 0 ? "" : $"where={Uri.EscapeDataString(canonical)}&";
        return $"{HttpContext.Request.PathBase}{HttpContext.Request.Path}?{parameters}handle={handle}&offset={offset}";
    }
}

/// <summary>Response headers of shape reads.</summary>
public static class ShapeHeaders
{
    /// <summary>The shape's current handle.</summary>
    public const string Handle = "TB-Shape-Handle";

    /// <summary>The offset to request next.</summary>
    public const string NextOffset = "TB-Shape-Next-Offset";

    /// <summary><c>true</c> when the next offset is the head.</summary>
    public const string UpToDate = "TB-Shape-Up-To-Date";

    /// <summary>The live bucket to send as <c>cursor</c> on the next live request.</summary>
    public const string Cursor = "TB-Shape-Cursor";
}
