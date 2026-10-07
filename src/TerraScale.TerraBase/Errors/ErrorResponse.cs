using TerraBase.Hosting;

namespace TerraBase.Errors;

/// <summary>
/// The single mapping from an expected failure to its HTTP response at the endpoint edge: the status for its
/// kind and an <see cref="ErrorBody"/> with its code and message, never cached, like every other error response.
/// </summary>
internal static class ErrorResponse
{
    /// <summary>The response for <paramref name="error"/>; <paramref name="op"/> names the failing operation of a batch.</summary>
    public static IResult From(Failure error, int? op = null) => error switch
    {
        NotFound e => new Response(StatusCodes.Status404NotFound, e.Code, e.Message, op),
        Conflict e => new Response(StatusCodes.Status409Conflict, e.Code, e.Message, op),
        Validation e => new Response(StatusCodes.Status400BadRequest, e.Code, e.Message, op),
        PreconditionFailed e => new Response(StatusCodes.Status412PreconditionFailed, e.Code, e.Message, op),
        Unauthorized e => new Response(StatusCodes.Status401Unauthorized, e.Code, e.Message, op),
        Forbidden e => new Response(StatusCodes.Status403Forbidden, e.Code, e.Message, op),
        QuotaExceeded e => new Response(StatusCodes.Status429TooManyRequests, e.Code, e.Message, op),
        Unavailable e => new Response(StatusCodes.Status503ServiceUnavailable, e.Code, e.Message, op),
        UriTooLong e => new Response(StatusCodes.Status414UriTooLong, e.Code, e.Message, op),
        PayloadTooLarge e => new Response(StatusCodes.Status413PayloadTooLarge, e.Code, e.Message, op),
        UnsupportedMediaType e => new Response(StatusCodes.Status415UnsupportedMediaType, e.Code, e.Message, op),
    };

    private sealed class Response(int status, string code, string? message, int? op) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            return BadRequests.WriteAsync(httpContext, status, code, message, op);
        }
    }
}
