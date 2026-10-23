using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Tenancy;

namespace TerraBase.Features.Hrana;

/// <summary>Request handling shared by the Hrana-over-HTTP endpoints.</summary>
internal static class HranaRoute
{
    /// <summary>Base route; <c>{version}</c> is <c>v2</c> or <c>v3</c>.</summary>
    public const string Base = DocumentRoute.DatabaseRoute + "/libsql/{version}";

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
            Validation error => (object)ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
        if (database is not TenantContext found)
        {
            return (null, null, (IResult)database);
        }

        return await DocumentRoute.CheckBodyAsync(context, MaxRequestBytes, "invalid_request", cancellationToken).ConfigureAwait(false) switch
        {
            Validation error => (null, null, ErrorResponse.From(error)),
            PayloadTooLarge error => (null, null, ErrorResponse.From(error)),
            UnsupportedMediaType error => (null, null, ErrorResponse.From(error)),
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
}
