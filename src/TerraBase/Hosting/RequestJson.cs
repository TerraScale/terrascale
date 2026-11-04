using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Errors;
using TerraBase.Primitives;

namespace TerraBase.Hosting;

/// <summary>The boundary validator for client JSON: request bodies deserialize strictly through <see cref="Read{T}"/>.</summary>
public static class RequestJson
{
    /// <summary>The read depth bound for retained legacy documents.</summary>
    public const int StoredDocumentMaxDepth = DocumentJson.MaxStoredDepth;

    internal static JsonDocumentOptions StoredDocumentOptions { get; } = new()
    {
        AllowDuplicateProperties = true,
        MaxDepth = StoredDocumentMaxDepth,
    };

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly JsonSerializerOptions Options = new(TerraBaseJsonContext.Default.Options)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 64,
        TypeInfoResolver = TerraBaseJsonContext.Default.WithAddedModifier(RejectNullElements),
    };

    /// <summary>The rule <see cref="IsText"/> checks, as its failure message.</summary>
    public const string TextRule = "strings must be valid UTF-8 without lone UTF-16 surrogate escapes";

    /// <summary>
    /// Deserializes a request body: exact member names, no unknown or duplicate members, numbers as numbers and no
    /// null list elements. Any failure is a 400 with <paramref name="code"/>.
    /// </summary>
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

    /// <summary>
    /// Whether every string and member name is valid UTF-8 without lone UTF-16 surrogate escapes. Reading a token alone
    /// does not validate its text; decoding it does. Malformed JSON passes: the parser that reads it reports it.
    /// </summary>
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
            // Reported, with the route's own message, by the parser that reads this body.
        }
        catch (Exception error) when (error is InvalidOperationException or DecoderFallbackException)
        {
            return false;
        }

        return true;
    }

    // System.Text.Json stores null for a null list element even when the element type is non-nullable, so every
    // request type rejects null elements here instead of in each consumer.
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
