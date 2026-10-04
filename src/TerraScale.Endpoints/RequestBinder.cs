using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Validation;

namespace TerraScale.Endpoints;

/// <summary>
/// Default request binding and validation for one request type, built once at mapping time.
/// Object requests merge the JSON body with query and route values (route wins) by JSON property
/// name, then deserialize through the app's JSON contract, so binding stays trim and AOT safe.
/// </summary>
internal sealed class RequestBinder<TRequest>
{
    private readonly JsonTypeInfo<TRequest> typeInfo;
    private readonly Dictionary<string, JsonPropertyInfo>? properties;
    private readonly IValidatableTypeInfo? validator;
    private readonly ValidationOptions? validationOptions;

    internal RequestBinder(IServiceProvider services)
    {
        this.typeInfo = EndpointJson.GetRequestTypeInfo<TRequest>(services);
        if (this.typeInfo.Kind == JsonTypeInfoKind.Object)
        {
            this.properties = new(StringComparer.OrdinalIgnoreCase);
            foreach (var property in this.typeInfo.Properties)
            {
                this.properties.TryAdd(property.Name, property);
            }
        }

        // IOptions<ValidationOptions> resolves even without AddValidation(); it just has no resolvers then.
        if (services.GetService<IOptions<ValidationOptions>>()?.Value is { } options
            && options.TryGetValidatableTypeInfo(typeof(TRequest), out var info))
        {
            this.validator = info;
            this.validationOptions = options;
        }
    }

    /// <summary>The bindable properties of an object request, for route and query parameter descriptions.</summary>
    internal IReadOnlyList<JsonPropertyInfo>? Properties => this.properties?.Values.ToArray();

    internal bool Validates => this.validator is not null;

    internal ValueTask<TRequest> BindAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (this.properties is null)
        {
            return EndpointJson.ReadBodyAsync(context, this.typeInfo, cancellationToken);
        }

        var request = context.Request;
        var hasJson = request.HasJsonContentType();
        if (hasJson && !this.HasParameters(request))
        {
            return EndpointJson.ReadJsonAsync(context, this.typeInfo, cancellationToken);
        }

        if (!hasJson && HasBody(context))
        {
            throw new BadHttpRequestException("Expected a JSON request body.", StatusCodes.Status415UnsupportedMediaType);
        }

        return this.MergeAsync(request, hasJson, cancellationToken);
    }

    /// <summary>
    /// Runs data annotation validation (when <c>AddValidation()</c> knows the type) and the endpoint's
    /// own rules; writes one 400 validation problem and returns false when either reports errors.
    /// </summary>
    internal async ValueTask<bool> ValidateAsync(
        HttpContext context,
        TRequest request,
        IAsyncEnumerable<ValidationResult>? rules,
        CancellationToken cancellationToken)
    {
        Dictionary<string, List<string>>? errors = null;
        if (this.validator is not null && request is not null)
        {
            var validation = new ValidateContext { ValidationOptions = this.validationOptions!, ServiceProvider = context.RequestServices };
            await this.validator.ValidateAsync(request, validation, cancellationToken).ConfigureAwait(false);
            if (validation.ValidationErrors is { Count: > 0 } found)
            {
                foreach (var (key, list) in found)
                {
                    foreach (var error in list)
                    {
                        Add(ref errors, key, error.ErrorMessage);
                    }
                }
            }
        }

        if (rules is not null)
        {
            await foreach (var result in rules.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (result is null || result == ValidationResult.Success)
                {
                    continue;
                }

                var message = result.ErrorMessage ?? "The request is invalid.";
                var any = false;
                foreach (var member in result.MemberNames)
                {
                    Add(ref errors, member, message);
                    any = true;
                }

                if (!any)
                {
                    Add(ref errors, string.Empty, message);
                }
            }
        }

        if (errors is null)
        {
            return true;
        }

        var problem = errors.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray(), StringComparer.Ordinal);
        await TypedResults.ValidationProblem(problem).ExecuteAsync(context).ConfigureAwait(false);
        return false;
    }

    private static void Add(ref Dictionary<string, List<string>>? errors, string key, string message)
    {
        errors ??= new(StringComparer.Ordinal);
        if (!errors.TryGetValue(key, out var messages))
        {
            errors[key] = messages = [];
        }

        messages.Add(message);
    }

    private bool HasParameters(HttpRequest request)
    {
        // Read the routing feature directly: HttpRequest.RouteValues allocates one when it is missing.
        if (request.HttpContext.Features.Get<IRouteValuesFeature>()?.RouteValues is { Count: > 0 } routeValues)
        {
            foreach (var (key, _) in routeValues)
            {
                if (this.properties!.ContainsKey(key))
                {
                    return true;
                }
            }
        }

        if (request.QueryString.HasValue)
        {
            foreach (var (key, _) in request.Query)
            {
                if (this.properties!.ContainsKey(key))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async ValueTask<TRequest> MergeAsync(HttpRequest request, bool hasJson, CancellationToken cancellationToken)
    {
        var options = this.typeInfo.Options;
        var nodeOptions = new JsonNodeOptions { PropertyNameCaseInsensitive = options.PropertyNameCaseInsensitive };
        JsonObject json;
        try
        {
            // ponytail: assumes a UTF-8 body; ReadFromJsonAsync transcodes other charsets, this path does not.
            json = !hasJson
                ? new JsonObject(nodeOptions)
                : await JsonNode.ParseAsync(
                    request.Body,
                    nodeOptions,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = options.AllowTrailingCommas,
                        CommentHandling = options.ReadCommentHandling,
                        MaxDepth = options.MaxDepth,
                    },
                    cancellationToken).ConfigureAwait(false) switch
                {
                    JsonObject body => body,
                    null => throw new BadHttpRequestException("A JSON request body is required.", StatusCodes.Status400BadRequest),
                    _ => throw new BadHttpRequestException("The JSON request body must be an object.", StatusCodes.Status400BadRequest),
                };
        }
        catch (JsonException exception)
        {
            throw new BadHttpRequestException("The JSON request body is invalid.", StatusCodes.Status400BadRequest, exception);
        }

        if (request.QueryString.HasValue)
        {
            foreach (var (key, values) in request.Query)
            {
                this.Set(json, key, values);
            }
        }

        foreach (var (key, value) in request.RouteValues)
        {
            this.Set(json, key, value?.ToString());
        }

        try
        {
            return json.Deserialize(this.typeInfo)
                ?? throw new BadHttpRequestException("A request is required.", StatusCodes.Status400BadRequest);
        }
        catch (JsonException exception)
        {
            var detail = string.IsNullOrEmpty(exception.Path) || exception.Path == "$"
                ? "The request is invalid."
                : $"The value of '{exception.Path[2..]}' is invalid.";
            throw new BadHttpRequestException(detail, StatusCodes.Status400BadRequest, exception);
        }
    }

    private void Set(JsonObject json, string key, StringValues values)
    {
        if (!this.properties!.TryGetValue(key, out var property))
        {
            return;
        }

        var info = property.Options.GetTypeInfo(property.PropertyType);
        if (info.Kind == JsonTypeInfoKind.Enumerable && property.PropertyType != typeof(string))
        {
            var array = new JsonArray();
            foreach (var value in values)
            {
                array.Add((JsonNode?)ToNode(value, info.ElementType!));
            }

            json[property.Name] = array;
        }
        else
        {
            json[property.Name] = ToNode(values.Count == 0 ? null : values[^1], property.PropertyType);
        }
    }

    // Strings stay strings; anything else gets JSON number and boolean literals so the property's
    // own converter does the parsing. Other text (guids, dates, enum names) stays a string.
    private static JsonValue? ToNode(string? raw, Type type)
    {
        if (raw is null)
        {
            return null;
        }

        if (type != typeof(string) && (raw is "true" or "false" || (raw.Length > 0 && (char.IsAsciiDigit(raw[0]) || raw[0] == '-'))))
        {
            try
            {
                if (JsonNode.Parse(raw) is JsonValue literal)
                {
                    return literal;
                }
            }
            catch (JsonException)
            {
            }
        }

        return JsonValue.Create(raw);
    }

    private static bool HasBody(HttpContext context) =>
        context.Request.ContentLength > 0
        || context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true;
}
