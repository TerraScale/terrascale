using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Validation;
using Microsoft.Net.Http.Headers;

namespace TerraScale.Endpoints;

/// <summary>
/// Default request binding and validation for one request type, built once at mapping time.
/// Object requests merge the JSON body with query and route values (route wins) by JSON property
/// name, then deserialize through the app's JSON contract, so binding stays trim and AOT safe.
/// </summary>
internal sealed class RequestBinder<TRequest>
{
    private readonly JsonTypeInfo<TRequest> typeInfo;
    private readonly JsonPropertyInfo[]? properties;

    // Route and query keys resolve to bindable properties by exact JSON name first, then ignoring
    // case for names that stay unambiguous, so a case-sensitive contract with "id" and "ID" binds both.
    private readonly Dictionary<string, JsonPropertyInfo>? exact;
    private readonly Dictionary<string, JsonPropertyInfo>? folded;
    private readonly IValidatableTypeInfo? validator;
    private readonly ValidationOptions? validationOptions;

    internal RequestBinder(IServiceProvider services)
    {
        this.typeInfo = EndpointJson.GetRequestTypeInfo<TRequest>(services);
        if (this.typeInfo.Kind == JsonTypeInfoKind.Object)
        {
            this.properties = [.. this.typeInfo.Properties];
            this.exact = new(StringComparer.Ordinal);
            this.folded = new(StringComparer.OrdinalIgnoreCase);
            var ambiguous = this.properties
                .GroupBy(static property => property.Name, StringComparer.OrdinalIgnoreCase)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Nested objects and dictionaries cannot come from a string, so query and route values
            // never overwrite them; they only bind from the JSON body.
            foreach (var property in this.properties.Where(RequestValues.CanBindFromString))
            {
                this.exact.TryAdd(property.Name, property);
                if (!ambiguous.Contains(property.Name))
                {
                    this.folded.TryAdd(property.Name, property);
                }
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

    /// <summary>The properties of an object request, for route and query parameter descriptions.</summary>
    internal IReadOnlyList<JsonPropertyInfo>? Properties => this.properties;

    internal bool Validates => this.validator is not null;

    internal ValueTask<TRequest> BindAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (this.properties is null)
        {
            return EndpointJson.ReadBodyAsync(context, this.typeInfo, cancellationToken);
        }

        // An empty body (Content-Length: 0) is no body, even with a JSON content type.
        var request = context.Request;
        var hasBody = HasBody(context);
        var hasJson = hasBody && request.HasJsonContentType();
        if (hasJson && !this.HasParameters(request))
        {
            return EndpointJson.ReadJsonAsync(context, this.typeInfo, cancellationToken);
        }

        if (hasBody && !hasJson)
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
                if (this.TryFind(key, out _))
                {
                    return true;
                }
            }
        }

        if (request.QueryString.HasValue)
        {
            foreach (var (key, _) in request.Query)
            {
                if (this.TryFind(key, out _))
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
        var json = hasJson ? await ReadObjectAsync(request, options, nodeOptions, cancellationToken).ConfigureAwait(false) : new JsonObject(nodeOptions);

        try
        {
            if (request.QueryString.HasValue)
            {
                foreach (var (key, values) in request.Query)
                {
                    this.Set(json, key, values, fromRoute: false);
                }
            }

            foreach (var (key, value) in request.RouteValues)
            {
                this.Set(json, key, value?.ToString(), fromRoute: true);
            }
        }
        catch (ArgumentException exception)
        {
            // JsonObject builds its property dictionary on the first write and rejects duplicate
            // names (or names equal under case-insensitive matching) in the body at that point.
            throw new BadHttpRequestException("The JSON request body contains duplicate properties.", StatusCodes.Status400BadRequest, exception);
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

    private static async ValueTask<JsonObject> ReadObjectAsync(
        HttpRequest request,
        JsonSerializerOptions options,
        JsonNodeOptions nodeOptions,
        CancellationToken cancellationToken)
    {
        // Like ReadFromJsonAsync, honor a non-UTF-8 charset by transcoding the body to UTF-8.
        var encoding = MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ? mediaType.Encoding : null;
        var transcoded = encoding is null || encoding.CodePage == Encoding.UTF8.CodePage
            ? null
            : Encoding.CreateTranscodingStream(request.Body, encoding, Encoding.UTF8, leaveOpen: true);
        await using (transcoded)
        {
            var documentOptions = new JsonDocumentOptions
            {
                AllowTrailingCommas = options.AllowTrailingCommas,
                CommentHandling = options.ReadCommentHandling,
                MaxDepth = options.MaxDepth,
            };
            JsonNode? body;
            try
            {
                body = await JsonNode.ParseAsync(transcoded ?? request.Body, nodeOptions, documentOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new BadHttpRequestException("The JSON request body is invalid.", StatusCodes.Status400BadRequest, exception);
            }

            return body switch
            {
                JsonObject value => value,
                null => throw new BadHttpRequestException("A JSON request body is required.", StatusCodes.Status400BadRequest),
                _ => throw new BadHttpRequestException("The JSON request body must be an object.", StatusCodes.Status400BadRequest),
            };
        }
    }

    private bool TryFind(string key, [NotNullWhen(true)] out JsonPropertyInfo? property) =>
        this.exact!.TryGetValue(key, out property) || this.folded!.TryGetValue(key, out property);

    private void Set(JsonObject json, string key, StringValues values, bool fromRoute)
    {
        if (this.TryFind(key, out var property))
        {
            json[property.Name] = RequestValues.ToJson(property, values, fromRoute);
        }
    }

    private static bool HasBody(HttpContext context) =>
        context.Request.ContentLength > 0
        || context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true;
}

/// <summary>Turns route and query strings into the JSON tokens a request property's converter reads.</summary>
internal static class RequestValues
{
    /// <summary>True when the binder can fill the property from a route or query string.</summary>
    internal static bool CanBindFromString(JsonPropertyInfo property)
    {
        if (property.CustomConverter is not null || !property.Options.TryGetTypeInfo(property.PropertyType, out var info))
        {
            return true; // Receives the raw string.
        }

        return info.Kind == JsonTypeInfoKind.None
            || (info.Kind == JsonTypeInfoKind.Enumerable
                && (!property.Options.TryGetTypeInfo(info.ElementType!, out var element) || element.Kind == JsonTypeInfoKind.None));
    }

    internal static JsonNode? ToJson(JsonPropertyInfo property, StringValues values, bool fromRoute)
    {
        var type = property.PropertyType;
        var options = property.Options;
        var last = values.Count == 0 ? null : values[^1];

        // A property-level converter supplies the contract; the property type may have no standalone
        // metadata (source generation without reflection), so do not ask for it.
        if (property.CustomConverter is { } custom)
        {
            return ToScalar(last, type, IsBuiltIn(custom));
        }

        if (!options.TryGetTypeInfo(type, out var info))
        {
            return ToScalar(last, type, builtIn: false);
        }

        if (info.Kind != JsonTypeInfoKind.Enumerable || type == typeof(string) || !IsBuiltIn(info.Converter))
        {
            return ToScalar(last, type, IsBuiltIn(options, type, info.Converter));
        }

        // A route segment carries a collection as OpenAPI's default "simple" style: 1,2,3.
        if (fromRoute && values.Count == 1 && values[0] is { } segment)
        {
            values = segment.Split(',');
        }

        var elementType = info.ElementType!;
        var elementBuiltIn = options.TryGetTypeInfo(elementType, out var element) && IsBuiltIn(options, elementType, element.Converter);
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add((JsonNode?)ToScalar(value, elementType, elementBuiltIn));
        }

        return array;
    }

    // System.Text.Json wraps the converter of T for T?, which may be an application converter.
    private static bool IsBuiltIn(JsonSerializerOptions options, Type type, JsonConverter converter) =>
        IsBuiltIn(converter)
        && (Nullable.GetUnderlyingType(type) is not { } underlying
            || (options.TryGetTypeInfo(underlying, out var underlyingInfo) && IsBuiltIn(underlyingInfo.Converter)));

    // Application, type and property converters may read any token shape, so they get the raw
    // string; only System.Text.Json's own converters get typed tokens.
    private static bool IsBuiltIn(JsonConverter converter) => converter.GetType().Assembly == typeof(JsonSerializer).Assembly;

    // Picks the token a built-in converter reads: numbers (and numeric enum values) as number
    // literals, booleans as literals, any Guid format TryParse accepts (route constraints allow
    // compact ones) in the "D" format System.Text.Json expects. Everything else, including char,
    // dates and enum names, stays a string.
    private static JsonValue? ToScalar(string? raw, Type type, bool builtIn)
    {
        if (raw is null)
        {
            return null;
        }

        if (!builtIn)
        {
            return JsonValue.Create(raw);
        }

        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(bool) && bool.TryParse(raw, out var flag))
        {
            return JsonValue.Create(flag);
        }

        if (target == typeof(Guid) && Guid.TryParse(raw, out var guid))
        {
            return JsonValue.Create(guid);
        }

        if (ToNumber(raw, target) is { } number)
        {
            return number;
        }

        return JsonValue.Create(raw);
    }

    // Parses like ASP.NET Core parameter binding (invariant culture; leading zeros, a sign and
    // surrounding whitespace allowed) and emits a canonical JSON number, so strict number handling
    // reads it. Range checks stay with the target converter: 300 for a byte is still a 400.
    private static JsonValue? ToNumber(string raw, Type type)
    {
        var culture = CultureInfo.InvariantCulture;
        switch (Type.GetTypeCode(type))
        {
            // Integer types and enums (by their underlying type).
            case >= TypeCode.SByte and <= TypeCode.UInt64:
                return BigInteger.TryParse(raw, NumberStyles.Integer, culture, out var integer) ? Parse(integer.ToString(culture)) : null;
            case TypeCode.Decimal:
                return decimal.TryParse(raw, NumberStyles.Float, culture, out var money) ? JsonValue.Create(money) : null;
            case TypeCode.Single or TypeCode.Double:
                return double.TryParse(raw, NumberStyles.Float, culture, out var real) && double.IsFinite(real) ? JsonValue.Create(real) : null;
        }

        if (type == typeof(Int128) || type == typeof(UInt128))
        {
            return BigInteger.TryParse(raw, NumberStyles.Integer, culture, out var wide) ? Parse(wide.ToString(culture)) : null;
        }

        if (type == typeof(Half))
        {
            return double.TryParse(raw, NumberStyles.Float, culture, out var half) && double.IsFinite(half) ? JsonValue.Create(half) : null;
        }

        return null;

        static JsonValue Parse(string canonical) => (JsonValue)JsonNode.Parse(canonical)!;
    }
}
