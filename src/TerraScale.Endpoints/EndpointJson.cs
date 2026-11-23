using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using TerraScale.Endpoints.Binding;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace TerraScale.Endpoints;

internal static class EndpointJson
{
    internal static JsonTypeInfo<TRequest> GetRequestTypeInfo<TRequest>(IServiceProvider services) =>
        GetTypeInfo<TRequest>(services);

    internal static JsonTypeInfo<T> GetTypeInfo<T>(IServiceProvider services) =>
        GetSerializerOptions(services).GetTypeInfo<T>();

    internal static (JsonTypeInfo<TResponse>? TypeInfo, IUnionResponseWriter<TResponse>? UnionWriter, IReadOnlyList<(Type? Type, int StatusCode, string? ContentType)>? UnionProduces)
        ResolveResponse<TResponse>(IServiceProvider services)
    {
        if (services.GetService<IUnionResponseWriterFactory<TResponse>>() is { } factory)
        {
            var registry = services.GetRequiredService<EndpointOptions>().Responses;
            List<(Type? Type, int StatusCode, string? ContentType)> produces = [];
            var writer = factory.Create(services, registry, (type, status, contentType) => produces.Add((type, status, contentType)));
            return (null, writer, produces);
        }

        if (typeof(IResult).IsAssignableFrom(typeof(TResponse)) || typeof(TResponse) == typeof(string))
        {
            return (null, null, null);
        }

        return (GetTypeInfo<TResponse>(services), null, null);
    }

    internal static async ValueTask<Bound<TRequest>> ReadBodyAsync<TRequest>(
        HttpContext context,
        JsonTypeInfo<TRequest> typeInfo,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType())
        {
            return new UnsupportedMediaType("Expected a JSON request body.");
        }

        return await ReadJsonAsync(context, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<Bound<TRequest>> ReadJsonAsync<TRequest>(
        HttpContext context,
        JsonTypeInfo<TRequest> typeInfo,
        CancellationToken cancellationToken)
    {
        TRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return new Validation("The JSON request body is invalid.");
        }
        catch (Exception exception) when (IsConverterFailure(exception))
        {
            return new Validation("The JSON request body is invalid.");
        }
        catch (InvalidOperationException) when (GetEncoding(context.Request) is null)
        {
            return UnsupportedCharset();
        }

        return request is null ? new Validation("A JSON request body is required.") : request;
    }

    internal static bool IsConverterFailure(Exception exception) => exception is FormatException or OverflowException;

    internal static Encoding? GetEncoding(HttpRequest request) =>
        !MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) || !mediaType.Charset.HasValue
            ? Encoding.UTF8
            : mediaType.Encoding;

    internal static UnsupportedMediaType UnsupportedCharset() => new("The request charset is not supported.");

    internal static Task RespondAsync<TResponse>(
        HttpContext context,
        ValueTask<TResponse> handled,
        JsonTypeInfo<TResponse>? typeInfo,
        IUnionResponseWriter<TResponse>? unionWriter) =>
        handled.IsCompletedSuccessfully
            ? WriteResponseAsync(context, handled.Result, typeInfo, unionWriter)
            : AwaitAsync(context, handled, typeInfo, unionWriter);

    private static async Task AwaitAsync<TResponse>(
        HttpContext context,
        ValueTask<TResponse> handled,
        JsonTypeInfo<TResponse>? typeInfo,
        IUnionResponseWriter<TResponse>? unionWriter) =>
        await WriteResponseAsync(context, await handled.ConfigureAwait(false), typeInfo, unionWriter).ConfigureAwait(false);

    private static Task WriteResponseAsync<TResponse>(
        HttpContext context,
        TResponse response,
        JsonTypeInfo<TResponse>? typeInfo,
        IUnionResponseWriter<TResponse>? unionWriter)
    {
        if (unionWriter is not null)
        {
            return unionWriter.WriteAsync(context, response);
        }

        return response switch
        {
            IResult result => result.ExecuteAsync(context),
            string text => WriteTextAsync(context, text),
            null when typeInfo is null => throw new InvalidOperationException("An endpoint returning IResult or string must not return null."),
            _ => typeInfo is not null
                ? context.Response.WriteAsJsonAsync(response, typeInfo, contentType: null)
                : throw new InvalidOperationException("No response serializer is available for this endpoint."),
        };
    }

    internal static Task WriteTextAsync(HttpContext context, string text)
    {
        context.Response.ContentType ??= "text/plain; charset=utf-8";
        return context.Response.WriteAsync(text);
    }

    private static JsonSerializerOptions GetSerializerOptions(IServiceProvider services) =>
        services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
}

[JsonSerializable(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
internal sealed partial class ProblemJsonContext : JsonSerializerContext;
