using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
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
    private readonly JsonPropertyInfo[]? properties;

    // Route and query keys resolve to bindable properties by exact JSON name first, then ignoring
    // case for names that stay unambiguous, so a case-sensitive contract with "id" and "ID" binds both.
    private readonly Dictionary<string, JsonPropertyInfo>? exact;
    private readonly Dictionary<string, JsonPropertyInfo>? folded;

    // Every contract member name, for the serializer-equivalent duplicate check under
    // case-insensitive names (the JSON parser only compares names exactly).
    private readonly HashSet<string>? memberNames;
    private readonly IValidatableTypeInfo? validator;
    private readonly ValidationOptions? validationOptions;

    internal RequestBinder(IServiceProvider services)
    {
        this.typeInfo = EndpointJson.GetRequestTypeInfo<TRequest>(services);
        if (this.typeInfo.Kind == JsonTypeInfoKind.Object)
        {
            // Only members the deserializer can fill are inputs: a getter-only `int Id => 42` would
            // silently drop ?id=7, so it is neither bound nor advertised. Populate only fills
            // getter-only members whose value is a mutable reference (a List<T>, not an int or array).
            var populate = this.typeInfo.PreferredPropertyObjectCreationHandling ?? this.typeInfo.Options.PreferredObjectCreationHandling;
            this.properties = [.. this.typeInfo.Properties.Where(property =>
                property.Set is not null
                || property.AssociatedParameter is not null
                || (property.Get is not null
                    && (property.ObjectCreationHandling ?? populate) == JsonObjectCreationHandling.Populate
                    && IsPopulatable(property)))];
            this.exact = new(StringComparer.Ordinal);
            this.folded = new(StringComparer.OrdinalIgnoreCase);
            this.memberNames = new(this.typeInfo.Properties.Select(static property => property.Name), StringComparer.OrdinalIgnoreCase);
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

        // The raw query keeps every key's spelling, as CollectParameters reads it; HttpRequest.Query
        // would fold ?Id=x&id=7 into one "Id" key and miss the exact "id" member.
        if (request.QueryString.HasValue)
        {
            foreach (var pair in new QueryStringEnumerable(request.QueryString.Value))
            {
                if (this.TryFind(pair.DecodeName().Span, out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The body is streamed through unchanged except for the top-level members that route or query
    // values override, so duplicates, Populate, dictionary keys and converters see exactly what direct
    // deserialization would; only the overridden members are replaced, then the serializer runs once.
    private async ValueTask<TRequest> MergeAsync(HttpRequest request, bool hasJson, CancellationToken cancellationToken)
    {
        var overrides = this.CollectParameters(request);
        var options = this.typeInfo.Options;
        using var document = hasJson ? await ReadDocumentAsync(request, options, cancellationToken).ConfigureAwait(false) : null;

        var buffer = new ArrayBufferWriter<byte>();
        // The reader honored options.MaxDepth; the writer must allow the same depth or a body the direct
        // path accepts would fail here (its default is 1000, an app may configure more).
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = options.MaxDepth }))
        {
            writer.WriteStartObject();
            if (document is not null)
            {
                this.RejectCaseInsensitiveDuplicates(document.RootElement, options);
                foreach (var member in document.RootElement.EnumerateObject())
                {
                    if (!IsOverridden(member.Name, overrides, options))
                    {
                        member.WriteTo(writer);
                    }
                }
            }

            foreach (var (property, value) in overrides)
            {
                writer.WritePropertyName(property.Name);
                if (value is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        try
        {
            return JsonSerializer.Deserialize(buffer.WrittenSpan, this.typeInfo)
                ?? throw new BadHttpRequestException("A request is required.", StatusCodes.Status400BadRequest);
        }
        catch (JsonException exception)
        {
            var detail = string.IsNullOrEmpty(exception.Path) || exception.Path == "$"
                ? "The request is invalid."
                : $"The value of '{exception.Path[2..]}' is invalid.";
            throw new BadHttpRequestException(detail, StatusCodes.Status400BadRequest, exception);
        }
        catch (Exception exception) when (EndpointJson.IsConverterFailure(exception))
        {
            throw new BadHttpRequestException("The request is invalid.", StatusCodes.Status400BadRequest, exception);
        }
    }

    // Query values are read from the raw query string because HttpRequest.Query merges keys that
    // differ only by case, which would lose one of "id" and "ID" in a case-sensitive contract. Keys
    // resolving to the same member are combined in order; route values (one per slot) win.
    private Dictionary<JsonPropertyInfo, JsonNode?> CollectParameters(HttpRequest request)
    {
        var overrides = new Dictionary<JsonPropertyInfo, JsonNode?>(ReferenceEqualityComparer.Instance);
        if (request.QueryString.HasValue)
        {
            var queried = new Dictionary<JsonPropertyInfo, List<string>>(ReferenceEqualityComparer.Instance);
            foreach (var pair in new QueryStringEnumerable(request.QueryString.Value))
            {
                if (this.TryFind(pair.DecodeName().Span, out var property))
                {
                    if (!queried.TryGetValue(property, out var values))
                    {
                        queried[property] = values = [];
                    }

                    values.Add(pair.DecodeValue().ToString());
                }
            }

            foreach (var (property, values) in queried)
            {
                overrides[property] = RequestValues.ToJson(property, new StringValues([.. values]), fromRoute: false);
            }
        }

        foreach (var (key, value) in request.RouteValues)
        {
            if (this.TryFind(key, out var property))
            {
                // Route defaults and middleware can store typed values; format them like the strings parsed below.
                overrides[property] = RequestValues.ToJson(property, Convert.ToString(value, CultureInfo.InvariantCulture), fromRoute: true);
            }
        }

        return overrides;
    }

    // With case-insensitive names and AllowDuplicateProperties = false, the serializer rejects
    // {"page":1,"Page":2}, but the JSON parser only compares exact names. Check the same way before
    // overridden members are dropped, so parameters cannot hide a rejected body.
    private void RejectCaseInsensitiveDuplicates(JsonElement root, JsonSerializerOptions options)
    {
        if (options.AllowDuplicateProperties || !options.PropertyNameCaseInsensitive)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in root.EnumerateObject())
        {
            if (this.memberNames!.Contains(member.Name) && !seen.Add(member.Name))
            {
                throw new BadHttpRequestException("The JSON request body contains duplicate properties.", StatusCodes.Status400BadRequest);
            }
        }
    }

    // A body member is replaced when the serializer would bind it to an overridden property:
    // its exact name, or any casing when the contract matches names case-insensitively.
    private static bool IsOverridden(string name, Dictionary<JsonPropertyInfo, JsonNode?> overrides, JsonSerializerOptions options)
    {
        var comparison = options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var property in overrides.Keys)
        {
            if (string.Equals(property.Name, name, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask<JsonDocument> ReadDocumentAsync(HttpRequest request, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        // Like ReadFromJsonAsync, honor a non-UTF-8 charset by transcoding the body to UTF-8.
        var encoding = EndpointJson.GetEncoding(request) ?? throw EndpointJson.UnsupportedCharset();
        var transcoded = encoding.CodePage == Encoding.UTF8.CodePage
            ? null
            : Encoding.CreateTranscodingStream(request.Body, encoding, Encoding.UTF8, leaveOpen: true);
        await using (transcoded)
        {
            var documentOptions = new JsonDocumentOptions
            {
                AllowTrailingCommas = options.AllowTrailingCommas,
                AllowDuplicateProperties = options.AllowDuplicateProperties,
                CommentHandling = options.ReadCommentHandling,
                MaxDepth = options.MaxDepth,
            };
            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(transcoded ?? request.Body, documentOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new BadHttpRequestException("The JSON request body is invalid.", StatusCodes.Status400BadRequest, exception);
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            var kind = document.RootElement.ValueKind;
            document.Dispose();
            throw kind == JsonValueKind.Null
                ? new BadHttpRequestException("A JSON request body is required.", StatusCodes.Status400BadRequest)
                : new BadHttpRequestException("The JSON request body must be an object.", StatusCodes.Status400BadRequest);
        }
    }

    // Populate fills an existing instance only when its converter can add to it: objects, and mutable
    // collections. Interfaces such as IReadOnlyList<T>/IEnumerable<T>, arrays and read-only or
    // immutable collections are silently replaced or ignored instead.
    // ponytail: mutable collections are recognized through IList/IDictionary, so a getter-only
    // HashSet<T> is conservatively not an input; widen if populated sets show up.
    private static bool IsPopulatable(JsonPropertyInfo property)
    {
        var type = property.PropertyType;
        if (type.IsValueType || type.IsArray || type.IsInterface || !property.Options.TryGetTypeInfo(type, out var contract))
        {
            return false;
        }

        return contract.Kind switch
        {
            JsonTypeInfoKind.Object => true,
            JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary =>
                (typeof(System.Collections.IList).IsAssignableFrom(type) || typeof(System.Collections.IDictionary).IsAssignableFrom(type))
                && !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Collections.ObjectModel.ReadOnlyCollection<>))
                && type.Namespace != "System.Collections.Immutable",
            _ => false,
        };
    }

    private bool TryFind(string key, [NotNullWhen(true)] out JsonPropertyInfo? property) =>
        this.exact!.TryGetValue(key, out property) || this.folded!.TryGetValue(key, out property);

    // Span lookups avoid allocating a string per query key.
    private bool TryFind(ReadOnlySpan<char> key, [NotNullWhen(true)] out JsonPropertyInfo? property) =>
        this.exact!.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key, out property)
        || this.folded!.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key, out property);

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
        var options = property.Options;
        if (property.CustomConverter is not null || !options.TryGetTypeInfo(property.PropertyType, out var info))
        {
            return true; // Receives the raw string.
        }

        // Nullable<T> reports the kind of T (Object for a struct with members), so Filter? stays body-only.
        return info.Kind == JsonTypeInfoKind.None
            || (info.Kind == JsonTypeInfoKind.Enumerable
                && (!options.TryGetTypeInfo(info.ElementType!, out var element) || element.Kind == JsonTypeInfoKind.None));
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
            return ToScalar(last, type, IsBuiltIn(custom), options, custom);
        }

        if (!options.TryGetTypeInfo(type, out var info))
        {
            return ToScalar(last, type, builtIn: false, options);
        }

        if (info.Kind != JsonTypeInfoKind.Enumerable || type == typeof(string) || !IsBuiltIn(info.Converter))
        {
            return ToScalar(last, type, IsBuiltIn(options, type, info.Converter), options);
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
            array.Add((JsonNode?)ToScalar(value, elementType, elementBuiltIn, options));
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
    private static JsonValue? ToScalar(string? raw, Type type, bool builtIn, JsonSerializerOptions options, JsonConverter? propertyConverter = null)
    {
        if (raw is null)
        {
            return null;
        }

        if (!builtIn)
        {
            return JsonValue.Create(raw);
        }

        // Like ASP.NET Core parameter binding, ?limit= binds null to a nullable value type.
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null && string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var target = underlying ?? type;
        if (target == typeof(bool) && bool.TryParse(raw, out var flag))
        {
            return JsonValue.Create(flag);
        }

        if (target == typeof(Guid) && Guid.TryParse(raw, out var guid))
        {
            return JsonValue.Create(guid);
        }

        if (target.IsEnum)
        {
            return ToEnum(raw, target, options, propertyConverter);
        }

        return ToNumber(raw, target) ?? JsonValue.Create(raw);
    }

    // A string-enum contract (JsonStringEnumConverter at any level) reads names itself, with its
    // naming policy, member names and AllowIntegerValues rule, so it gets the raw string. The
    // default numeric contract only reads numbers, so names and flag combinations ("Read, Write")
    // parse like ASP.NET Core parameter binding and are sent as the underlying number.
    private static JsonValue ToEnum(string raw, Type type, JsonSerializerOptions options, JsonConverter? propertyConverter)
    {
        if (ReadsEnumNames(type, options, propertyConverter))
        {
            return JsonValue.Create(raw);
        }

        if (ToNumber(raw, type) is { } number)
        {
            return number;
        }

        if (!Enum.TryParse(type, raw, ignoreCase: true, out var named))
        {
            return JsonValue.Create(raw);
        }

        var culture = CultureInfo.InvariantCulture;
        var value = Type.GetTypeCode(type) == TypeCode.UInt64
            ? Convert.ToUInt64(named, culture).ToString(culture)
            : Convert.ToInt64(named, culture).ToString(culture);
        return (JsonValue)JsonNode.Parse(value)!;
    }

    private static readonly ConditionalWeakTable<JsonTypeInfo, StrongBox<bool>> EnumContracts = [];

    private static bool ReadsEnumNames(Type type, JsonSerializerOptions options, JsonConverter? propertyConverter)
    {
        if (propertyConverter is not null)
        {
            // Subclasses count too: [JsonConverter] needs a parameterless one to pass options.
            for (var converterType = propertyConverter.GetType(); converterType is not null; converterType = converterType.BaseType)
            {
                if (converterType == typeof(JsonStringEnumConverter)
                    || (converterType.IsGenericType && converterType.GetGenericTypeDefinition() == typeof(JsonStringEnumConverter<>)))
                {
                    return true;
                }
            }

            return false;
        }

        // Options- and type-level converters are only visible through the contract, so serialize one
        // defined value once per contract: a string-enum converter writes a string token.
        if (!options.TryGetTypeInfo(type, out var info) || Enum.GetNames(type) is not [var first, ..])
        {
            return false;
        }

        if (!EnumContracts.TryGetValue(info, out var known))
        {
            known = new StrongBox<bool>(JsonSerializer.Serialize(Enum.Parse(type, first), info).StartsWith('"'));
            EnumContracts.AddOrUpdate(info, known);
        }

        return known.Value;
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
