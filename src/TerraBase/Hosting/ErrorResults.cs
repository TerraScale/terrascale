using System.Globalization;
using TerraBase.Features.Sql;
using TerraScale.Contracts;
using TerraScale.Contracts.Http;
using TerraScale.Endpoints;

namespace TerraBase.Hosting;

internal static class ErrorResults
{
    public static IResult Json(int status, string code, string? message, IReadOnlyDictionary<string, string>? details = null, TimeSpan? retryAfter = null) =>
        new ErrorResult(status, Body(status, code, message, details), retryAfter);

    public static ApiErrorBody Body(int status, string code, string? message, IReadOnlyDictionary<string, string>? details = null) =>
        new(code, message ?? DefaultMessage(status), null, null, details);

    public static IResult Sql(int status, ApiErrorBody body, TimeSpan? retryAfter = null) => new ErrorResult(status, body, retryAfter);

    public static Task WriteAsync(HttpContext context, int status, string code, string message) =>
        Json(status, code, message).ExecuteAsync(context);

    extension(EndpointOptions endpoints)
    {
        public EndpointOptions MapTerraBaseErrors()
        {
            endpoints.MapResponse<SqlRefusal>(static refusal => Sql(refusal.Status, refusal.Body, refusal.RetryAfter), StatusCodes.Status400BadRequest, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<Done>(static _ => TypedResults.NoContent(), StatusCodes.Status204NoContent);
            endpoints.MapResponse<NotFound>(static error => Json(StatusCodes.Status404NotFound, error.Code, error.Message), StatusCodes.Status404NotFound, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<Conflict>(static error => Json(StatusCodes.Status409Conflict, error.Code, error.Message), StatusCodes.Status409Conflict, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<Validation>(static error => Json(StatusCodes.Status400BadRequest, error.Code, error.Message, Details(error.Details)), StatusCodes.Status400BadRequest, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<PreconditionFailed>(static error => Json(StatusCodes.Status412PreconditionFailed, error.Code, error.Message), StatusCodes.Status412PreconditionFailed, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<Unauthorized>(static error => Json(StatusCodes.Status401Unauthorized, error.Code, error.Message), StatusCodes.Status401Unauthorized, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<Forbidden>(static error => Json(StatusCodes.Status403Forbidden, error.Code, error.Message, error.Details), StatusCodes.Status403Forbidden, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<QuotaExceeded>(static error => Json(StatusCodes.Status429TooManyRequests, error.Code, error.Message), StatusCodes.Status429TooManyRequests, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<RateLimited>(static error => Json(StatusCodes.Status429TooManyRequests, error.Code, error.Message, retryAfter: error.RetryAfter ?? TimeSpan.FromSeconds(1)), StatusCodes.Status429TooManyRequests, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<Unavailable>(static error => Json(StatusCodes.Status503ServiceUnavailable, error.Code, error.Message, error.Details, TimeSpan.FromSeconds(1)), StatusCodes.Status503ServiceUnavailable, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<PayloadTooLarge>(static error => Json(StatusCodes.Status413PayloadTooLarge, error.Code, error.Message), StatusCodes.Status413PayloadTooLarge, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<UnsupportedMediaType>(static error => Json(StatusCodes.Status415UnsupportedMediaType, error.Code, error.Message), StatusCodes.Status415UnsupportedMediaType, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<RequestTimeout>(static error => Json(StatusCodes.Status504GatewayTimeout, error.Code, error.Message), StatusCodes.Status504GatewayTimeout, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<Gone>(static error => Json(StatusCodes.Status410Gone, error.Code, error.Message), StatusCodes.Status410Gone, typeof(ApiErrorEnvelope));
            return endpoints;
        }
    }

    private static Dictionary<string, string>? Details(IReadOnlyList<ValidationDetail>? details) =>
        details is { Count: > 0 } ? details.ToDictionary(static detail => detail.Path, static detail => detail.Keyword, StringComparer.Ordinal) : null;

    private static string DefaultMessage(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => "the credential is missing, unknown or expired",
        StatusCodes.Status404NotFound => "the resource does not exist",
        _ => "the request was refused",
    };

    private sealed class ErrorResult(int status, ApiErrorBody body, TimeSpan? retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = status;
            httpContext.Response.Headers.CacheControl = "no-store";
            if (retryAfter is { } delay)
            {
                httpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }

            return httpContext.Response.WriteAsJsonAsync(new ApiErrorEnvelope(body), TerraScaleContractsJsonContext.Default.ApiErrorEnvelope);
        }
    }
}
