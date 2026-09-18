namespace TerraScale.Endpoints;

/// <summary>Fluent route and OpenAPI metadata for one endpoint.</summary>
public sealed class EndpointDefinition
{
    private readonly List<string> methods = [];
    private readonly List<string> tags = [];
    private readonly List<object> metadata = [];
    private readonly List<(Type Type, int StatusCode, string ContentType)> responses = [];
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

    internal RouteHandlerBuilder Map(IEndpointRouteBuilder routes, Delegate handler, Type? requestType, Type responseType)
    {
        if (this.methods.Count == 0 || !this.routeConfigured)
        {
            throw new InvalidOperationException("An endpoint must select an HTTP method and route.");
        }

        var builder = routes.MapMethods(this.Route, this.methods, handler);
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

        if (this.tags.Count > 0)
        {
            builder.WithTags(this.tags.ToArray());
        }

        if (requestType is not null && requestType != typeof(EmptyRequest))
        {
            foreach (var contentType in this.acceptedContentTypes ?? ["application/json"])
            {
                builder.Accepts(requestType, contentType);
            }
        }
        else if (this.acceptedContentTypes is not null)
        {
            foreach (var contentType in this.acceptedContentTypes)
            {
                builder.Accepts(typeof(EmptyRequest), contentType);
            }
        }

        if (responseType != typeof(void) && !typeof(IResult).IsAssignableFrom(responseType))
        {
            builder.Produces(StatusCodes.Status200OK, responseType, "application/json");
        }
        foreach (var response in this.responses)
        {
            builder.Produces(response.StatusCode, response.Type, response.ContentType);
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

/// <summary>Marker request used by endpoints without a request body.</summary>
public readonly record struct EmptyRequest;
