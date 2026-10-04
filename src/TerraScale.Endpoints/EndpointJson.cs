using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace TerraScale.Endpoints;

/// <summary>
/// Request binding and response writing through the application's configured JSON contracts.
/// Type metadata is resolved once at mapping time, so a native AOT app missing a type in its
/// <c>JsonSerializerContext</c> fails at startup instead of on the first request.
/// </summary>
internal static class EndpointJson
{
    internal static JsonTypeInfo<TRequest> GetRequestTypeInfo<TRequest>(IServiceProvider services) =>
        GetSerializerOptions(services).GetTypeInfo<TRequest>();

    internal static JsonTypeInfo<TResponse>? GetResponseTypeInfo<TResponse>(IServiceProvider services) =>
        typeof(IResult).IsAssignableFrom(typeof(TResponse)) || typeof(TResponse) == typeof(string)
            ? null
            : GetSerializerOptions(services).GetTypeInfo<TResponse>();

    internal static async ValueTask<TRequest> ReadBodyAsync<TRequest>(
        HttpContext context,
        JsonTypeInfo<TRequest> typeInfo,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType())
        {
            throw new BadHttpRequestException("Expected a JSON request body.", StatusCodes.Status415UnsupportedMediaType);
        }

        return await ReadJsonAsync(context, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the body once the JSON content type is known; parsing it again allocates.</summary>
    internal static async ValueTask<TRequest> ReadJsonAsync<TRequest>(
        HttpContext context,
        JsonTypeInfo<TRequest> typeInfo,
        CancellationToken cancellationToken)
    {
        TRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new BadHttpRequestException("The JSON request body is invalid.", StatusCodes.Status400BadRequest, exception);
        }
        catch (InvalidOperationException exception) when (GetEncoding(context.Request) is null)
        {
            // ReadFromJsonAsync throws InvalidOperationException for an unknown charset; the filter
            // only runs on that failure, so valid requests do not parse the content type again.
            throw UnsupportedCharset(exception);
        }

        return request ?? throw new BadHttpRequestException("A JSON request body is required.", StatusCodes.Status400BadRequest);
    }

    /// <summary>The body encoding, UTF-8 when no charset is given, or null for an unknown charset.</summary>
    internal static Encoding? GetEncoding(HttpRequest request) =>
        !MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) || !mediaType.Charset.HasValue
            ? Encoding.UTF8
            : mediaType.Encoding;

    internal static BadHttpRequestException UnsupportedCharset() =>
        new("The request charset is not supported.", StatusCodes.Status415UnsupportedMediaType);

    internal static BadHttpRequestException UnsupportedCharset(Exception inner) =>
        new("The request charset is not supported.", StatusCodes.Status415UnsupportedMediaType, inner);

    // Completed handlers write without an async state machine, like generated minimal API code.
    internal static Task RespondAsync<TResponse>(HttpContext context, ValueTask<TResponse> handled, JsonTypeInfo<TResponse>? typeInfo) =>
        handled.IsCompletedSuccessfully ? WriteResponseAsync(context, handled.Result, typeInfo) : AwaitAsync(context, handled, typeInfo);

    private static async Task AwaitAsync<TResponse>(HttpContext context, ValueTask<TResponse> handled, JsonTypeInfo<TResponse>? typeInfo) =>
        await WriteResponseAsync(context, await handled.ConfigureAwait(false), typeInfo).ConfigureAwait(false);

    // No cancellation token on writes: a cancellable token sends WriteAsJsonAsync down its slower
    // linked-token path, and writes to an aborted connection fail regardless.
    private static Task WriteResponseAsync<TResponse>(HttpContext context, TResponse response, JsonTypeInfo<TResponse>? typeInfo) =>
        response switch
        {
            IResult result => result.ExecuteAsync(context),
            string text => WriteTextAsync(context, text),
            null when typeInfo is null => throw new InvalidOperationException("An endpoint returning IResult or string must not return null."),
            _ => context.Response.WriteAsJsonAsync(response, typeInfo!, contentType: null),
        };

    private static Task WriteTextAsync(HttpContext context, string text)
    {
        context.Response.ContentType ??= "text/plain; charset=utf-8";
        return context.Response.WriteAsync(text);
    }

    private static JsonSerializerOptions GetSerializerOptions(IServiceProvider services) =>
        services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
}

/// <summary>
/// Problem responses (binding errors, validation, <c>Send.ProblemAsync</c>) serialize through the
/// app's JSON options. This context is appended to the resolver chain so they work in native AOT.
/// </summary>
[JsonSerializable(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
internal sealed partial class ProblemJsonContext : JsonSerializerContext;
