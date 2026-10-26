using System.Globalization;
using TerraScale.Authentication.Contracts;
using ZLogger;

namespace TerraScale.Authentication.Hosting;

/// <summary>
/// Turns <see cref="ApiException"/> into its JSON envelope and any other unhandled exception into
/// the generic 500 envelope, so internal details never reach the client.
/// </summary>
public sealed class ErrorEnvelopeMiddleware(RequestDelegate next, ILogger<ErrorEnvelopeMiddleware> logger)
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
            Diagnose(context, error, () => logger.ZLogError(error, $"Request failed with {error.Code:@Code}"));
            await WriteAsync(context, error);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            Diagnose(context, exception, () => logger.ZLogError(exception, $"Unhandled request exception"));
            await WriteAsync(context, ApiException.Internal());
        }
        catch (Exception exception)
        {
            // The response is already committed, so no envelope can be written. Report it
            // here (rather than letting Kestrel log it) and cut the connection so the client
            // never mistakes a truncated body for a complete one.
            try
            {
                Diagnose(context, exception, () => logger.ZLogError(exception, $"Request failed after the response started; connection aborted"));
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
            if (error.RetryAfter is { } wait)
            {
                context.Response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }

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
}
