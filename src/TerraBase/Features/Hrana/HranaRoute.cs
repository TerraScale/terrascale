using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Documents;
using TerraBase.Tenancy;

namespace TerraBase.Features.Hrana;

/// <summary>Request handling shared by the Hrana-over-HTTP endpoints.</summary>
internal static class HranaRoute
{
    /// <summary>Base route; <c>{version}</c> is <c>v2</c> or <c>v3</c>.</summary>
    public const string Base = DocumentRoute.DatabaseRoute + "/libsql/{version}";

    /// <summary>Requests per pipeline, statement-free ones included.</summary>
    public const int MaxRequests = 1000;

    /// <summary>Largest request body.</summary>
    public const int MaxRequestBytes = 1024 * 1024;

    /// <summary>libSQL's JS client reads a JSON error body only under exactly this media type.</summary>
    public const string JsonMediaType = "application/json";

    /// <summary>
    /// Wire options: the Rust client deserializes borrowed strings and rejects escapes it does not need, so output uses
    /// relaxed escaping.
    /// </summary>
    private static readonly JsonSerializerOptions Wire = new(HranaJsonContext.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = HranaJsonContext.Default,
    };

    public static JsonTypeInfo<T> Json<T>() => Wire.GetTypeInfo<T>();

    /// <summary>A request-level refusal in the shape libSQL clients raise as an error.</summary>
    public static IResult Refuse(int status, string code, string message) =>
        TypedResults.Json(new HranaErrorBody(message, message, code), Json<HranaErrorBody>(), JsonMediaType, status);

    /// <summary>
    /// The tenant and the parsed body, or the response refusing them. The body needs no Content-Type (the Rust client
    /// sends none).
    /// </summary>
    public static async ValueTask<(TenantContext? Database, T? Body, IResult? Refusal)> ReadAsync<T>(
        HttpContext context, ITenantDirectory tenants, string[] versions, CancellationToken cancellationToken)
        where T : class
    {
        if (!versions.Contains(context.Request.RouteValues["version"] as string, StringComparer.Ordinal))
        {
            return (null, null, Refuse(StatusCodes.Status404NotFound, "NOT_FOUND", "this Hrana version is not served"));
        }

        var database = await DocumentRoute.CheckTenantAsync(context, tenants, cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext tenant => tenant,
            Validation error => (object)Refuse(StatusCodes.Status400BadRequest, error.Code, error.Message),
            NotFound error => Refuse(StatusCodes.Status404NotFound, error.Code, error.Message ?? "not found"),
            Unavailable error => ErrorResponse.From(error),
        };
        if (database is not TenantContext found)
        {
            return (null, null, (IResult)database);
        }

        return await DocumentRoute.CheckBodyAsync(context, MaxRequestBytes, "invalid_request", cancellationToken).ConfigureAwait(false) switch
        {
            Validation error => (null, null, Refuse(StatusCodes.Status400BadRequest, error.Code, error.Message)),
            PayloadTooLarge error => (null, null, Refuse(StatusCodes.Status413PayloadTooLarge, error.Code, error.Message)),
            UnsupportedMediaType error => (null, null, Refuse(StatusCodes.Status415UnsupportedMediaType, error.Code, error.Message)),
            byte[] body => Parse<T>(body) is { } parsed
                ? (found, parsed, null)
                : (null, null, Refuse(StatusCodes.Status400BadRequest, "PROTOCOL_ERROR", "the body is not a valid Hrana request")),
        };
    }

    private static T? Parse<T>(byte[] body)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, Json<T>());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The request's stream: the one parked under the baton for this key and database, else a new one when there is no
    /// baton or the baton's stream held no state (suffix <see cref="HranaStreams.StatelessSuffix"/>), so idle
    /// connections of long-running apps survive expiry. A lost stream with a transaction or stored SQL is refused.
    /// </summary>
    public static (HranaStream? Stream, IResult? Refusal) Acquire(HttpContext context, IHranaStreams streams, TenantContext database, string? baton)
    {
        // A stream belongs to one credential with one write right: the same user's read-only token cannot reuse a writer's stream.
        var canWrite = context.User.HasClaim(ApiKeyAuthenticationHandler.ScopeClaim, ApiKeyScopes.DocumentsWrite);
        var owner = $"{context.User.Identity?.AuthenticationType}\n{context.User.FindFirstValue(ClaimTypes.NameIdentifier)}\n{(canWrite ? "write" : "read")}";
        if (baton is not null)
        {
            // A canonical name can be recreated with a different database identity. A lost stateless stream
            // may recover only on its original identity, even after its issuing node has forgotten the stream.
            if (!HranaStreams.IsForDatabase(baton, database.DatabaseId))
            {
                return (null, Expired());
            }

            if (streams.Take(baton, owner, database) is { } taken)
            {
                return (taken, null);
            }

            if (!baton.EndsWith(HranaStreams.StatelessSuffix, StringComparison.Ordinal))
            {
                return (null, Expired());
            }
        }

        return streams.Open(database, owner, canWrite) switch
        {
            HranaStream opened => (opened, null),
            HranaError refused => (null, RetryLater(context, refused)),
        };
    }

    private static IResult Expired() =>
        Refuse(StatusCodes.Status400BadRequest, "STREAM_EXPIRED", "stream not found: the baton is unknown or expired; open a new stream");

    private static IResult RetryLater(HttpContext context, HranaError refused)
    {
        context.Response.Headers.RetryAfter = "1";
        return Refuse(StatusCodes.Status503ServiceUnavailable, refused.Code, refused.Message);
    }
}
