using System.Globalization;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet.TerraBase;
using ZLogger;

namespace TerraScale.Authentication.Hosting;

public sealed class ErrorEnvelopeMiddleware(RequestDelegate next, AuthSchema schema, ILogger<ErrorEnvelopeMiddleware> logger)
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
        }
        catch (ApiException error) when (!context.Response.HasStarted && error.Status < 500)
        {
            await WriteAsync(context, error.Status, error.Code, error.Message, error.RetryAfter);
        }
        catch (ApiException failure) when (!context.Response.HasStarted)
        {
            var error = failure is { Status: StatusCodes.Status503ServiceUnavailable, InnerException: { } cause } && AuthData.IsOutcomeUnknown(cause)
                ? AuthData.OutcomeUnknown(cause)
                : failure;
            var level = error.Code == KeyRing.NoSigningKeysCode && schema.BootstrapPending ? LogLevel.Information : LogLevel.Error;
            Diagnose(context, error, () => logger.ZLog(level, error, $"Request failed with {error.Code:@Code}"));
            await WriteAsync(context, error.Status, error.Code, error.Message, error.RetryAfter);
        }
        catch (TerraBaseException conflict) when (!context.Response.HasStarted && AuthData.IsConflict(conflict))
        {
            var error = new ApiException(409, "conflict", "A concurrent change won; retry the request.", conflict);
            Diagnose(context, error, () => logger.ZLogWarning(error, $"Request failed with {error.Code:@Code}"));
            await WriteAsync(context, error.Status, error.Code, error.Message, error.RetryAfter);
        }
        catch (OpenIddictExceptions.ConcurrencyException conflict) when (!context.Response.HasStarted)
        {
            var error = new ApiException(409, "conflict", "A concurrent change won; retry the request.", conflict);
            Diagnose(context, error, () => logger.ZLogWarning(error, $"Request failed with {error.Code:@Code}"));
            await WriteAsync(context, error.Status, error.Code, error.Message, error.RetryAfter);
        }
        catch (Exception busy) when (!context.Response.HasStarted && busy is not ApiException && AuthData.IsUnavailable(busy))
        {
            var error = AuthData.Unavailable(busy);
            Diagnose(context, error, () => logger.ZLogError(error, $"Request failed with {error.Code:@Code}"));
            await WriteAsync(context, error.Status, error.Code, error.Message, error.RetryAfter);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            Diagnose(context, exception, () => logger.ZLogError(exception, $"Unhandled request exception"));
            await WriteAsync(context, ApiException.Internal());
        }
        catch (Exception exception)
        {
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

    private static Task WriteAsync(HttpContext context, ApiException error) =>
        WriteAsync(context, error.Status, error.Code, error.Message, error.RetryAfter);

    internal static Task WriteAsync(HttpContext context, int status, string code, string message, TimeSpan? retryAfter)
    {
        try
        {
            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.Headers.CacheControl = "no-store";
            if (retryAfter is { } wait)
            {
                context.Response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }

            if (status >= 500 && SignInFlow.HostedPage(context))
            {
                return SignInFlow.Transient(context).ExecuteAsync(context);
            }

            return context.Response.WriteAsJsonAsync(new ErrorBody(new ErrorDetail(code, message)), context.RequestAborted);
        }
        catch (Exception writeFailure) when (writeFailure is not OutOfMemoryException)
        {
            _ = writeFailure;
            context.Abort();
            return Task.CompletedTask;
        }
    }
}
