using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
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
using TerraScale.Endpoints.Binding;

namespace TerraScale.Endpoints;

internal sealed class RequestBinder<TRequest>
{
    private readonly JsonTypeInfo<TRequest> typeInfo;
    private readonly JsonPropertyInfo[]? properties;
    private readonly Dictionary<string, JsonPropertyInfo>? exact;
    private readonly Dictionary<string, JsonPropertyInfo>? folded;
    private readonly HashSet<string>? memberNames;
    private readonly IValidatableTypeInfo? validator;
    private readonly ValidationOptions? validationOptions;

    internal RequestBinder(IServiceProvider services)
    {
        typeInfo = EndpointJson.GetRequestTypeInfo<TRequest>(services);
        if (typeInfo.Kind == JsonTypeInfoKind.Object)
        {
            var populate = typeInfo.PreferredPropertyObjectCreationHandling ?? typeInfo.Options.PreferredObjectCreationHandling;
            properties = [.. typeInfo.Properties.Where(property =>
                property.Set is not null
                || property.AssociatedParameter is not null
                || (property.Get is not null
                    && (property.ObjectCreationHandling ?? populate) == JsonObjectCreationHandling.Populate
                    && IsPopulatable(property)))];
            exact = new(StringComparer.Ordinal);
            folded = new(StringComparer.OrdinalIgnoreCase);
            memberNames = new(typeInfo.Properties.Select(static property => property.Name), StringComparer.OrdinalIgnoreCase);
            var ambiguous = properties
                .GroupBy(static property => property.Name, StringComparer.OrdinalIgnoreCase)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var property in properties.Where(RequestValues.CanBindFromString))
            {
                exact.TryAdd(property.Name, property);
                if (!ambiguous.Contains(property.Name))
                {
                    folded.TryAdd(property.Name, property);
                }
            }
        }

        if (services.GetService<IOptions<ValidationOptions>>()?.Value is { } options
            && options.TryGetValidatableTypeInfo(typeof(TRequest), out var info))
        {
            validator = info;
            validationOptions = options;
        }
    }

    internal IReadOnlyList<JsonPropertyInfo>? Properties => properties;

    internal bool Validates => validator is not null;

    internal ValueTask<Bound<TRequest>> BindAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (properties is null)
        {
            return EndpointJson.ReadBodyAsync(context, typeInfo, cancellationToken);
        }

        var request = context.Request;
        var hasBody = HasBody(context);
        var hasJson = hasBody && request.HasJsonContentType();
        if (hasJson && !HasParameters(request))
        {
            return EndpointJson.ReadJsonAsync(context, typeInfo, cancellationToken);
        }

        if (hasBody && !hasJson)
        {
            if (string.IsNullOrEmpty(request.ContentType) && request.ContentLength is null)
            {
                return BindEmptyOrUnsupportedAsync(request, cancellationToken);
            }

            return ValueTask.FromResult<Bound<TRequest>>(new UnsupportedMediaType("Expected a JSON request body."));
        }

        return MergeAsync(request, hasJson, cancellationToken);
    }

    private async ValueTask<Bound<TRequest>> BindEmptyOrUnsupportedAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var probe = new byte[1];
        if (await request.Body.ReadAsync(probe, cancellationToken).ConfigureAwait(false) > 0)
        {
            return new UnsupportedMediaType("Expected a JSON request body.");
        }

        return await MergeAsync(request, hasJson: false, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<bool> ValidateAsync(
        HttpContext context,
        TRequest request,
        IAsyncEnumerable<ValidationResult>? rules,
        CancellationToken cancellationToken)
    {
        Dictionary<string, List<string>>? errors = null;
        if (validator is not null && validationOptions is not null && request is not null)
        {
            var validation = new ValidateContext { ValidationOptions = validationOptions, ServiceProvider = context.RequestServices };
            await validator.ValidateAsync(request, validation, cancellationToken).ConfigureAwait(false);
            if (validation.ValidationErrors is { Count: > 0 } found)
            {
                foreach (var (key, list) in found)
                {
                    foreach (var error in list)
                    {
                        Add(ref errors, key, error.ErrorMessage ?? "The request is invalid.");
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
        if (request.HttpContext.Features.Get<IRouteValuesFeature>()?.RouteValues is { Count: > 0 } routeValues)
        {
            foreach (var (key, _) in routeValues)
            {
                if (TryFind(key, out _))
                {
                    return true;
                }
            }
        }

        if (request.QueryString.HasValue)
        {
            foreach (var pair in new QueryStringEnumerable(request.QueryString.Value))
            {
                if (TryFind(pair.DecodeName().Span, out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async ValueTask<Bound<TRequest>> MergeAsync(HttpRequest request, bool hasJson, CancellationToken cancellationToken)
    {
        var parameters = CollectParameters(request);
        var options = typeInfo.Options;
        if (hasJson)
        {
            var body = await ReadDocumentAsync(request, options, cancellationToken).ConfigureAwait(false);
            return body switch
            {
                Validation invalid => invalid,
                UnsupportedMediaType unsupported => unsupported,
                null => throw new UnreachableException(),
                JsonDocument parsed => MergeJson(parsed, parameters, options),
            };
        }

        return MergeJson(null, parameters, options);
    }

    private Bound<TRequest> MergeJson(JsonDocument? document, RequestParameters parameters, JsonSerializerOptions options)
    {
        var comparison = options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var buffer = new ArrayBufferWriter<byte>();
        using (document)
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = options.MaxDepth }))
        {
            writer.WriteStartObject();
            if (document is not null)
            {
                if (RejectCaseInsensitiveDuplicates(document.RootElement, options) is { } duplicate)
                {
                    return duplicate;
                }

                foreach (var member in document.RootElement.EnumerateObject())
                {
                    if (!Names(parameters.Route.Keys, member.Name, comparison))
                    {
                        member.WriteTo(writer);
                    }
                }
            }

            foreach (var (property, value) in parameters.Route)
            {
                Write(writer, property, value);
            }

            foreach (var (property, value) in parameters.Query)
            {
                if (!parameters.Route.ContainsKey(property) && !InBody(document, property.Name, comparison))
                {
                    Write(writer, property, value);
                }
            }

            writer.WriteEndObject();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(buffer.WrittenSpan, typeInfo);
            return parsed is null ? new Validation("A request is required.") : parsed;
        }
        catch (JsonException exception)
        {
            var detail = string.IsNullOrEmpty(exception.Path) || exception.Path == "$"
                ? "The request is invalid."
                : $"The value of '{exception.Path[2..]}' is invalid.";
            return new Validation(detail);
        }
        catch (Exception exception) when (EndpointJson.IsConverterFailure(exception))
        {
            return new Validation("The request is invalid.");
        }
    }

    private RequestParameters CollectParameters(HttpRequest request)
    {
        var query = new Dictionary<JsonPropertyInfo, JsonNode?>(ReferenceEqualityComparer.Instance);
        if (request.QueryString.HasValue)
        {
            var queried = new Dictionary<JsonPropertyInfo, List<string>>(ReferenceEqualityComparer.Instance);
            foreach (var pair in new QueryStringEnumerable(request.QueryString.Value))
            {
                if (TryFind(pair.DecodeName().Span, out var property))
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
                query[property] = RequestValues.ToJson(property, new StringValues([.. values]), fromRoute: false);
            }
        }

        var route = new Dictionary<JsonPropertyInfo, JsonNode?>(ReferenceEqualityComparer.Instance);
        foreach (var (key, value) in request.RouteValues)
        {
            if (TryFind(key, out var property))
            {
                route[property] = RequestValues.ToJson(property, Convert.ToString(value, CultureInfo.InvariantCulture), fromRoute: true);
            }
        }

        return new RequestParameters(route, query);
    }

    private static void Write(Utf8JsonWriter writer, JsonPropertyInfo property, JsonNode? value)
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

    private static bool InBody(JsonDocument? document, string name, StringComparison comparison)
    {
        if (document is null)
        {
            return false;
        }

        foreach (var member in document.RootElement.EnumerateObject())
        {
            if (string.Equals(member.Name, name, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private Validation? RejectCaseInsensitiveDuplicates(JsonElement root, JsonSerializerOptions options)
    {
        if (options.AllowDuplicateProperties || !options.PropertyNameCaseInsensitive || memberNames is null)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in root.EnumerateObject())
        {
            if (memberNames.Contains(member.Name) && !seen.Add(member.Name))
            {
                return new Validation("The JSON request body contains duplicate properties.");
            }
        }

        return null;
    }

    private static bool Names(IEnumerable<JsonPropertyInfo> properties, string name, StringComparison comparison)
    {
        foreach (var property in properties)
        {
            if (string.Equals(property.Name, name, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask<Bound<JsonDocument>> ReadDocumentAsync(HttpRequest request, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        var encoding = EndpointJson.GetEncoding(request);
        if (encoding is null)
        {
            return EndpointJson.UnsupportedCharset();
        }

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
            catch (JsonException)
            {
                return new Validation("The JSON request body is invalid.");
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            var kind = document.RootElement.ValueKind;
            document.Dispose();
            return kind == JsonValueKind.Null
                ? new Validation("A JSON request body is required.")
                : new Validation("The JSON request body must be an object.");
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "A trimmed interface list only hides a mutable collection interface, which makes the check conservative.")]
    private static bool IsPopulatable(JsonPropertyInfo property)
    {
        var type = property.PropertyType;
        if (type.IsValueType || type.IsArray || !property.Options.TryGetTypeInfo(type, out var contract))
        {
            return false;
        }

        if (contract.Kind == JsonTypeInfoKind.Object)
        {
            return !type.IsInterface;
        }

        if (contract.Kind is not (JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary)
            || type.Namespace == "System.Collections.Immutable"
            || (type.IsGenericType && type.GetGenericTypeDefinition() is var definition
                && (definition == typeof(System.Collections.ObjectModel.ReadOnlyCollection<>)
                    || definition == typeof(System.Collections.ObjectModel.ReadOnlyDictionary<,>))))
        {
            return false;
        }

        return IsMutableCollection(type) || (!type.IsInterface && type.GetInterfaces().Any(IsMutableCollection));

        static bool IsMutableCollection(Type candidate) =>
            candidate == typeof(System.Collections.IList)
            || candidate == typeof(System.Collections.IDictionary)
            || candidate == typeof(System.Collections.Queue)
            || candidate == typeof(System.Collections.Stack)
            || (candidate.IsGenericType && candidate.GetGenericTypeDefinition() is var generic
                && (generic == typeof(ICollection<>) || generic == typeof(IList<>) || generic == typeof(ISet<>) || generic == typeof(IDictionary<,>)
                    || generic == typeof(Queue<>) || generic == typeof(Stack<>)
                    || generic == typeof(System.Collections.Concurrent.ConcurrentQueue<>)
                    || generic == typeof(System.Collections.Concurrent.ConcurrentStack<>)));
    }

    private bool TryFind(string key, [NotNullWhen(true)] out JsonPropertyInfo? property)
    {
        if (exact is not null && exact.TryGetValue(key, out property))
        {
            return true;
        }

        if (folded is not null && folded.TryGetValue(key, out property))
        {
            return true;
        }

        property = null;
        return false;
    }

    private bool TryFind(ReadOnlySpan<char> key, [NotNullWhen(true)] out JsonPropertyInfo? property)
    {
        if (exact is not null && exact.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key, out property))
        {
            return true;
        }

        if (folded is not null && folded.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key, out property))
        {
            return true;
        }

        property = null;
        return false;
    }

    private static bool HasBody(HttpContext context) =>
        context.Request.ContentLength > 0
        || context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true;
}

internal sealed record RequestParameters(IReadOnlyDictionary<JsonPropertyInfo, JsonNode?> Route, IReadOnlyDictionary<JsonPropertyInfo, JsonNode?> Query);

internal static class RequestValues
{
    internal static bool CanBindFromString(JsonPropertyInfo property)
    {
        var options = property.Options;
        if (property.CustomConverter is not null || !options.TryGetTypeInfo(property.PropertyType, out var info))
        {
            return true;
        }

        return info.Kind == JsonTypeInfoKind.None
            || (info.Kind == JsonTypeInfoKind.Enumerable
                && info.ElementType is { } elementType
                && (!options.TryGetTypeInfo(elementType, out var element) || element.Kind == JsonTypeInfoKind.None));
    }

    internal static JsonNode? ToJson(JsonPropertyInfo property, StringValues values, bool fromRoute)
    {
        var type = property.PropertyType;
        var options = property.Options;
        var last = values.Count == 0 ? null : values[^1];

        if (property.CustomConverter is { } custom)
        {
            return ToScalar(last, type, IsBuiltIn(custom), options, custom);
        }

        if (!options.TryGetTypeInfo(type, out var info))
        {
            return ToScalar(last, type, builtIn: false, options);
        }

        if (info.Kind != JsonTypeInfoKind.Enumerable || type == typeof(string) || !IsBuiltIn(info.Converter) || info.ElementType is not { } elementType)
        {
            return ToScalar(last, type, IsBuiltIn(options, type, info.Converter), options);
        }

        if (fromRoute && values.Count == 1 && values[0] is { } segment)
        {
            values = segment.Split(',');
        }

        var elementBuiltIn = options.TryGetTypeInfo(elementType, out var element) && IsBuiltIn(options, elementType, element.Converter);
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add((JsonNode?)ToScalar(value, elementType, elementBuiltIn, options));
        }

        return array;
    }

    private static bool IsBuiltIn(JsonSerializerOptions options, Type type, JsonConverter converter) =>
        IsBuiltIn(converter)
        && (Nullable.GetUnderlyingType(type) is not { } underlying
            || (options.TryGetTypeInfo(underlying, out var underlyingInfo) && IsBuiltIn(underlyingInfo.Converter)));

    private static bool IsBuiltIn(JsonConverter converter) => converter.GetType().Assembly == typeof(JsonSerializer).Assembly;

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
        return JsonNode.Parse(value) as JsonValue ?? throw new UnreachableException();
    }

    private static readonly ConditionalWeakTable<JsonTypeInfo, StrongBox<bool>> EnumContracts = [];

    private static bool ReadsEnumNames(Type type, JsonSerializerOptions options, JsonConverter? propertyConverter)
    {
        if (propertyConverter is not null)
        {
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

    private static JsonValue? ToNumber(string raw, Type type)
    {
        var culture = CultureInfo.InvariantCulture;
        switch (Type.GetTypeCode(type))
        {
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

        static JsonValue Parse(string canonical) => JsonNode.Parse(canonical) as JsonValue ?? throw new UnreachableException();
    }
}
