using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Errors;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>The request's tenant, or why it has none: a malformed database name, or a database the key does not reach.</summary>
internal readonly union TenantResult(TenantContext, Validation, NotFound);

/// <summary>The addressed document, or why the address is refused.</summary>
internal readonly union KeyResult(DocumentKey, Validation, NotFound);

/// <summary>A request body, or why it is refused.</summary>
internal readonly union BodyResult(byte[], Validation, PayloadTooLarge, UnsupportedMediaType);

/// <summary>Route, header and body handling shared by the document endpoints (Documents API v1).</summary>
internal static class DocumentRoute
{
    /// <summary>Route of one database: a project of the API key's organization, and a database name unique in it.</summary>
    public const string DatabaseRoute = "/v1/projects/{project}/databases/{database}";

    /// <summary>Base route of one database's collections.</summary>
    public const string Collections = DatabaseRoute + "/collections";

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

    /// <summary>The one answer for a database that does not exist, or that the caller's tenant does not reach.</summary>
    public static readonly NotFound UnknownDatabase = new("the database does not exist", "unknown_database");

    public static string Project(HttpContext context) => (string)context.Request.RouteValues["project"]!;

    public static DatabaseName Database(HttpContext context) => CheckDatabase(context) switch
    {
        DatabaseName name => name,
        Validation error => throw Throwable(error),
    };

    /// <summary>The route's database name, or the rule it breaks.</summary>
    public static Valid<DatabaseName> CheckDatabase(HttpContext context) =>
        DatabaseName.TryParse(context.Request.RouteValues["database"] as string, out var name)
            ? name
            : new Validation("database must be a lowercase name of at most 63 characters");

    /// <summary>As <see cref="CheckTenantAsync"/>, for the callers not converted to unions yet.</summary>
    public static async ValueTask<TenantContext> TenantAsync(HttpContext context, ITenantDirectory tenants, CancellationToken cancellationToken) =>
        await CheckTenantAsync(context, tenants, cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext tenant => tenant,
            Validation error => throw Throwable(error),
            NotFound error => throw Throwable(error),
        };

    /// <summary>
    /// Resolves the request's tenant: the route's database, in the route's project of the API key's organization.
    /// A database that does not exist and one the key does not reach both answer 404 <c>unknown_database</c>, so a key
    /// learns nothing about other tenants.
    /// </summary>
    public static async ValueTask<TenantResult> CheckTenantAsync(HttpContext context, ITenantDirectory tenants, CancellationToken cancellationToken) =>
        CheckDatabase(context) switch
        {
            Validation error => error,
            DatabaseName name => await ResolveAsync(context, tenants, name, cancellationToken).ConfigureAwait(false),
        };

    private static async ValueTask<TenantResult> ResolveAsync(HttpContext context, ITenantDirectory tenants, DatabaseName name, CancellationToken cancellationToken)
    {
        var key = ApiKey.Of(context);
        if (await tenants.FindDatabaseAsync(key.OrganizationId, Project(context), name.Value, cancellationToken).ConfigureAwait(false) is not { } tenant
            || !key.Reaches(tenant))
        {
            return UnknownDatabase;
        }

        context.Features.Set(tenant);
        return tenant;
    }

    public static string CollectionName(HttpContext context) => CollectionName(context.Request.RouteValues["collection"] as string);

    public static string CollectionName(string? name) => CheckCollectionName(name) switch
    {
        string collection => collection,
        Validation error => throw Throwable(error),
    };

    /// <summary>The route's collection name, or the rule it breaks.</summary>
    public static Valid<string> CheckCollectionName(HttpContext context) => CheckCollectionName(context.Request.RouteValues["collection"] as string);

    /// <summary>A collection name, or the rule it breaks.</summary>
    public static Valid<string> CheckCollectionName(string? name) =>
        DatabaseName.TryParse(name, out var parsed)
            ? parsed.Value
            : new Validation("collection must be a lowercase name of at most 63 characters");

    /// <summary>The route's document: malformed collection and id first (400), then the tenant (404).</summary>
    public static async ValueTask<KeyResult> CheckKeyAsync(HttpContext context, ITenantDirectory tenants, CancellationToken cancellationToken) =>
        (CheckCollectionName(context), CheckDocumentId(context.Request.RouteValues["id"] as string)) switch
        {
            (Validation error, _) => error,
            (_, Validation error) => error,
            (string collection, string id) => await CheckTenantAsync(context, tenants, cancellationToken).ConfigureAwait(false) switch
            {
                TenantContext tenant => new DocumentKey(tenant, collection, id),
                Validation error => error,
                NotFound error => error,
            },
        };

    /// <summary>Bounds an address; the service permits a non-UUID only when that legacy id already exists.</summary>
    public static string DocumentId(string? id) => CheckDocumentId(id) switch
    {
        string valid => valid,
        Validation error => throw Throwable(error),
    };

    /// <summary>A document id, or the rule it breaks.</summary>
    public static Valid<string> CheckDocumentId(string? id) =>
        id is { Length: > 0 and <= 256 } && !id.Any(char.IsControl)
            ? id
            : new Validation("id must be 1 to 256 characters without control characters");

    /// <summary>A document URI built from escaped route values, preserving legacy identities.</summary>
    public static string Location(HttpContext context, DocumentKey key) =>
        $"/v1/projects/{Uri.EscapeDataString(Project(context))}/databases/{Uri.EscapeDataString(Database(context).Value)}/collections/{Uri.EscapeDataString(key.Collection)}/documents/{Uri.EscapeDataString(key.Id)}";

    public static bool IsUuid(string id) =>
        Guid.TryParseExact(id, "D", out var guid) && string.Equals(guid.ToString("D"), id, StringComparison.Ordinal);

    /// <summary>As <see cref="CheckParameters"/>, for the callers not converted to unions yet.</summary>
    public static IQueryCollection Parameters(HttpContext context, params string[] allowed) => CheckParameters(context, allowed) switch
    {
        IQueryCollection query => query,
        Validation error => throw Throwable(error),
    };

    /// <summary>Accepts only the listed query parameters, each at most once and non-empty.</summary>
    public static Valid<IQueryCollection> CheckParameters(HttpContext context, params string[] allowed)
    {
        var query = context.Request.Query;
        foreach (var (name, values) in query)
        {
            if (!allowed.Contains(name, StringComparer.Ordinal) || values.Count != 1 || string.IsNullOrEmpty(values[0]))
            {
                return new Validation(allowed.Length == 0
                    ? "this route takes no query parameters"
                    : $"query parameters are limited to: {string.Join(", ", allowed)}, each once and non-empty");
            }
        }

        return query;
    }

    /// <summary>Parses an optional integer query parameter within bounds.</summary>
    public static Valid<int> CheckLimit(IQueryCollection query, int fallback, int max) =>
        !query.TryGetValue("limit", out var raw)
            ? fallback
            : int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit is >= 1 && limit <= max
                ? limit
                : new Validation($"limit must be an integer between 1 and {max}");

    /// <summary>As <see cref="CheckIdempotencyKey"/>, for the callers not converted to unions yet.</summary>
    public static void RejectIdempotencyKey(HttpContext context)
    {
        if (CheckIdempotencyKey(context) is Validation error)
        {
            throw Throwable(error);
        }
    }

    /// <summary>Mutating routes do not deduplicate retries yet, so they refuse to pretend they do.</summary>
    public static Valid<Done> CheckIdempotencyKey(HttpContext context) =>
        context.Request.Headers.ContainsKey("Idempotency-Key")
            ? new Validation("Idempotency-Key is not supported yet")
            : new Done();

    /// <summary>Applies one write and returns its response body, with the new version as the ETag.</summary>
    public static async ValueTask<(WriteBody Body, bool Created)> WriteAsync(
        HttpContext context, IDocumentService documents, DocumentKey key, DocumentOperation operation, CancellationToken cancellationToken)
    {
        var written = await documents.WriteAsync(key.Database, [operation], indexErrors: false, cancellationToken).ConfigureAwait(false);
        var result = written.Results[0];
        var version = result.ETag!;
        ETags.Set(context, version);
        return (new WriteBody(key.Id, version, Decimal(written.Offset), Durability), result.Created);
    }

    public static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>As <see cref="CheckBodyAsync"/>, for the callers not converted to unions yet.</summary>
    public static async ValueTask<byte[]> ReadBodyAsync(
        HttpContext context, int maxBytes, string code, CancellationToken cancellationToken, params string[] mediaTypes) =>
        await CheckBodyAsync(context, maxBytes, code, cancellationToken, mediaTypes).ConfigureAwait(false) switch
        {
            byte[] body => body,
            Validation error => throw Throwable(error),
            PayloadTooLarge error => throw Throwable(error),
            UnsupportedMediaType error => throw Throwable(error),
        };

    /// <summary>
    /// Reads a bounded JSON body of one of <paramref name="mediaTypes"/> whose strings are valid text; a body that
    /// breaks either rule fails with <paramref name="code"/> (413 for size).
    /// </summary>
    public static async ValueTask<BodyResult> CheckBodyAsync(
        HttpContext context, int maxBytes, string code, CancellationToken cancellationToken, params string[] mediaTypes)
    {
        if (context.Request.Headers.ContainsKey(HeaderNames.ContentEncoding))
        {
            return new UnsupportedMediaType("Content-Encoding is not accepted");
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType)
            || !mediaTypes.Contains(contentType.MediaType.Value, StringComparer.OrdinalIgnoreCase))
        {
            return new UnsupportedMediaType($"Content-Type must be {string.Join(" or ", mediaTypes)}");
        }

        if (context.Request.ContentLength > maxBytes)
        {
            return TooLarge(maxBytes);
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
                return TooLarge(maxBytes);
            }

            body.Write(chunk, 0, read);
        }

        var json = body.ToArray();
        return RequestJson.IsText(json) ? json : new Validation(RequestJson.TextRule, code);
    }

    private static PayloadTooLarge TooLarge(int maxBytes) => new($"the body is at most {maxBytes / 1024 / 1024} MiB");

    /// <summary>Reads a document body: one JSON object within the document limits.</summary>
    public static ValueTask<BodyResult> CheckDocumentAsync(HttpContext context, CancellationToken cancellationToken) =>
        CheckBodyAsync(context, RequestJson.MaxDocumentBytes, "invalid_request", cancellationToken, JsonMediaType);

    /// <summary>
    /// A failure as the exception that the callers not converted to unions yet still throw (docs/error-handling.md);
    /// <see cref="BadRequests"/> answers it with the same status, code and message.
    /// </summary>
    internal static ApiException Throwable(Failure failure) => failure switch
    {
        NotFound e => new ApiException(StatusCodes.Status404NotFound, e.Code, e.Message),
        Conflict e => new ApiException(StatusCodes.Status409Conflict, e.Code, e.Message),
        Validation e => new ApiException(StatusCodes.Status400BadRequest, e.Code, e.Message),
        PreconditionFailed e => new ApiException(StatusCodes.Status412PreconditionFailed, e.Code, e.Message),
        Unauthorized e => new ApiException(StatusCodes.Status401Unauthorized, e.Code, e.Message),
        Forbidden e => new ApiException(StatusCodes.Status403Forbidden, e.Code, e.Message),
        QuotaExceeded e => new ApiException(StatusCodes.Status429TooManyRequests, e.Code, e.Message),
        Unavailable e => new ApiException(StatusCodes.Status503ServiceUnavailable, e.Code, e.Message),
        UriTooLong e => new ApiException(StatusCodes.Status414UriTooLong, e.Code, e.Message),
        PayloadTooLarge e => new ApiException(StatusCodes.Status413PayloadTooLarge, e.Code, e.Message),
        UnsupportedMediaType e => new ApiException(StatusCodes.Status415UnsupportedMediaType, e.Code, e.Message),
    };
}
