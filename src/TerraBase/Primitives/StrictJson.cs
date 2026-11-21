using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TerraBase.Errors;

namespace TerraBase.Primitives;

internal readonly union JsonText(string, PayloadTooLarge, Validation);

internal static class StrictJson
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxStoredDepth = 1000;

    private static readonly UTF8Encoding Utf8 = new(false, true);

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
            throw new InvalidDataException("a stored value is not valid UTF-8 JSON", error);
        }
    }

    public static JsonText Read(ReadOnlyMemory<byte> json, bool requireObject)
    {
        if (json.Length > MaxBytes)
        {
            return new PayloadTooLarge("a JSON value is at most 1 MiB");
        }

        string text;
        try
        {
            text = Utf8.GetString(json.Span);
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 64 });
            if (requireObject && parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Validation("the value must be a JSON object");
            }

            if (ValidateValue(parsed.RootElement) is { } refused)
            {
                return refused;
            }
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            return new Validation("the value must be valid UTF-8 JSON without duplicate members or lone surrogate escapes");
        }

        return text;
    }

    public static Validation? ValidateValue(JsonElement value, int maxValues = 4096)
    {
        var nodes = 0;
        return Validate(value, 1, ref nodes, maxValues);
    }

    private static Validation? Validate(JsonElement value, int depth, ref int nodes, int maxValues = 4096)
    {
        if (++nodes > maxValues)
        {
            return new Validation($"a JSON value holds at most {maxValues} values");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object or JsonValueKind.Array when depth > 16:
                return new Validation("a JSON value nests at most 16 levels");
            case JsonValueKind.Object:
                foreach (var member in value.EnumerateObject())
                {
                    _ = member.Name;
                    if (Validate(member.Value, depth + 1, ref nodes, maxValues) is { } refused)
                    {
                        return refused;
                    }
                }

                return null;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (Validate(item, depth + 1, ref nodes, maxValues) is { } refused)
                    {
                        return refused;
                    }
                }

                return null;
            case JsonValueKind.String:
                _ = value.GetString();
                return null;
            case JsonValueKind.Number when !ExactNumber.TryParse(JsonMarshal.GetRawUtf8Value(value), out _):
                return new Validation("numbers have at most 128 significant digits and a magnitude of 0 or 1e-307 to below 1e308");
            default:
                return null;
        }
    }
}
