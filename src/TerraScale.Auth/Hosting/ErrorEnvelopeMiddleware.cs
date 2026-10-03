using TerraScale.Auth.Contracts;

namespace TerraScale.Auth.Hosting;

/// <summary>
/// Turns <see cref="ApiException"/> into its JSON envelope and any other unhandled exception into
/// the generic 500 envelope, so internal details never reach the client.
/// </summary>
public sealed partial class ErrorEnvelopeMiddleware(RequestDelegate next, ILogger<ErrorEnvelopeMiddleware> logger)
{
    /// <summary>Runs the rest of the pipeline.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to answer.
        }
        catch (ApiException error) when (!context.Response.HasStarted && error.Status < 500)
        {
            await WriteAsync(context, error);
        }
        catch (ApiException error) when (!context.Response.HasStarted)
        {
            Diagnose(context, error, () => LogServerError(logger, error.Code, error));
            await WriteAsync(context, error);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            Diagnose(context, exception, () => LogUnhandled(logger, exception));
            await WriteAsync(context, ApiException.Internal());
        }
        catch (Exception exception)
        {
            // The response is already committed, so no envelope can be written. Report it
            // here (rather than letting Kestrel log it) and cut the connection so the client
            // never mistakes a truncated body for a complete one.
            try
            {
                Diagnose(context, exception, () => LogAfterResponseStarted(logger, exception));
            }
            finally
            {
                context.Abort();
            }
        }
    }

    // GlitchTip gets the original exception for its stack frames; Telemetry.Scrub reduces the
    // event to types and frames before sending. Log providers only ever see a ScrubbedException
    // (SafeLoggerFactory). The hub is this application's own and exists only when a DSN is set.
    // Diagnostics are best effort: a failing reporter or log provider must never stop the
    // error response (or the abort) from happening.
    private static void Diagnose(HttpContext context, Exception exception, Action log)
    {
        try
        {
            context.RequestServices.GetService<Sentry.IHub>()?.CaptureException(exception);
        }
        catch (Exception reportFailure) when (reportFailure is not OutOfMemoryException)
        {
            _ = reportFailure;
        }

        try
        {
            log();
        }
        catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
        {
            _ = logFailure;
        }
    }

    private static async Task WriteAsync(HttpContext context, ApiException error)
    {
        try
        {
            context.Response.Clear();
            context.Response.StatusCode = error.Status;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(error.ToBody(), context.RequestAborted);
        }
        catch (Exception writeFailure) when (writeFailure is not OutOfMemoryException)
        {
            // The client went away or the transport failed mid-envelope; nothing more can
            // be said on this connection.
            _ = writeFailure;
            context.Abort();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Request failed with {Code}")]
    private static partial void LogServerError(ILogger logger, string code, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Request failed after the response started; connection aborted")]
    private static partial void LogAfterResponseStarted(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled request exception")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
