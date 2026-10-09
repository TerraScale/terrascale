using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TerraBase.Primitives;

/// <summary>A stored document violates the shared JSON contract.</summary>
public sealed class DocumentValidationException(string message, bool tooLarge = false) : Exception(message)
{
    /// <summary>The document exceeds the byte limit.</summary>
    public bool TooLarge { get; } = tooLarge;
}

/// <summary>One JSON contract for local writes and replication apply, enforced by storage.</summary>
internal static class DocumentJson
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxStoredDepth = 1000;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    // Read compatibility for retained/restored bodies, separate from the strict write admission contract.
    public static void ValidateStored(ReadOnlySpan<byte> json)
    {
        try
        {
            _ = Utf8.GetCharCount(json);
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = MaxStoredDepth });
            if (!reader.Read())
            {
                throw new JsonException("the stored JSON value is empty");
            }

            do
            {
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
                {
                    _ = reader.GetString();
                }
            }
            while (reader.Read());
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            throw new InvalidDataException("a stored document is not valid UTF-8 JSON", error);
        }
    }

    public static string Read(ReadOnlyMemory<byte> json, bool requireObject)
    {
        if (json.Length > MaxBytes)
        {
            throw new DocumentValidationException("a document is at most 1 MiB", tooLarge: true);
        }

        try
        {
            var text = Utf8.GetString(json.Span);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 64 });
            if (requireObject && document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DocumentValidationException("the document must be a JSON object");
            }

            ValidateValue(document.RootElement);
            return text;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            throw new DocumentValidationException("the document must be valid UTF-8 JSON without duplicate members or lone surrogate escapes");
        }
    }

    public static void ValidateValue(JsonElement value, int maxValues = 4096)
    {
        var nodes = 0;
        Validate(value, 1, ref nodes, maxValues);
    }

    private static void Validate(JsonElement value, int depth, ref int nodes, int maxValues = 4096)
    {
        if (++nodes > maxValues)
        {
            throw new DocumentValidationException($"a JSON value holds at most {maxValues} values");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object or JsonValueKind.Array when depth > 16:
                throw new DocumentValidationException("a JSON value nests at most 16 levels");
            case JsonValueKind.Object:
                foreach (var member in value.EnumerateObject())
                {
                    _ = member.Name;
                    Validate(member.Value, depth + 1, ref nodes, maxValues);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    Validate(item, depth + 1, ref nodes, maxValues);
                }

                break;
            case JsonValueKind.String:
                _ = value.GetString();
                break;
            case JsonValueKind.Number when !ExactNumber.TryParse(JsonMarshal.GetRawUtf8Value(value), out _):
                throw new DocumentValidationException("numbers have at most 128 significant digits and a magnitude of 0 or 1e-307 to below 1e308");
        }
    }
}
