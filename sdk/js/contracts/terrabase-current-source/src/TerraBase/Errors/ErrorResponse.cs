using TerraBase.Hosting;

namespace TerraBase.Errors;

internal static class ErrorResponse
{
    public static IResult From(Failure error, int? op = null) => error switch
    {
        NotFound e => new Response(StatusCodes.Status404NotFound, e.Code, e.Message, op),
        Conflict e => new Response(StatusCodes.Status409Conflict, e.Code, e.Message, op),
        Validation e => new Response(StatusCodes.Status400BadRequest, e.Code, e.Message, op, e.Details),
        PreconditionFailed e => new Response(StatusCodes.Status412PreconditionFailed, e.Code, e.Message, op),
        Unauthorized e => new Response(StatusCodes.Status401Unauthorized, e.Code, e.Message, op),
        Forbidden e => new Response(StatusCodes.Status403Forbidden, e.Code, e.Message, op),
        QuotaExceeded e => new Response(StatusCodes.Status429TooManyRequests, e.Code, e.Message, op),
        Unavailable e => new Response(StatusCodes.Status503ServiceUnavailable, e.Code, e.Message, op),
        PayloadTooLarge e => new Response(StatusCodes.Status413PayloadTooLarge, e.Code, e.Message, op),
        UnsupportedMediaType e => new Response(StatusCodes.Status415UnsupportedMediaType, e.Code, e.Message, op),
        RequestTimeout e => new Response(StatusCodes.Status408RequestTimeout, e.Code, e.Message, op),
        Gone e => new Response(StatusCodes.Status410Gone, e.Code, e.Message, op),
        OutcomeUnknown e => new Response(StatusCodes.Status503ServiceUnavailable, e.Code, e.Message, op, region: e.Region, machine: e.Machine),
    };

    private sealed class Response(
        int status, string code, string? message, int? op, IReadOnlyList<ValidationDetail>? details = null, bool retryAfter = false, string? region = null,
        string? machine = null) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            if (retryAfter)
            {
                httpContext.Response.Headers.RetryAfter = "1";
            }

            return BadRequests.WriteAsync(httpContext, status, code, message, op, details, region, machine);
        }
    }
}
