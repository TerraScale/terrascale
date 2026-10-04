using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>Route, header and body handling shared by the document endpoints (Documents API v1).</summary>
internal static class DocumentRoute
{
    /// <summary>Base route of one database's collections.</summary>
    public const string Collections = "/v1/databases/{database}/collections";

    /// <summary>Base route of one collection's documents.</summary>
    public const string Collection = Collections + "/{collection}/documents";

    /// <summary>Largest request body of any other route.</summary>
    public const int MaxRequestBytes = 4 * 1024 * 1024;

    /// <summary>Bounds the JSON one page materializes; a page always holds at least one document.</summary>
    public const long MaxPageBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The contract's durability label for a write committed to the local SQLite file. It names the commit
    /// path only; durability is not qualified yet (docs/decisions/0004-document-api-v1.md).
    /// </summary>
    public const string Durability = "local_durable";

    public const string JsonMediaType = "application/json";

    public static DatabaseName Database(HttpContext context) =>
        DatabaseName.TryParse(context.Request.RouteValues["database"] as string, out var name)
            ? name
            : throw new ApiException("database must be a lowercase name of at most 63 characters");

    public static string CollectionName(HttpContext context) => CollectionName(context.Request.RouteValues["collection"] as string);

    public static string CollectionName(string? name) =>
        DatabaseName.TryParse(name, out var parsed)
            ? parsed.Value
            : throw new ApiException("collection must be a lowercase name of at most 63 characters");

    public static DocumentKey Key(HttpContext context) =>
        new(Database(context), CollectionName(context), DocumentId(context.Request.RouteValues["id"] as string));

    /// <summary>Bounds an address; the service permits a non-UUID only when that legacy id already exists.</summary>
    public static string DocumentId(string? id) =>
        id is { Length: > 0 and <= 256 } && !id.Any(char.IsControl)
            ? id
            : throw new ApiException("id must be 1 to 256 characters without control characters");

    /// <summary>A document URI built from escaped route values, preserving legacy identities.</summary>
    public static string Location(DocumentKey key) =>
        $"/v1/databases/{Uri.EscapeDataString(key.Database.Value)}/collections/{Uri.EscapeDataString(key.Collection)}/documents/{Uri.EscapeDataString(key.Id)}";

    public static bool IsUuid(string id) =>
        Guid.TryParseExact(id, "D", out var guid) && string.Equals(guid.ToString("D"), id, StringComparison.Ordinal);

    /// <summary>Accepts only the listed query parameters, each at most once and non-empty.</summary>
    public static IQueryCollection Parameters(HttpContext context, params string[] allowed)
    {
        var query = context.Request.Query;
        foreach (var (name, values) in query)
        {
            if (!allowed.Contains(name, StringComparer.Ordinal) || values.Count != 1 || string.IsNullOrEmpty(values[0]))
            {
                throw new ApiException(allowed.Length == 0
                    ? "this route takes no query parameters"
                    : $"query parameters are limited to: {string.Join(", ", allowed)}, each once and non-empty");
            }
        }

        return query;
    }

    /// <summary>Parses an optional integer query parameter within bounds.</summary>
    public static int Limit(IQueryCollection query, int fallback, int max) =>
        !query.TryGetValue("limit", out var raw)
            ? fallback
            : int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit is >= 1 && limit <= max
                ? limit
                : throw new ApiException($"limit must be an integer between 1 and {max}");

    /// <summary>Mutating routes do not deduplicate retries yet, so they refuse to pretend they do.</summary>
    public static void RejectIdempotencyKey(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey("Idempotency-Key"))
        {
            throw new ApiException("Idempotency-Key is not supported yet");
        }
    }

    /// <summary>Applies one write and returns its response body, with the new version as the ETag.</summary>
    public static async ValueTask<(WriteBody Body, bool Created)> WriteAsync(
        HttpContext context, IDocumentService documents, DocumentKey key, DocumentOperation operation, CancellationToken cancellationToken)
    {
        var written = await documents.WriteAsync(key.Database, [operation], indexErrors: false, cancellationToken).ConfigureAwait(false);
        var result = written.Results[0];
        var version = ETags.Version(written.Incarnation, key.Collection, key.Id, result.Version!.Value);
        ETags.Set(context, version);
        return (new WriteBody(key.Id, version, Decimal(written.Offset), Durability), result.Created);
    }

    public static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a bounded JSON body of one of <paramref name="mediaTypes"/> whose strings are valid text; a body that
    /// breaks either rule fails with <paramref name="code"/> (413 for size).
    /// </summary>
    public static async ValueTask<byte[]> ReadBodyAsync(
        HttpContext context, int maxBytes, string code, CancellationToken cancellationToken, params string[] mediaTypes)
    {
        if (context.Request.Headers.ContainsKey(HeaderNames.ContentEncoding))
        {
            throw new ApiException(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", "Content-Encoding is not accepted");
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType)
            || !mediaTypes.Contains(contentType.MediaType.Value, StringComparer.OrdinalIgnoreCase))
        {
            throw new ApiException(
                StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", $"Content-Type must be {string.Join(" or ", mediaTypes)}");
        }

        if (context.Request.ContentLength > maxBytes)
        {
            throw TooLarge(maxBytes);
        }

        // Kestrel stops reading early when the limit is writable; the loop enforces it either way.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = maxBytes;
        }

        using var body = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > maxBytes)
            {
                throw TooLarge(maxBytes);
            }

            body.Write(chunk, 0, read);
        }

        var json = body.ToArray();
        RequestJson.RequireText(json, code);
        return json;
    }

    private static ApiException TooLarge(int maxBytes) =>
        new(StatusCodes.Status413PayloadTooLarge, "payload_too_large", $"the body is at most {maxBytes / 1024 / 1024} MiB");

    /// <summary>Reads a document body: one JSON object within the document limits.</summary>
    public static async ValueTask<byte[]> ReadDocumentAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var json = await ReadBodyAsync(context, RequestJson.MaxDocumentBytes, "invalid_request", cancellationToken, JsonMediaType).ConfigureAwait(false);
        RequestJson.ValidateDocument(json);
        return json;
    }
}
