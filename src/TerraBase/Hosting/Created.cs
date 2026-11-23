namespace TerraBase.Hosting;

public sealed class Created<T>(string location, T body) : IResult
{
    public T Body { get; } = body;

    public Task ExecuteAsync(HttpContext httpContext) => TypedResults.Created(location, Body).ExecuteAsync(httpContext);
}

public sealed class Accepted<T>(string location, T body) : IResult
{
    public T Body { get; } = body;

    public Task ExecuteAsync(HttpContext httpContext) => TypedResults.Accepted(location, Body).ExecuteAsync(httpContext);
}
