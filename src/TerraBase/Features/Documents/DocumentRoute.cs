using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.UserAuth;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

internal readonly union TenantResult(TenantContext, Validation, NotFound, Unavailable);
internal readonly union KeyResult(DocumentKey, Validation, NotFound, Unavailable);
internal readonly union BodyResult(byte[], Validation, PayloadTooLarge, UnsupportedMediaType);
internal static class DocumentRoute
{
    public const string DatabaseRoute = "/v1/projects/{project}/databases/{database}";
    public const string Collections = DatabaseRoute + "/collections";
    public const string Collection = Collections + "/{collection}/documents";
    public const string Document = Collection + "/by-key";
    public const string DurabilityHeader = "TB-Durability";
    public const string WriteDurabilityDescription = "Mutations answer local_durable after the local commit is synced to disk. An ambiguous commit answers 503 write_outcome_unknown with region and machine; never replay it automatically. Assertions only answer unchanged.";
    public const int MaxRequestBytes = 4 * 1024 * 1024;
    public const long MaxPageBytes = 8 * 1024 * 1024;

    public const string JsonMediaType = "application/json";
    public static readonly NotFound UnknownDatabase = new("the database does not exist", "unknown_database");

    public static string Project(HttpContext context) => (string)context.Request.RouteValues["project"]!;
    public static string Database(HttpContext context) => (string)context.Request.RouteValues["database"]!;
    public static Valid<DatabaseName> CheckDatabase(HttpContext context) =>
        DatabaseName.TryParse(context.Request.RouteValues["database"] as string, out var name)
            ? name
            : new Validation("database must be a lowercase name of at most 63 characters");
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
        var faults = context.RequestServices.GetRequiredService<IStorageCopyFaults>();
        var storage = context.RequestServices.GetRequiredService<IStorageEngine>();
        var incarnation = (await storage.OpenDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false))?.Incarnation;
        if (await faults.RefusedAsync(SystemDatabase.Identity, Ulid.Empty, cancellationToken).ConfigureAwait(false)
            || incarnation is { } pin && await faults.RefusedAsync(tenant, pin, cancellationToken).ConfigureAwait(false))
        {
            throw new StorageCopyFaultedException("this database copy requires storage recovery");
        }

        context.Features.Set(tenant);
        return tenant;
    }
    public static Valid<string> CheckCollectionName(HttpContext context) => CheckCollectionName(context.Request.RouteValues["collection"] as string);
    public static Valid<string> CheckCollectionName(string? name) =>
        !DatabaseName.TryParse(name, out var parsed)
            ? new Validation("collection must be a lowercase name of at most 63 characters")
            : CollectionName.IsReserved(parsed.Value)
                ? new Validation("collection name is reserved", "reserved_name")
                : parsed.Value;
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
    public static Valid<string> CheckDocumentId(string? id) =>
        EntityIds.IsValid(id)
            ? id
            : new Validation("id must be a canonical ULID: 26 uppercase Crockford base32 characters", "invalid_document_id");
    public static Valid<string> CheckStoredId(string? id) =>
        id is { Length: > 0 and <= 256 } && !id.Any(char.IsControl)
            ? id
            : new Validation("id must be 1 to 256 characters without control characters");
    public static string Location(HttpContext context, DocumentKey key) =>
        $"/v1/projects/{Uri.EscapeDataString(Project(context))}/databases/{Uri.EscapeDataString(Database(context))}/collections/{Uri.EscapeDataString(key.Collection)}/documents/by-key?id={Uri.EscapeDataString(key.Id)}";
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
    public static Valid<int> CheckLimit(IQueryCollection query, int fallback, int max) =>
        !query.TryGetValue("limit", out var raw)
            ? fallback
            : int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit is >= 1 && limit <= max
                ? limit
                : new Validation($"limit must be an integer between 1 and {max}");
    public static Valid<Done> CheckIdempotencyKey(HttpContext context) =>
        context.Request.Headers.ContainsKey("Idempotency-Key")
            ? new Validation("Idempotency-Key is not supported yet")
            : new Done();
    public static Valid<Done> CheckDurability(HttpContext context) =>
        context.Request.Headers[DurabilityHeader].ToString() switch
        {
            "" or WriteResult.LocalDurable => new Done(),
            _ => new Validation($"{DurabilityHeader} must be {WriteResult.LocalDurable}"),
        };
    public static async ValueTask<IResult> WriteAsync(
        HttpContext context, IDocumentService documents, DocumentKey key, DocumentOperation operation, Func<WriteBody, bool, IResult> answer,
        CancellationToken cancellationToken) =>
        CheckDurability(context) switch
        {
            Validation error => ErrorResponse.From(error),
            Done => await documents.WriteConfirmedAsync(key.Database, [operation], indexErrors: false, cancellationToken)
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
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = maxBytes;
        }

        var json = context.Request.ContentLength is { } length
            ? await ReadExactlyAsync(context.Request.Body, (int)length, cancellationToken).ConfigureAwait(false)
            : await ReadChunkedAsync(context.Request.Body, maxBytes, cancellationToken).ConfigureAwait(false);
        return json is null ? TooLarge(maxBytes) : RequestJson.IsText(json) ? json : new Validation(RequestJson.TextRule, code);
    }
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
    public static ValueTask<BodyResult> CheckDocumentAsync(HttpContext context, CancellationToken cancellationToken) =>
        CheckBodyAsync(context, RequestJson.MaxDocumentBytes, "invalid_request", cancellationToken, JsonMediaType);
}
