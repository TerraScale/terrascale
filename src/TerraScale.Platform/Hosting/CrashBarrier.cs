using ZLogger;

namespace TerraScale.Platform.Hosting;

public sealed class CrashBarrier(RequestDelegate next, ILogger<CrashBarrier> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
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
}
