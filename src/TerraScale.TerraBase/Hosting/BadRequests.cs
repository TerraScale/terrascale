using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Hosting;

/// <summary>An API error: <c>{"error":{"code":…,"message":…,"op":…}}</c> (Documents API v1 errors).</summary>
/// <param name="Error">The error.</param>
public sealed record ErrorBody(ErrorDetail Error);

/// <summary>Error details.</summary>
/// <param name="Code">Stable machine-readable code, such as <c>not_found</c>.</param>
/// <param name="Message">Human-readable rule that failed; never echoes client input.</param>
/// <param name="Op">Index of the failing transaction operation.</param>
public sealed record ErrorDetail(
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Op = null);

/// <summary>A request that fails with an API error; answered by <see cref="BadRequests"/>.</summary>
public sealed class ApiException : Exception
{
    /// <summary>Creates an error.</summary>
    public ApiException(int statusCode, string code, string? message = null)
        : base(message ?? code)
    {
        StatusCode = statusCode;
        Code = code;
        Detail = message;
    }

    /// <summary>Creates an <c>invalid_request</c> error.</summary>
    public ApiException()
        : this(StatusCodes.Status400BadRequest, "invalid_request")
    {
    }

    /// <summary>Creates an <c>invalid_request</c> error.</summary>
    public ApiException(string message)
        : this(StatusCodes.Status400BadRequest, "invalid_request", message)
    {
    }

    /// <summary>Creates an <c>invalid_request</c> error.</summary>
    public ApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = StatusCodes.Status400BadRequest;
        Code = "invalid_request";
        Detail = message;
    }

    /// <summary>HTTP status.</summary>
    public int StatusCode { get; }

    /// <summary>Error code.</summary>
    public string Code { get; }

    /// <summary>Optional message.</summary>
    public string? Detail { get; }

    /// <summary>Index of the failing transaction operation, when the request was a transaction.</summary>
    public int? Op { get; set; }
}

/// <summary>
/// Answers <see cref="ApiException"/> and <see cref="BadHttpRequestException"/> with an
/// <see cref="ErrorBody"/> instead of logging them as server errors, logs any other exception and
/// answers it with <c>500 internal_error</c>, and gives every other error status without a body
/// (authentication, authorization, unknown routes) an <see cref="ErrorBody"/> too.
/// </summary>
public static partial class BadRequests
{
    /// <summary>Adds the middleware; register it before the endpoints.</summary>
    public static WebApplication UseTerraBaseBadRequests(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseStatusCodePages(context =>
        {
            var status = context.HttpContext.Response.StatusCode;
            return WriteAsync(context.HttpContext, status, CodeFor(status), message: null, op: null);
        });
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (ApiException error) when (!context.Response.HasStarted)
            {
                await WriteAsync(context, error.StatusCode, error.Code, error.Detail, error.Op).ConfigureAwait(false);
            }
            catch (BadHttpRequestException error) when (!context.Response.HasStarted)
            {
                // The message describes the rule that failed and never echoes client input.
                await WriteAsync(context, error.StatusCode, CodeFor(error.StatusCode), error.Message, op: null).ConfigureAwait(false);
            }
            catch (Exception error) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested
                && error is not OperationCanceledException)
            {
                // A server fault: log it, and answer without its details.
                LogUnhandled(
                    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BadRequests)),
                    error, context.Request.Method, context.GetEndpoint()?.DisplayName);
                await WriteAsync(context, StatusCodes.Status500InternalServerError, "internal_error", message: null, op: null).ConfigureAwait(false);
            }
        });
        return app;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error serving {Method} {Endpoint}")]
    private static partial void LogUnhandled(ILogger logger, Exception error, string method, string? endpoint);

    private static string CodeFor(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => "invalid_key",
        StatusCodes.Status403Forbidden => "forbidden_scope",
        StatusCodes.Status404NotFound => "unknown_route",
        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
        StatusCodes.Status413PayloadTooLarge => "payload_too_large",
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        >= 500 => "internal_error",
        _ => "invalid_request",
    };

    private static Task WriteAsync(HttpContext context, int status, string code, string? message, int? op)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(
            context.Response.Body, new ErrorBody(new ErrorDetail(code, message, op)), TerraBaseJsonContext.Default.ErrorBody, context.RequestAborted);
    }
}
