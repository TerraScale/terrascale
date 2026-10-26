using System.Buffers;
using System.Globalization;
using System.Text.Json;
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
    /// <summary>Route of one database: a project of the API key's organization, and its database ULID or current name.</summary>
    public const string DatabaseRoute = "/v1/projects/{project}/databases/{database}";

    /// <summary>Base route of one database's collections.</summary>
    public const string Collections = DatabaseRoute + "/collections";

    /// <summary>Base route of one collection's documents.</summary>
    public const string Collection = Collections + "/{collection}/documents";

    /// <summary>Point operations carry the document's ULID in one query parameter.</summary>
    public const string Document = Collection + "/by-key";

    /// <summary>The request header that makes a mutation wait for another placed region's durable acknowledgement.</summary>
    public const string DurabilityHeader = "TB-Durability";

    /// <summary>The durability barrier and retry semantics shared by single-document mutations.</summary>
    public const string WriteDurabilityDescription = "Mutations answer local_durable once this region's commit is synced to disk; replication to the other placed regions continues in the background. With the header TB-Durability: remote_durable, a mutation in a cell also waits up to 10 seconds for another placed region's durable acknowledgement and answers remote_durable (a cell of one answers local_durable); only then, before execution, 503 remote_region_unavailable or confirmation_busy carries Retry-After, and an ambiguous commit or missing acknowledgement answers 503 write_outcome_unknown with region and machine, without Retry-After; never replay it automatically. Any other TB-Durability value answers 400. A database placed in its home region alone answers 409 placement_needs_two_regions on either path until a placement update adds a region.";

    /// <summary>Largest request body of any other route.</summary>
    public const int MaxRequestBytes = 4 * 1024 * 1024;

    /// <summary>Bounds the JSON one page materializes; a page always holds at least one document.</summary>
    public const long MaxPageBytes = 8 * 1024 * 1024;

    public const string JsonMediaType = "application/json";

    /// <summary>The one answer for a database that does not exist, or that the caller's tenant does not reach.</summary>
    public static readonly NotFound UnknownDatabase = new("the database does not exist", "unknown_database");

    public static string Project(HttpContext context) => (string)context.Request.RouteValues["project"]!;

    /// <summary>The route's database ULID or current name, which the tenant check already validated.</summary>
    public static string Database(HttpContext context) => (string)context.Request.RouteValues["database"]!;

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
    public static async ValueTask<TenantResult> CheckTenantAsync(HttpContext context, ITenantDirectory tenants, CancellationToken cancellationToken)
    {
        var database = context.Request.RouteValues["database"] as string;
        if (database is not { } name || !EntityIds.IsValid(name) && !DatabaseName.TryParse(name, out _))
        {
            return new Validation("database must be a canonical uppercase ULID or lowercase name of at most 63 characters");
        }

        return await ResolveAsync(context, tenants, name, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<TenantResult> ResolveAsync(HttpContext context, ITenantDirectory tenants, string name, CancellationToken cancellationToken)
    {
        if (!EntityIds.IsValid(Project(context)))
        {
            return new Validation("project must be a canonical uppercase ULID");
        }

        if (context.Features.Get<ProjectAccess>() is { } access)
        {
            var resolved = await tenants.FindDatabaseAsync(access.OrganizationId, access.ProjectId, name, cancellationToken).ConfigureAwait(false);
            if (resolved is not TenantContext database
                || (access.DatabaseName is not null && !string.Equals(access.DatabaseId, database.DatabaseId, StringComparison.Ordinal)))
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
        var found = await tenants.FindDatabaseAsync(key.OrganizationId, Project(context), name, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The route's single <c>id</c> query parameter, taken verbatim: a ULID needs no query encoding.</summary>
    public static Valid<string> CheckDocumentParameter(HttpContext context)
    {
        string? id = null;
        foreach (var parameter in new QueryStringEnumerable(context.Request.QueryString.Value))
        {
            if (!parameter.EncodedName.Span.SequenceEqual("id"))
            {
                continue;
            }

            if (id is not null)
            {
                return new Validation("id is required exactly once");
            }

            id = parameter.EncodedValue.ToString();
        }

        return id is null ? new Validation("id is required exactly once") : CheckDocumentId(id);
    }

    /// <summary>
    /// A document id, or the rule it breaks: exactly the canonical form, so one document has exactly one id.
    /// </summary>
    public static Valid<string> CheckDocumentId(string? id) =>
        EntityIds.IsValid(id)
            ? id
            : new Validation("id must be a canonical ULID: 26 uppercase Crockford base32 characters", "invalid_document_id");

    /// <summary>
    /// A stored document id carried back in a cursor: a ULID, or a SQL table's primary key value (1 to 256 characters
    /// without control characters), which the Documents API never accepts as input.
    /// </summary>
    public static Valid<string> CheckStoredId(string? id) =>
        id is { Length: > 0 and <= 256 } && !id.Any(char.IsControl)
            ? id
            : new Validation("id must be 1 to 256 characters without control characters");

    /// <summary>A document URI built from escaped route values, escaping caller-supplied keys.</summary>
    public static string Location(HttpContext context, DocumentKey key) =>
        $"/v1/projects/{Uri.EscapeDataString(Project(context))}/databases/{Uri.EscapeDataString(Database(context))}/collections/{Uri.EscapeDataString(key.Collection)}/documents/by-key?id={Uri.EscapeDataString(key.Id)}";

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

    /// <summary>Whether a mutation waits for another region (<c>TB-Durability: remote_durable</c>); absent or <c>local_durable</c> does not.</summary>
    public static Valid<bool> CheckDurability(HttpContext context) =>
        context.Request.Headers[DurabilityHeader].ToString() switch
        {
            "" or WriteResult.LocalDurable => false,
            WriteResult.RemoteDurable => true,
            _ => new Validation($"{DurabilityHeader} must be {WriteResult.LocalDurable} or {WriteResult.RemoteDurable}"),
        };

    /// <summary>
    /// Applies one write and answers it: <paramref name="answer"/> gets the response body and whether the write created
    /// the document, with the new version as the ETag; a refused write is its error response.
    /// </summary>
    public static async ValueTask<IResult> WriteAsync(
        HttpContext context, IDocumentService documents, DocumentKey key, DocumentOperation operation, Func<WriteBody, bool, IResult> answer,
        CancellationToken cancellationToken) =>
        CheckDurability(context) switch
        {
            Validation error => ErrorResponse.From(error),
            bool waitForRemote => await documents.WriteConfirmedAsync(key.Database, [operation], indexErrors: false, waitForRemote, cancellationToken)
                .ConfigureAwait(false) switch
            {
                DocumentRejected rejected => ErrorResponse.From(rejected.Error),
                WriteResult written => Written(context, key, written, answer),
            },
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
