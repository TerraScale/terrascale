using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing.Patterns;

namespace TerraScale.Endpoints;

public sealed class EndpointDefinition
{
    private readonly List<string> methods = [];
    private readonly List<string> tags = [];
    private readonly List<object> metadata = [];
    private readonly List<(Type? Type, int StatusCode, string? ContentType)> responses = [];
    private string[]? acceptedContentTypes;
    private string[]? authorizationPolicies;
    private bool allowAnonymous;
    private bool routeConfigured;

    public string Route { get; private set; } = "/";

    public IReadOnlyList<string> Methods => methods;

    public string? Name { get; private set; }

    public string? Summary { get; private set; }

    public string? Description { get; private set; }

    public IReadOnlyList<string> Tags => tags;

    public EndpointDefinition Get(string route) => Method(HttpMethods.Get, route);

    public EndpointDefinition Post(string route) => Method(HttpMethods.Post, route);

    public EndpointDefinition Put(string route) => Method(HttpMethods.Put, route);

    public EndpointDefinition Patch(string route) => Method(HttpMethods.Patch, route);

    public EndpointDefinition Delete(string route) => Method(HttpMethods.Delete, route);

    public EndpointDefinition Verbs(params string[] verbs)
    {
        ArgumentNullException.ThrowIfNull(verbs);
        if (verbs.Length == 0 || verbs.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one HTTP method is required.", nameof(verbs));
        }

        methods.Clear();
        foreach (var method in verbs)
        {
            var normalized = method.ToUpperInvariant();
            if (!methods.Contains(normalized, StringComparer.Ordinal))
            {
                methods.Add(normalized);
            }
        }

        return this;
    }

    public EndpointDefinition WithRoute(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        if (!route.StartsWith('/'))
        {
            throw new ArgumentException("The endpoint route must start with '/'.", nameof(route));
        }

        Route = route;
        routeConfigured = true;
        return this;
    }

    public EndpointDefinition Method(string method, string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        WithRoute(route);
        methods.Clear();
        methods.Add(method.ToUpperInvariant());
        return this;
    }

    public EndpointDefinition WithName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        return this;
    }

    public EndpointDefinition WithSummary(string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        Summary = summary;
        return this;
    }

    public EndpointDefinition WithDescription(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Description = description;
        return this;
    }

    public EndpointDefinition WithTags(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            tags.Add(value);
        }

        return this;
    }

    public EndpointDefinition Produces<TResponse>(int statusCode = StatusCodes.Status200OK, string contentType = "application/json") =>
        Produces(typeof(TResponse), statusCode, contentType);

    public EndpointDefinition ProducesSse(int statusCode = StatusCodes.Status200OK) =>
        Produces<string>(statusCode, "text/event-stream");

    public EndpointDefinition Produces(Type responseType, int statusCode = StatusCodes.Status200OK, string contentType = "application/json")
    {
        ArgumentNullException.ThrowIfNull(responseType);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), "The response status code must be between 100 and 599.");
        }

        responses.Add((responseType, statusCode, contentType));
        return this;
    }

    public EndpointDefinition Produces(int statusCode)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), "The response status code must be between 100 and 599.");
        }

        responses.Add((null, statusCode, null));
        return this;
    }

    public EndpointDefinition Accepts<TRequest>(params string[] contentTypes) =>
        Accepts(typeof(TRequest), contentTypes);

    public EndpointDefinition Accepts(Type requestType, params string[] contentTypes)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(contentTypes);
        if (contentTypes.Length == 0)
        {
            contentTypes = ["application/json"];
        }
        else if (contentTypes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Content types cannot be empty.", nameof(contentTypes));
        }

        acceptedContentTypes = contentTypes;
        return this;
    }

    public EndpointDefinition AllowAnonymous()
    {
        allowAnonymous = true;
        authorizationPolicies = null;
        return this;
    }

    public EndpointDefinition RequireAuthorization(params string[] policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        allowAnonymous = false;
        authorizationPolicies = policies;
        return this;
    }

    public EndpointDefinition WithMetadata(params object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        metadata.AddRange(values);
        return this;
    }

    internal void EnsureMappable()
    {
        if (methods.Count == 0 || !routeConfigured)
        {
            throw new InvalidOperationException("An endpoint must select an HTTP method and route.");
        }
    }

    internal RouteHandlerBuilder Map(
        IEndpointRouteBuilder routes,
        RequestDelegate handler,
        MethodInfo signature,
        Type? requestType,
        Type responseType,
        IReadOnlyList<JsonPropertyInfo>? requestProperties = null,
        bool validated = false,
        IReadOnlyList<(Type? Type, int StatusCode, string? ContentType)>? unionProduces = null)
    {
        EnsureMappable();

        var pattern = RoutePatternFactory.Parse(Route);
        var builder = new RouteHandlerBuilder([routes.MapMethods(Route, methods, handler)]);
        builder.WithMetadata(signature);
        if (Name is not null)
        {
            builder.WithName(Name);
        }

        if (Summary is not null)
        {
            builder.WithSummary(Summary);
        }

        if (Description is not null)
        {
            builder.WithDescription(Description);
        }

        builder.WithMetadata(new RequestParametersMetadata(requestType, requestProperties, validated));

        if (tags.Count > 0)
        {
            builder.WithTags([.. tags]);
        }
        else if (pattern.PathSegments is [{ Parts: [RoutePatternLiteralPart literal] }, ..])
        {
            builder.WithTags(literal.Content);
        }

        if (requestType is not null || acceptedContentTypes is not null)
        {
            builder.WithMetadata(new AcceptsMetadata(
                acceptedContentTypes ?? ["application/json"],
                requestType,
                isOptional: requestProperties is not null));
        }

        if (unionProduces is not null)
        {
            foreach (var produced in unionProduces)
            {
                if (!responses.Exists(existing => existing.StatusCode == produced.StatusCode))
                {
                    builder.WithMetadata(new ProducesResponseTypeMetadata(
                        produced.StatusCode,
                        produced.Type ?? typeof(void),
                        produced.ContentType is null ? null : [produced.ContentType]));
                }
            }
        }
        else if (!typeof(IResult).IsAssignableFrom(responseType)
            && !responses.Exists(static response => response.StatusCode == StatusCodes.Status200OK))
        {
            var contentType = responseType == typeof(string) ? "text/plain" : "application/json";
            builder.WithMetadata(new ProducesResponseTypeMetadata(StatusCodes.Status200OK, responseType, [contentType]));
        }

        foreach (var response in responses)
        {
            builder.WithMetadata(new ProducesResponseTypeMetadata(
                response.StatusCode,
                response.Type ?? typeof(void),
                response.ContentType is null ? null : [response.ContentType]));
        }

        if (allowAnonymous)
        {
            builder.AllowAnonymous();
        }
        else if (authorizationPolicies is not null)
        {
            builder.RequireAuthorization(authorizationPolicies);
        }

        foreach (var item in metadata)
        {
            builder.WithMetadata(item);
        }

        return builder;
    }
}

internal sealed record RequestParametersMetadata(Type? RequestType, IReadOnlyList<JsonPropertyInfo>? Properties, bool Validated);

public readonly record struct EmptyRequest;
