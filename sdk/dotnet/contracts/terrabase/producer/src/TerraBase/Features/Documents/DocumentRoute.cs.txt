using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Replication;
using TerraBase.Features.UserAuth;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>
/// The request's tenant, or why it has none: a malformed database name, a database the key does not reach, or a catalog
/// this build cannot use.
/// </summary>
internal readonly union TenantResult(TenantContext, Validation, NotFound, Unavailable);

/// <summary>The addressed document, or why the address is refused.</summary>
internal readonly union KeyResult(DocumentKey, Validation, NotFound, Unavailable);

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

    /// <summary>Point operations carry the raw document key in one query parameter.</summary>
    public const string Document = Collection + "/by-key";

    /// <summary>The durability barrier and retry semantics shared by single-document mutations.</summary>
    public const string WriteDurabilityDescription = "Mutations in a cell wait up to 10 seconds for another placed region's durable acknowledgement and answer remote_durable; a cell of one answers local_durable. Before execution, 503 remote_region_unavailable or confirmation_busy carries Retry-After. An ambiguous commit or missing acknowledgement answers 503 write_outcome_unknown with region and machine, without Retry-After; never replay it automatically.";

    private static readonly UTF8Encoding KeyEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Largest request body of any other route.</summary>
    public const int MaxRequestBytes = 4 * 1024 * 1024;

    /// <summary>Bounds the JSON one page materializes; a page always holds at least one document.</summary>
    public const long MaxPageBytes = 8 * 1024 * 1024;

    public const string JsonMediaType = "application/json";

    /// <summary>The one answer for a database that does not exist, or that the caller's tenant does not reach.</summary>
    public static readonly NotFound UnknownDatabase = new("the database does not exist", "unknown_database");

    public static string Project(HttpContext context) => (string)context.Request.RouteValues["project"]!;

    /// <summary>The route's database name, which the tenant check already validated.</summary>
    public static DatabaseName Database(HttpContext context) => CheckDatabase(context) switch
    {
        DatabaseName name => name,
        Validation => throw new UnreachableException("the tenant check validates the database name first"),
    };

    /// <summary>The route's database name, or the rule it breaks.</summary>
    public static Valid<DatabaseName> CheckDatabase(HttpContext context) =>
        DatabaseName.TryParse(context.Request.RouteValues["database"] as string, out var name)
            ? name
            : new Validation("database must be a lowercase name of at most 63 characters");

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
        if (!EntityIds.IsValid(Project(context)))
        {
            return new Validation("project must be a canonical uppercase ULID");
        }

        if (context.Features.Get<ProjectAccess>() is { } access)
        {
            if (access.DatabaseName is not null && !string.Equals(access.DatabaseName, name.Value, StringComparison.Ordinal))
            {
                return UnknownDatabase;
            }

            var resolved = await tenants.FindDatabaseAsync(access.OrganizationId, access.ProjectId, name.Value, cancellationToken).ConfigureAwait(false);
            if (resolved is not TenantContext database
                || (access.DatabaseId is not null && !string.Equals(access.DatabaseId, database.DatabaseId, StringComparison.Ordinal)))
            {
                return resolved switch
                {
                    Unavailable error => error,
                    TenantContext or NotFound => UnknownDatabase,
                };
            }

            return await AdmitCopyAsync(context, database, cancellationToken).ConfigureAwait(false);
        }

        var key = ApiKey.Of(context);
        var found = await tenants.FindDatabaseAsync(key.OrganizationId, Project(context), name.Value, cancellationToken).ConfigureAwait(false);
        if (found is not TenantContext tenant || !key.Reaches(tenant))
        {
            return found switch
            {
                Unavailable error => error,
                TenantContext or NotFound => UnknownDatabase,
            };
        }

        return await AdmitCopyAsync(context, tenant, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<TenantResult> AdmitCopyAsync(HttpContext context, TenantContext tenant, CancellationToken cancellationToken)
    {
        var faults = context.RequestServices.GetRequiredService<IReplicaCopyFaults>();
        var storage = context.RequestServices.GetRequiredService<IRebuildStorage>();
        var incarnation = await storage.IncarnationAsync(tenant, cancellationToken).ConfigureAwait(false);
        if (await faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, cancellationToken).ConfigureAwait(false)
            || incarnation is { } pin && await faults.RefusedAsync(tenant, pin, cancellationToken).ConfigureAwait(false))
        {
            throw new ReplicaCopyFaultedException("this database copy is awaiting certified recovery");
        }

        context.Features.Set(tenant);
        return tenant;
    }

    /// <summary>The route's collection name, or the rule it breaks.</summary>
    public static Valid<string> CheckCollectionName(HttpContext context) => CheckCollectionName(context.Request.RouteValues["collection"] as string);

    /// <summary>A collection name, or the rule it breaks.</summary>
    public static Valid<string> CheckCollectionName(string? name) =>
        !DatabaseName.TryParse(name, out var parsed)
            ? new Validation("collection must be a lowercase name of at most 63 characters")
            : CollectionName.IsReserved(parsed.Value)
                ? new Validation("collection name is reserved", "reserved_name")
                : parsed.Value;

    /// <summary>The route's document: malformed collection and id first (400), then the tenant (404).</summary>
    public static async ValueTask<KeyResult> CheckKeyAsync(HttpContext context, ITenantDirectory tenants, CancellationToken cancellationToken) =>
        (CheckCollectionName(context), CheckDocumentParameter(context)) switch
        {
            (Validation error, _) => error,
            (_, Validation error) => error,
            (string collection, string id) => await CheckTenantAsync(context, tenants, cancellationToken).ConfigureAwait(false) switch
            {
                TenantContext tenant => new DocumentKey(tenant, collection, id),
                Validation error => error,
                NotFound error => error,
                Unavailable error => error,
            },
        };

    /// <summary>Decodes one UTF-8 query key exactly once, without path normalization or replacement characters.</summary>
    public static Valid<string> CheckDocumentParameter(HttpContext context)
    {
        string? encoded = null;
        foreach (var parameter in new QueryStringEnumerable(context.Request.QueryString.Value))
        {
            if (!parameter.EncodedName.Span.SequenceEqual("id"))
            {
                continue;
            }

            if (encoded is not null)
            {
                return new Validation("id is required exactly once");
            }

            // A UTF-16 character needs at most three UTF-8 bytes, each query-encoded as three ASCII characters.
            if (parameter.EncodedValue.Length > 256 * 9)
            {
                return new Validation("id must be 1 to 256 characters without control characters");
            }

            encoded = parameter.EncodedValue.ToString();
        }

        if (string.IsNullOrEmpty(encoded))
        {
            return new Validation("id is required exactly once and must be non-empty");
        }

        for (var i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] > 127 || (encoded[i] == '%' &&
                (i + 2 >= encoded.Length || !Uri.IsHexDigit(encoded[i + 1]) || !Uri.IsHexDigit(encoded[i + 2]))))
            {
                return new Validation("id must use valid UTF-8 query encoding");
            }

            if (encoded[i] == '%')
            {
                i += 2;
            }
        }

        try
        {
            return CheckDocumentId(KeyEncoding.GetString(HttpUtility.UrlDecodeToBytes(encoded)!));
        }
        catch (DecoderFallbackException)
        {
            return new Validation("id must use valid UTF-8 query encoding");
        }
    }

    /// <summary>
    /// A caller-supplied document key, or the length or character rule it breaks.
    /// </summary>
    public static Valid<string> CheckDocumentId(string? id) =>
        id is { Length: > 0 and <= 256 } && !id.Any(char.IsControl)
            ? id
            : new Validation("id must be 1 to 256 characters without control characters");

    /// <summary>A document URI built from escaped route values, escaping caller-supplied keys.</summary>
    public static string Location(HttpContext context, DocumentKey key) =>
        $"/v1/projects/{Uri.EscapeDataString(Project(context))}/databases/{Uri.EscapeDataString(Database(context).Value)}/collections/{Uri.EscapeDataString(key.Collection)}/documents/by-key?id={Uri.EscapeDataString(key.Id)}";

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

    /// <summary>Mutating routes do not deduplicate retries yet, so they refuse to pretend they do.</summary>
    public static Valid<Done> CheckIdempotencyKey(HttpContext context) =>
        context.Request.Headers.ContainsKey("Idempotency-Key")
            ? new Validation("Idempotency-Key is not supported yet")
            : new Done();

    /// <summary>
    /// Applies one write and answers it: <paramref name="answer"/> gets the response body and whether the write created
    /// the document, with the new version as the ETag; a refused write is its error response.
    /// </summary>
    public static async ValueTask<IResult> WriteAsync(
        HttpContext context, IDocumentService documents, DocumentKey key, DocumentOperation operation, Func<WriteBody, bool, IResult> answer,
        CancellationToken cancellationToken) =>
        await documents.WriteConfirmedAsync(key.Database, [operation], indexErrors: false, cancellationToken).ConfigureAwait(false) switch
        {
            DocumentRejected rejected => ErrorResponse.From(rejected.Error),
            WriteResult written => Written(context, key, written, answer),
        };

    private static IResult Written(HttpContext context, DocumentKey key, WriteResult written, Func<WriteBody, bool, IResult> answer)
    {
        var result = written.Results[0];
        var version = result.ETag!;
        ETags.Set(context, version);
        return answer(new WriteBody(key.Id, version, Decimal(written.Offset), written.Durability, Json(result.Document)), result.Created);
    }

    /// <summary>A stored document as a response value, or null when the operation returns none.</summary>
    public static JsonElement? Json(ReadOnlyMemory<byte>? document)
    {
        if (document is not { } json)
        {
            return null;
        }

        using var parsed = JsonDocument.Parse(json, RequestJson.StoredDocumentOptions);
        return parsed.RootElement.Clone();
    }

    public static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a bounded JSON body of one of <paramref name="mediaTypes"/> (any Content-Type, or none, when empty) whose
    /// strings are valid text; a body that breaks either rule fails with <paramref name="code"/> (413 for size).
    /// </summary>
    public static async ValueTask<BodyResult> CheckBodyAsync(
        HttpContext context, int maxBytes, string code, CancellationToken cancellationToken, params string[] mediaTypes)
    {
        if (context.Request.Headers.ContainsKey(HeaderNames.ContentEncoding))
        {
            return new UnsupportedMediaType("Content-Encoding is not accepted");
        }

        if (mediaTypes.Length > 0 && (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType)
            || !mediaTypes.Contains(contentType.MediaType.Value, StringComparer.OrdinalIgnoreCase)))
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

        var json = context.Request.ContentLength is { } length
            ? await ReadExactlyAsync(context.Request.Body, (int)length, cancellationToken).ConfigureAwait(false)
            : await ReadChunkedAsync(context.Request.Body, maxBytes, cancellationToken).ConfigureAwait(false);
        return json is null ? TooLarge(maxBytes) : RequestJson.IsText(json) ? json : new Validation(RequestJson.TextRule, code);
    }

    // With Content-Length (checked against the limit above) the body goes straight into one array of that size: no
    // staging buffer and no copy. Kestrel fails a body that ends early; a shorter read keeps what arrived.
    private static async ValueTask<byte[]> ReadExactlyAsync(Stream body, int length, CancellationToken cancellationToken)
    {
        var json = new byte[length];
        var filled = 0;
        int read;
        while (filled < length && (read = await body.ReadAsync(json.AsMemory(filled), cancellationToken).ConfigureAwait(false)) > 0)
        {
            filled += read;
        }

        return filled == length ? json : json[..filled];
    }

    // Without Content-Length (chunked or HTTP/2 without the header): pooled reads, refused past maxBytes (null).
    private static async ValueTask<byte[]?> ReadChunkedAsync(Stream body, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }

    private static PayloadTooLarge TooLarge(int maxBytes) => new(maxBytes >= 1024 * 1024
        ? $"the body is at most {maxBytes / 1024 / 1024} MiB"
        : $"the body is at most {maxBytes / 1024} KiB");

    /// <summary>Reads a document body: one JSON object within the document limits.</summary>
    public static ValueTask<BodyResult> CheckDocumentAsync(HttpContext context, CancellationToken cancellationToken) =>
        CheckBodyAsync(context, RequestJson.MaxDocumentBytes, "invalid_request", cancellationToken, JsonMediaType);
}
