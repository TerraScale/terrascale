using System.Globalization;

namespace TerraScale.Platform.Features.Shapes;

public static class ShapeHeaders
{
    public const string Handle = "TB-Shape-Handle";
    public const string NextOffset = "TB-Shape-Next-Offset";
    public const string UpToDate = "TB-Shape-Up-To-Date";
    public const string Cursor = "TB-Shape-Cursor";
    public const string Hints = "TB-Shape-Hints";
    public const string HintProtocol = "terrascale.shape.v1";
}

public sealed class ShapeAnswer(ShapeResponse body, long? cursor) : IResult
{
    public ShapeResponse Body => body;

    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var headers = httpContext.Response.Headers;
        headers.Vary = "Authorization";
        headers[ShapeHeaders.Hints] = ShapeHeaders.HintProtocol;
        headers[ShapeHeaders.Handle] = body.Handle;
        headers[ShapeHeaders.NextOffset] = body.NextOffset;
        headers[ShapeHeaders.UpToDate] = body.UpToDate ? "true" : "false";
        if (cursor is { } bucket)
        {
            headers[ShapeHeaders.Cursor] = bucket.ToString(CultureInfo.InvariantCulture);
        }

        return TypedResults.Json(body, ShapesJsonContext.Default.ShapeResponse).ExecuteAsync(httpContext);
    }
}

public sealed class ShapeRedirect(string location, string handle) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var headers = httpContext.Response.Headers;
        headers.Vary = "Authorization";
        headers[ShapeHeaders.Hints] = ShapeHeaders.HintProtocol;
        headers[ShapeHeaders.Handle] = handle;
        return TypedResults.Redirect(location, permanent: false, preserveMethod: true).ExecuteAsync(httpContext);
    }
}

public sealed class ShapeRefetch(string handle, string message) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.Headers[ShapeHeaders.Handle] = handle;
        return TypedResults.Json(
            new ShapeErrorBody(new ShapeErrorDetail("must_refetch", message, handle)),
            ShapesJsonContext.Default.ShapeErrorBody,
            statusCode: StatusCodes.Status409Conflict).ExecuteAsync(httpContext);
    }
}

public sealed class ShapeSocketClosed : IResult
{
    public static ShapeSocketClosed Instance { get; } = new();

    public Task ExecuteAsync(HttpContext httpContext) => Task.CompletedTask;
}

public readonly union ShapeReply(ShapeAnswer, ShapeRedirect, ShapeRefetch, PlatformInvalidRequest, PlatformUnauthorized, PlatformForbidden, PlatformNotFound, PlatformUnavailable);

public sealed record ResolvedShape(ShapeRecord Shape, DatabaseAccess Access, ShapeQuery Query);
