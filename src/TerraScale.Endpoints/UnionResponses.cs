namespace TerraScale.Endpoints;

public interface IUnionResponseWriter<in TUnion>
{
    Task WriteAsync(HttpContext context, TUnion response);
}

public interface IUnionResponseWriterFactory<TUnion>
{
    IUnionResponseWriter<TUnion> Create(
        IServiceProvider services,
        EndpointResponseRegistry registry,
        Action<Type?, int, string?> addProduces);
}

public sealed class EndpointResponseRegistry
{
    private readonly Dictionary<Type, Entry> entries = [];

    public void Map<TCase>(Func<HttpContext, TCase, IResult> map, int statusCode, Type? bodyType = null, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ValidateStatusCode(statusCode);
        entries[typeof(TCase)] = new Entry(map, statusCode, bodyType, contentType);
    }

    public void Map<TCase>(Func<TCase, IResult> map, int statusCode, Type? bodyType = null, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        Map<TCase>((_, value) => map(value), statusCode, bodyType, contentType);
    }

    public Func<HttpContext, TCase, Task> Resolve<TCase>(IServiceProvider services, Action<Type?, int, string?> addProduces)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(addProduces);

        if (entries.TryGetValue(typeof(TCase), out var entry))
        {
            addProduces(entry.BodyType, entry.StatusCode, entry.ContentType);
            var mapper = (Func<HttpContext, TCase, IResult>)entry.Map;
            return (context, value) => mapper(context, value).ExecuteAsync(context);
        }

        if (typeof(IResult).IsAssignableFrom(typeof(TCase)))
        {
            return static (context, value) => value is IResult result
                ? result.ExecuteAsync(context)
                : throw new InvalidOperationException("An endpoint union case returning IResult must not be null.");
        }

        if (typeof(TCase) == typeof(string))
        {
            addProduces(typeof(string), StatusCodes.Status200OK, "text/plain");
            return static (context, value) => value is string text
                ? EndpointJson.WriteTextAsync(context, text)
                : throw new InvalidOperationException("An endpoint union case returning string must not be null.");
        }

        var typeInfo = EndpointJson.GetTypeInfo<TCase>(services);
        addProduces(typeof(TCase), StatusCodes.Status200OK, "application/json");
        return (context, value) => context.Response.WriteAsJsonAsync(value, typeInfo, contentType: null);
    }

    private static void ValidateStatusCode(int statusCode)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), "The response status code must be between 100 and 599.");
        }
    }

    private sealed record Entry(Delegate Map, int StatusCode, Type? BodyType, string? ContentType);
}
