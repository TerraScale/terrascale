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
        Validation e => new Response(StatusCodes.Status400BadRequest, e.Code, e.Message, op, e.Details),
        PreconditionFailed e => new Response(StatusCodes.Status412PreconditionFailed, e.Code, e.Message, op),
        Unauthorized e => new Response(StatusCodes.Status401Unauthorized, e.Code, e.Message, op),
        Forbidden e => new Response(StatusCodes.Status403Forbidden, e.Code, e.Message, op),
        QuotaExceeded e => new Response(StatusCodes.Status429TooManyRequests, e.Code, e.Message, op),
        // A write refused before execution because no other region can confirm it now (decision 0013a), or a shape key
        // withheld while this machine's catalog is unproven (catalog_stale): retrying is safe.
        Unavailable { Code: "remote_region_unavailable" or "confirmation_busy" or "catalog_stale" } e => new Response(StatusCodes.Status503ServiceUnavailable, e.Code, e.Message, op, retryAfter: true),
        Unavailable e => new Response(StatusCodes.Status503ServiceUnavailable, e.Code, e.Message, op),
        PayloadTooLarge e => new Response(StatusCodes.Status413PayloadTooLarge, e.Code, e.Message, op),
        UnsupportedMediaType e => new Response(StatusCodes.Status415UnsupportedMediaType, e.Code, e.Message, op),
        RequestTimeout e => new Response(StatusCodes.Status408RequestTimeout, e.Code, e.Message, op),
        Gone e => new Response(StatusCodes.Status410Gone, e.Code, e.Message, op),
        // Never Retry-After: the write may have applied.
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
