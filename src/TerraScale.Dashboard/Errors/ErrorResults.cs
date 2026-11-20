using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Errors;

internal static class ErrorResults
{
    public static IResult ToResult(HttpContext http, Failure failure) => failure switch
    {
        NotFound e => RequestError.Response(http, StatusCodes.Status404NotFound, e.Message ?? "Not found."),
        Conflict e => RequestError.Response(http, StatusCodes.Status409Conflict, e.Message),
        Validation e => RequestError.Response(http, StatusCodes.Status400BadRequest, e.Message),
        PreconditionFailed e => RequestError.Response(http, StatusCodes.Status412PreconditionFailed, e.Message),
        Unauthorized e => RequestError.Response(http, StatusCodes.Status401Unauthorized, e.Message ?? "Unauthorized."),
        Forbidden e => RequestError.Response(http, StatusCodes.Status403Forbidden, e.Message),
        QuotaExceeded e => RequestError.Response(http, StatusCodes.Status429TooManyRequests, e.Message),
        Unavailable e => Result(http, StatusCodes.Status503ServiceUnavailable, e.Message, e.Details),
        PayloadTooLarge e => RequestError.Response(http, StatusCodes.Status413PayloadTooLarge, e.Message),
        UnsupportedMediaType e => RequestError.Response(http, StatusCodes.Status415UnsupportedMediaType, e.Message),
        RequestTimeout e => RequestError.Response(http, StatusCodes.Status408RequestTimeout, e.Message),
        Gone e => RequestError.Response(http, StatusCodes.Status410Gone, e.Message),
        OutcomeUnknown e => RequestError.Response(http, StatusCodes.Status503ServiceUnavailable, e.Message),
    };

    private static IResult Result(HttpContext http, int status, string message, IReadOnlyDictionary<string, string>? details) =>
        details is not null && details.ContainsKey("retry_after")
            ? new RetryAfterResult(RequestError.Response(http, status, message))
            : RequestError.Response(http, status, message);

    private sealed class RetryAfterResult(IResult inner) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            http.Response.Clear();
            http.Response.Headers.RetryAfter = "1";
            await inner.ExecuteAsync(http);
        }
    }
}
