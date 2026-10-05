using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Errors;

/// <summary>
/// The single mapping from an expected failure to its HTTP response at the endpoint edge: the status for its
/// kind and an <see cref="ErrorBody"/> with its code and message, never cached, like every other error response.
/// </summary>
internal static class ErrorResponse
{
    /// <summary>The response for <paramref name="error"/>.</summary>
    public static IResult From(Failure error) => error switch
    {
        NotFound e => new Response(StatusCodes.Status404NotFound, e.Code, e.Message),
        Conflict e => new Response(StatusCodes.Status409Conflict, e.Code, e.Message),
        Validation e => new Response(StatusCodes.Status400BadRequest, e.Code, e.Message),
        Unauthorized e => new Response(StatusCodes.Status401Unauthorized, e.Code, e.Message),
        Forbidden e => new Response(StatusCodes.Status403Forbidden, e.Code, e.Message),
        QuotaExceeded e => new Response(StatusCodes.Status429TooManyRequests, e.Code, e.Message),
        Unavailable e => new Response(StatusCodes.Status503ServiceUnavailable, e.Code, e.Message),
    };

    private sealed class Response(int status, string code, string? message) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            return BadRequests.WriteAsync(httpContext, status, code, message, op: null);
        }
    }
}
