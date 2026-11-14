using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Engine;
using TerraBase.Errors;
using ZLogger;

namespace TerraBase.Hosting;

public sealed record ErrorBody(ErrorDetail Error);
public sealed record ErrorDetail(
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Op = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ValidationDetail>? Details = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Region = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Machine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DocsUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? UnavailableFrom = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? UnavailableTo = null);
public static class BadRequests
{
    internal const string UnknownRoute = "unknown_route";

    public static WebApplication UseTerraBaseBadRequests(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseStatusCodePages(context =>
        {
            var status = context.HttpContext.Response.StatusCode;
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            var message = status == StatusCodes.Status404NotFound
                ? $"no route matches {context.HttpContext.Request.Method} {context.HttpContext.Request.Path}; see the API reference at /scalar/"
                : null;
            return WriteAsync(context.HttpContext, status, CodeFor(status), message, op: null);
        });
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (BadHttpRequestException error) when (!context.Response.HasStarted)
            {
                Replace(context.Response);
                await WriteAsync(context, error.StatusCode, CodeFor(error.StatusCode), error.Message, op: null).ConfigureAwait(false);
            }
            catch (StorageProofPendingException) when (!context.Response.HasStarted)
            {
                Replace(context.Response);
                context.Response.Headers.RetryAfter = "1";
                await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "recovery_pending", "Storage recovery proof is pending; retry.", op: null).ConfigureAwait(false);
            }
            catch (StorageCopyFaultedException) when (!context.Response.HasStarted)
            {
                Replace(context.Response);
                await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "copy_faulted", "Storage requires recovery.", op: null).ConfigureAwait(false);
            }
            catch (DiskCapacityException error) when (!context.Response.HasStarted)
            {
                Replace(context.Response);
                context.Response.Headers.RetryAfter = "30";
                await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "disk_capacity", error.Message, op: null).ConfigureAwait(false);
            }
            catch (DatabaseMovedException error) when (!context.Response.HasStarted)
            {
                Replace(context.Response);
                context.Response.Headers.RetryAfter = "1";
                await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "move_unresolved", error.Message, op: null).ConfigureAwait(false);
            }
            catch (StorageBusyException error) when (!context.Response.HasStarted)
            {
                Replace(context.Response);
                context.Response.Headers.RetryAfter = "1";
                await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "storage_busy", error.Message, op: null).ConfigureAwait(false);
            }
            catch (Exception error) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
            {
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
        StatusCodes.Status404NotFound => UnknownRoute,
        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
        StatusCodes.Status408RequestTimeout => "request_timeout",
        StatusCodes.Status413PayloadTooLarge => "payload_too_large",
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        StatusCodes.Status421MisdirectedRequest => "misdirected_request",
        StatusCodes.Status503ServiceUnavailable => "unavailable",
        >= 500 => "internal_error",
        _ => "invalid_request",
    };
    private static void Replace(HttpResponse response)
    {
        response.Clear();
        response.Headers.CacheControl = "no-store";
    }

    internal static Task WriteAsync(
        HttpContext context, int status, string code, string? message, int? op, IReadOnlyList<ValidationDetail>? details = null, string? region = null, string? machine = null,
        string? url = null, HistoryGap? gap = null)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json";
        var requestId = context.TraceIdentifier;
        return JsonSerializer.SerializeAsync(
            context.Response.Body, new ErrorBody(new ErrorDetail(code, message, op, details, region, machine, url, requestId, ErrorDocs.Url(code), gap?.From, gap?.To)), TerraBaseJsonContext.Default.ErrorBody, context.RequestAborted);
    }
}
