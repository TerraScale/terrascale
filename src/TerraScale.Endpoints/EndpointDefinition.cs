using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing.Patterns;

namespace TerraScale.Endpoints;

/// <summary>Fluent route and OpenAPI metadata for one endpoint.</summary>
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

    /// <summary>The route pattern.</summary>
    public string Route { get; private set; } = "/";

    /// <summary>The HTTP methods accepted by the endpoint.</summary>
    public IReadOnlyList<string> Methods => this.methods;

    /// <summary>The generated route name.</summary>
    public string? Name { get; private set; }

    /// <summary>The OpenAPI summary.</summary>
    public string? Summary { get; private set; }

    /// <summary>The OpenAPI description.</summary>
    public string? Description { get; private set; }

    /// <summary>The OpenAPI tags.</summary>
    public IReadOnlyList<string> Tags => this.tags;

    /// <summary>Sets a GET route.</summary>
    public EndpointDefinition Get(string route) => this.Method(HttpMethods.Get, route);

    /// <summary>Sets a POST route.</summary>
    public EndpointDefinition Post(string route) => this.Method(HttpMethods.Post, route);

    /// <summary>Sets a PUT route.</summary>
    public EndpointDefinition Put(string route) => this.Method(HttpMethods.Put, route);

    /// <summary>Sets a PATCH route.</summary>
    public EndpointDefinition Patch(string route) => this.Method(HttpMethods.Patch, route);

    /// <summary>Sets a DELETE route.</summary>
    public EndpointDefinition Delete(string route) => this.Method(HttpMethods.Delete, route);

    /// <summary>Sets several HTTP verbs for the route.</summary>
    public EndpointDefinition Verbs(params string[] methods)
    {
        ArgumentNullException.ThrowIfNull(methods);
        if (methods.Length == 0 || methods.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one HTTP method is required.", nameof(methods));
        }

        this.methods.Clear();
        foreach (var method in methods)
        {
            var normalized = method.ToUpperInvariant();
            if (!this.methods.Contains(normalized, StringComparer.Ordinal))
            {
                this.methods.Add(normalized);
            }
        }

        return this;
    }

    /// <summary>Sets the route without changing the selected verbs.</summary>
    public EndpointDefinition WithRoute(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        if (!route.StartsWith('/'))
        {
            throw new ArgumentException("The endpoint route must start with '/'.", nameof(route));
        }

        this.Route = route;
        this.routeConfigured = true;
        return this;
    }

    /// <summary>Sets one or more HTTP methods and the route pattern.</summary>
    public EndpointDefinition Method(string method, string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        this.WithRoute(route);
        this.methods.Clear();
        this.methods.Add(method.ToUpperInvariant());
        return this;
    }

    /// <summary>Sets a stable OpenAPI operation name.</summary>
    public EndpointDefinition WithName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.Name = name;
        return this;
    }

    /// <summary>Sets the OpenAPI summary.</summary>
    public EndpointDefinition WithSummary(string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        this.Summary = summary;
        return this;
    }

    /// <summary>Sets the OpenAPI description.</summary>
    public EndpointDefinition WithDescription(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        this.Description = description;
        return this;
    }

    /// <summary>Adds OpenAPI tags.</summary>
    public EndpointDefinition WithTags(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            this.tags.Add(value);
        }

        return this;
    }

    /// <summary>Documents a response in OpenAPI.</summary>
    public EndpointDefinition Produces<TResponse>(int statusCode = StatusCodes.Status200OK, string contentType = "application/json") =>
        this.Produces(typeof(TResponse), statusCode, contentType);

    /// <summary>Documents a <c>text/event-stream</c> response in OpenAPI.</summary>
    public EndpointDefinition ProducesSse(int statusCode = StatusCodes.Status200OK) =>
        this.Produces<string>(statusCode, "text/event-stream");

    /// <summary>Documents a response type in OpenAPI.</summary>
    public EndpointDefinition Produces(Type responseType, int statusCode = StatusCodes.Status200OK, string contentType = "application/json")
    {
        ArgumentNullException.ThrowIfNull(responseType);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), "The response status code must be between 100 and 599.");
        }
        this.responses.Add((responseType, statusCode, contentType));
        return this;
    }

    /// <summary>Documents a response without a body, such as 404, in OpenAPI.</summary>
    public EndpointDefinition Produces(int statusCode)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), "The response status code must be between 100 and 599.");
        }

        this.responses.Add((null, statusCode, null));
        return this;
    }

    /// <summary>Documents accepted request content in OpenAPI.</summary>
    public EndpointDefinition Accepts<TRequest>(params string[] contentTypes) =>
        this.Accepts(typeof(TRequest), contentTypes);

    /// <summary>Documents accepted request content in OpenAPI.</summary>
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

        this.acceptedContentTypes = contentTypes;
        return this;
    }

    /// <summary>Allows unauthenticated requests to this endpoint.</summary>
    public EndpointDefinition AllowAnonymous()
    {
        this.allowAnonymous = true;
        this.authorizationPolicies = null;
        return this;
    }

    /// <summary>Requires authorization, optionally using named policies.</summary>
    public EndpointDefinition RequireAuthorization(params string[] policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        this.allowAnonymous = false;
        this.authorizationPolicies = policies;
        return this;
    }

    /// <summary>Adds endpoint metadata.</summary>
    public EndpointDefinition WithMetadata(params object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        this.metadata.AddRange(values);
        return this;
    }

    internal void EnsureMappable()
    {
        if (this.methods.Count == 0 || !this.routeConfigured)
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
        bool validated = false)
    {
        this.EnsureMappable();

        // Route and query parameters are described per operation by the OpenAPI transformer, which
        // sees the final route (including any MapGroup prefix) and each HTTP method separately.
        var pattern = RoutePatternFactory.Parse(this.Route);

        // A RequestDelegate keeps mapping free of RequestDelegateFactory (reflection and runtime
        // code generation). The signature MethodInfo is what lets ApiExplorer and OpenAPI describe it.
        var builder = new RouteHandlerBuilder([routes.MapMethods(this.Route, this.methods, handler)]);
        builder.WithMetadata(signature);
        if (this.Name is not null)
        {
            builder.WithName(this.Name);
        }

        if (this.Summary is not null)
        {
            builder.WithSummary(this.Summary);
        }

        if (this.Description is not null)
        {
            builder.WithDescription(this.Description);
        }

        builder.WithMetadata(new RequestParametersMetadata(requestType, requestProperties, validated));

        // Without tags OpenAPI would group the operation under the generic base class name.
        if (this.tags.Count > 0)
        {
            builder.WithTags([.. this.tags]);
        }
        else if (pattern.PathSegments is [{ Parts: [RoutePatternLiteralPart literal] }, ..])
        {
            builder.WithTags(literal.Content);
        }

        // Object requests also bind from route and query values, so their JSON body is optional and
        // routing must not answer 415 to a request that only carries route or query values.
        if (requestType is not null || this.acceptedContentTypes is not null)
        {
            builder.WithMetadata(new AcceptsMetadata(
                this.acceptedContentTypes ?? ["application/json"],
                requestType,
                isOptional: requestProperties is not null));
        }

        if (!typeof(IResult).IsAssignableFrom(responseType)
            && !this.responses.Exists(static response => response.StatusCode == StatusCodes.Status200OK))
        {
            var contentType = responseType == typeof(string) ? "text/plain" : "application/json";
            builder.WithMetadata(new ProducesResponseTypeMetadata(StatusCodes.Status200OK, responseType, [contentType]));
        }

        foreach (var response in this.responses)
        {
            builder.WithMetadata(new ProducesResponseTypeMetadata(
                response.StatusCode,
                response.Type ?? typeof(void),
                response.ContentType is null ? null : [response.ContentType]));
        }

        if (this.allowAnonymous)
        {
            builder.AllowAnonymous();
        }
        else if (this.authorizationPolicies is not null)
        {
            builder.RequireAuthorization(this.authorizationPolicies);
        }

        foreach (var item in this.metadata)
        {
            builder.WithMetadata(item);
        }

        return builder;
    }
}

/// <summary>Marks a TerraScale endpoint for the OpenAPI parameter transformer.</summary>
/// <param name="RequestType">The request model, whose schema supplies property schemas (converters included).</param>
/// <param name="Properties">
/// The properties of an object request bound by the default binder, possibly empty; null when the
/// request is not an object or a BindAsync override binds it, which keeps the body required.
/// </param>
/// <param name="Validated">True when AddValidation() validates the request, so [Required] members are required inputs.</param>
internal sealed record RequestParametersMetadata(Type? RequestType, IReadOnlyList<JsonPropertyInfo>? Properties, bool Validated);

/// <summary>Marker request used by endpoints without a request body.</summary>
public readonly record struct EmptyRequest;
