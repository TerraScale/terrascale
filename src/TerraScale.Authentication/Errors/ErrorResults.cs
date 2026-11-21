using System.Globalization;
using TerraScale.Authentication.Hosting;

namespace TerraScale.Authentication.Errors;

internal static class ErrorResults
{
    public static IResult ToResult(Failure failure) => failure switch
    {
        NotFound e => Result(StatusCodes.Status404NotFound, e.Message ?? "Not found.", e.Code),
        Conflict e => Result(StatusCodes.Status409Conflict, e.Message, e.Code),
        Validation e => Result(StatusCodes.Status400BadRequest, e.Message, e.Code),
        Unprocessable e => Result(StatusCodes.Status422UnprocessableEntity, e.Message, e.Code),
        PreconditionFailed e => Result(StatusCodes.Status412PreconditionFailed, e.Message, e.Code),
        Unauthorized e => Result(StatusCodes.Status401Unauthorized, e.Message ?? "Unauthorized.", e.Code),
        Forbidden e => Result(StatusCodes.Status403Forbidden, e.Message, e.Code),
        QuotaExceeded e => Result(StatusCodes.Status429TooManyRequests, e.Message, e.Code, details: e.Details, retryAfter: e.RetryAfter),
        Unavailable e => Result(StatusCodes.Status503ServiceUnavailable, e.Message, e.Code, e.Details),
        PayloadTooLarge e => Result(StatusCodes.Status413PayloadTooLarge, e.Message, e.Code),
        UnsupportedMediaType e => Result(StatusCodes.Status415UnsupportedMediaType, e.Message, e.Code),
        RequestTimeout e => Result(StatusCodes.Status408RequestTimeout, e.Message, e.Code),
        Gone e => Result(StatusCodes.Status410Gone, e.Message, e.Code),
        OutcomeUnknown e => Result(StatusCodes.Status503ServiceUnavailable, e.Message, e.Code),
    };

    private static EnvelopeResult Result(int status, string message, string Code, IReadOnlyDictionary<string, string>? details = null, TimeSpan? retryAfter = null) =>
        new(status, Code, message, retryAfter ?? RetryAfter(details));

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
