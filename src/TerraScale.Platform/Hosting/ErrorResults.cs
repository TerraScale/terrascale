using TerraScale.Contracts;

namespace TerraScale.Platform.Hosting;

public sealed class ErrorResult(int status, string code, string message, IReadOnlyDictionary<string, string>? details, bool retryAfter) : IResult
{
    public int StatusCode => status;

    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var response = httpContext.Response;
        response.StatusCode = status;
        response.Headers.CacheControl = "no-store";
        if (retryAfter)
        {
            response.Headers.RetryAfter = "1";
        }

        response.ContentType = "application/json";
        var body = new ApiErrorEnvelope(new ApiErrorBody(code, message, null, null, details));
        return JsonSerializer.SerializeAsync(response.Body, body, TerraScaleContractsJsonContext.Default.ApiErrorEnvelope, httpContext.RequestAborted);
    }
}

public static class ErrorResults
{
    public static ErrorResult From(PlatformInvalidRequest error) => new(StatusCodes.Status400BadRequest, error.Code, error.Message, null, false);

    public static ErrorResult From(PlatformNotFound error) =>
        new(StatusCodes.Status404NotFound, error.Code, error.Message ?? "the resource does not exist", null, false);

    public static ErrorResult From(PlatformConflict error) => new(StatusCodes.Status409Conflict, error.Code, error.Message, null, false);

    public static ErrorResult From(PlatformUnauthorized error) =>
        new(StatusCodes.Status401Unauthorized, error.Code, error.Message ?? "a valid credential is required", null, false);

    public static ErrorResult From(PlatformForbidden error) => new(StatusCodes.Status403Forbidden, error.Code, error.Message, error.Details, false);

    public static ErrorResult From(PlatformQuotaExceeded error) => new(StatusCodes.Status429TooManyRequests, error.Code, error.Message, null, true);

    public static ErrorResult From(PlatformUnavailable error) => new(StatusCodes.Status503ServiceUnavailable, error.Code, error.Message, error.Details, true);

    public static ErrorResult From(PlatformPayloadTooLarge error) => new(StatusCodes.Status413PayloadTooLarge, error.Code, error.Message, null, false);

    public static ErrorResult Internal() =>
        new(StatusCodes.Status500InternalServerError, "internal_error", "the platform failed to complete the request", null, false);
}
