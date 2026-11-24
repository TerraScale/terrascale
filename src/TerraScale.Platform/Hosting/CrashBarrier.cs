using System.Diagnostics;
using ZLogger;

namespace TerraScale.Platform.Hosting;

public sealed class CrashBarrier(RequestDelegate next, TerraBaseClient terraBase, ILogger<CrashBarrier> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (DatabaseTarget(context.Request.Path.Value) is { } target)
            {
                var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                await terraBase.RecordRequestLogAsync(
                    new InternalLogPayload(target.Project, target.Database, context.TraceIdentifier, context.Request.Method, target.Path, 499, elapsedMs),
                    CancellationToken.None);
            }

            context.Abort();
        }
        catch (BadHttpRequestException rejected) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            await Rejected(rejected).ExecuteAsync(context);
        }
        catch (Exception error)
        {
            logger.ZLogError(error, $"Unhandled failure serving {context.Request.Method} {context.Request.Path}");
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                await ErrorResults.Internal().ExecuteAsync(context);
            }
        }
    }

    private static ErrorResult Rejected(BadHttpRequestException rejected) => rejected.StatusCode == StatusCodes.Status413PayloadTooLarge
        ? ErrorResults.From(new PlatformPayloadTooLarge("the request body is larger than this route accepts"))
        : ErrorResults.From(new PlatformInvalidRequest("the request could not be read"));

    private static (string Project, string Database, string Path)? DatabaseTarget(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is ["v1", "projects", { Length: > 0 } project, "databases", { Length: > 0 } database, ..]
            ? (project, database, path)
            : null;
    }
}
