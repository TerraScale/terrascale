using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Engine;
using TerraBase.Errors;
using ZLogger;

namespace TerraBase.Hosting;

/// <summary>An API error: <c>{"error":{"code":…,"message":…,"op":…}}</c> (Documents API v1 errors).</summary>
/// <param name="Error">The error.</param>
public sealed record ErrorBody(ErrorDetail Error);

/// <summary>Error details.</summary>
/// <param name="Code">Stable machine-readable code, such as <c>not_found</c>.</param>
/// <param name="Message">Human-readable rule that failed; never echoes client input.</param>
/// <param name="Op">Index of the failing transaction operation.</param>
/// <param name="Details">Bounded schema validation errors, without document values.</param>
/// <param name="Region">For <c>write_outcome_unknown</c>: the region of the machine that accepted the write.</param>
/// <param name="Machine">For <c>write_outcome_unknown</c>: the machine that accepted the write.</param>
/// <param name="Regions">Authorized database placement for a wrong-region refusal.</param>
/// <param name="Host">The regional HTTP host to use.</param>
/// <param name="Docs">Public recovery documentation.</param>
public sealed record ErrorDetail(
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Op = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ValidationDetail>? Details = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Region = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Machine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Regions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Host = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Docs = null);

/// <summary>
/// Answers framework <see cref="BadHttpRequestException"/> with an
/// <see cref="ErrorBody"/> instead of logging them as server errors, logs any other exception and
/// answers it with <c>500 internal_error</c>, and gives every other error status without a body
/// (authentication, authorization, unknown routes) an <see cref="ErrorBody"/> too.
/// </summary>
public static class BadRequests
{
    /// <summary>Adds the middleware; register it before the endpoints.</summary>
    public static WebApplication UseTerraBaseBadRequests(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseStatusCodePages(context =>
        {
            var status = context.HttpContext.Response.StatusCode;
            // Not cleared: authentication challenges keep their headers. Still never cached (a 404 is
            // heuristically cacheable and would outlive a route that appears later).
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return WriteAsync(context.HttpContext, status, CodeFor(status), message: null, op: null);
        });
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (BadHttpRequestException error) when (!context.Response.HasStarted)
            {
                // The message describes the rule that failed and never echoes client input.
                Replace(context.Response);
                await WriteAsync(context, error.StatusCode, CodeFor(error.StatusCode), error.Message, op: null).ConfigureAwait(false);
            }
            catch (ReplicaCopyFaultedException) when (!context.Response.HasStarted)
            {
                Replace(context.Response);
                await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "copy_faulted", "Storage is awaiting certified recovery.", op: null).ConfigureAwait(false);
            }
            catch (DiskCapacityException error) when (!context.Response.HasStarted)
            {
                Replace(context.Response);
                context.Response.Headers.RetryAfter = "30";
                await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "disk_capacity", error.Message, op: null).ConfigureAwait(false);
            }
            catch (StorageBusyException error) when (!context.Response.HasStarted)
            {
                Replace(context.Response);
                context.Response.Headers.RetryAfter = "1";
                await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "storage_busy", error.Message, op: null).ConfigureAwait(false);
            }
            catch (Exception error) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
            {
                // A server fault: log it, and answer without its details.
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BadRequests)).ZLogError(error, $"Unhandled error serving {context.Request.Method:@Method} {context.GetEndpoint()?.DisplayName:@Endpoint}");
                Replace(context.Response);
                await WriteAsync(context, StatusCodes.Status500InternalServerError, "internal_error", message: null, op: null).ConfigureAwait(false);
            }
        });
        return app;
    }

    private static string CodeFor(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => "invalid_key",
        StatusCodes.Status403Forbidden => "forbidden_scope",
        StatusCodes.Status404NotFound => "unknown_route",
        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
        StatusCodes.Status408RequestTimeout => "request_timeout",
        StatusCodes.Status413PayloadTooLarge => "payload_too_large",
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        StatusCodes.Status421MisdirectedRequest => "misdirected_request",
        StatusCodes.Status503ServiceUnavailable => "unavailable",
        >= 500 => "internal_error",
        _ => "invalid_request",
    };

    // A framework or I/O failure may follow prepared success headers; never let a cache keep the error.
    private static void Replace(HttpResponse response)
    {
        response.Clear();
        response.Headers.CacheControl = "no-store";
    }

    internal static Task WriteAsync(
        HttpContext context, int status, string code, string? message, int? op, IReadOnlyList<ValidationDetail>? details = null, string? region = null, string? machine = null)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(
            context.Response.Body, new ErrorBody(new ErrorDetail(code, message, op, details, region, machine)), TerraBaseJsonContext.Default.ErrorBody, context.RequestAborted);
    }
}
