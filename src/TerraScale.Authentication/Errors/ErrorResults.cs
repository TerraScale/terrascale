using System.Globalization;
using TerraScale.Authentication.Hosting;

namespace TerraScale.Authentication.Errors;

internal static class ErrorResults
{
    public static IResult ToResult(AuthNotFound failure) =>
        Result(StatusCodes.Status404NotFound, failure.Message ?? "Not found.", failure.Code);

    public static IResult ToResult(AuthConflict failure) =>
        Result(StatusCodes.Status409Conflict, failure.Message, failure.Code);

    public static IResult ToResult(AuthValidation failure) =>
        Result(StatusCodes.Status400BadRequest, failure.Message, failure.Code);

    public static IResult ToResult(AuthPreconditionFailed failure) =>
        Result(StatusCodes.Status412PreconditionFailed, failure.Message, failure.Code);

    public static IResult ToResult(AuthUnauthorized failure) =>
        Result(StatusCodes.Status401Unauthorized, failure.Message ?? "Unauthorized.", failure.Code);

    public static IResult ToResult(AuthForbidden failure) =>
        Result(StatusCodes.Status403Forbidden, failure.Message, failure.Code);

    public static IResult ToResult(AuthQuotaExceeded failure) =>
        Result(StatusCodes.Status429TooManyRequests, failure.Message, failure.Code);

    public static IResult ToResult(AuthUnavailable failure) =>
        Result(StatusCodes.Status503ServiceUnavailable, failure.Message, failure.Code, failure.Details);

    public static IResult ToResult(AuthPayloadTooLarge failure) =>
        Result(StatusCodes.Status413PayloadTooLarge, failure.Message, failure.Code);

    public static IResult ToResult(AuthUnsupportedMediaType failure) =>
        Result(StatusCodes.Status415UnsupportedMediaType, failure.Message, failure.Code);

    public static IResult ToResult(AuthRequestTimeout failure) =>
        Result(StatusCodes.Status408RequestTimeout, failure.Message, failure.Code);

    public static IResult ToResult(AuthGone failure) =>
        Result(StatusCodes.Status410Gone, failure.Message, failure.Code);

    private static EnvelopeResult Result(int status, string message, string code, IReadOnlyDictionary<string, string>? details = null) =>
        new(status, code, message, RetryAfter(details));

    internal static TimeSpan? RetryAfter(IReadOnlyDictionary<string, string>? details)
    {
        if (details is null || !details.TryGetValue("retry_after", out var wait))
        {
            return null;
        }

        return long.TryParse(wait, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.TryParse(wait, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private sealed class EnvelopeResult(int status, string code, string message, TimeSpan? retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext context) =>
            ErrorEnvelopeMiddleware.WriteAsync(context, status, code, message, retryAfter);
    }
}
