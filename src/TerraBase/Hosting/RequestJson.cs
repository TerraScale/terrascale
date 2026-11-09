using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Errors;
using TerraBase.Primitives;

namespace TerraBase.Hosting;

public static class RequestJson
{
    public const int StoredMaxDepth = StrictJson.MaxStoredDepth;

    internal static JsonDocumentOptions StoredJsonOptions { get; } = new()
    {
        AllowDuplicateProperties = true,
        MaxDepth = StoredMaxDepth,
    };

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly JsonSerializerOptions Options = new(TerraBaseJsonContext.Default.Options)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 64,
        TypeInfoResolver = TerraBaseJson.Resolver.WithAddedModifier(RejectNullElements),
    };

    public const string TextRule = "strings must be valid UTF-8 without lone UTF-16 surrogate escapes";

    public static Valid<T> Read<T>(ReadOnlySpan<byte> json, string code) =>
        TryRead<T>(json, out var value, out var rule) ? value : new Validation(rule, code);

    private static bool TryRead<T>(ReadOnlySpan<byte> json, [MaybeNullWhen(false)] out T value, out string rule)
    {
        rule = "the body must be a JSON object";
        try
        {
            value = JsonSerializer.Deserialize(json, Options.GetTypeInfo<T>());
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            rule = "the body is not a valid request";
            return false;
        }
    }

    public static bool IsText(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        try
        {
            _ = StrictUtf8.GetCharCount(json);
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
                {
                    _ = reader.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (Exception error) when (error is InvalidOperationException or DecoderFallbackException)
        {
            return false;
        }

        return true;
    }

    private static void RejectNullElements(JsonTypeInfo type)
    {
        if (type.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        var lists = type.Properties
            .Where(property => property.Get is not null && typeof(IEnumerable<object>).IsAssignableFrom(property.PropertyType))
            .ToList();
        if (lists.Count == 0)
        {
            return;
        }

        type.OnDeserialized = value =>
        {
            foreach (var list in lists)
            {
                if (list.Get!(value) is IEnumerable<object?> items && items.Contains(null))
                {
                    throw new JsonException($"{list.Name} holds null");
                }
            }
        };
    }
}
