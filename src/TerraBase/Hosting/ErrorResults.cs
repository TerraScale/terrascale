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
            endpoints.MapResponse<Completed>(static _ => TypedResults.NoContent(), StatusCodes.Status204NoContent);
            endpoints.MapResponse<ResourceNotFound>(static error => Json(StatusCodes.Status404NotFound, error.Code, error.Message), StatusCodes.Status404NotFound, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<StateConflict>(static error => Json(StatusCodes.Status409Conflict, error.Code, error.Message), StatusCodes.Status409Conflict, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<InvalidInput>(static error => Json(StatusCodes.Status400BadRequest, error.Code, error.Message), StatusCodes.Status400BadRequest, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<DatabaseIdMismatch>(static error => Json(StatusCodes.Status412PreconditionFailed, error.Code, error.Message), StatusCodes.Status412PreconditionFailed, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<CredentialRejected>(static error => Json(StatusCodes.Status401Unauthorized, error.Code, error.Message), StatusCodes.Status401Unauthorized, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<CallerForbidden>(static error => Json(StatusCodes.Status403Forbidden, error.Code, error.Message, error.Details), StatusCodes.Status403Forbidden, typeof(ApiErrorEnvelope));
            endpoints.MapResponse<BackendUnavailable>(static error => Json(StatusCodes.Status503ServiceUnavailable, error.Code, error.Message, retryAfter: TimeSpan.FromSeconds(1)), StatusCodes.Status503ServiceUnavailable, typeof(ApiErrorEnvelope));
            return endpoints;
        }
    }

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
