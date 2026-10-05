using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>
/// The boundary validator for client JSON. Request bodies deserialize strictly through <see cref="Deserialize{T}"/>;
/// every client value (documents, patch values and operands, query values) passes <see cref="ValidateValue"/> or
/// <see cref="ValidateDocument"/> before anything uses it, so the rest of the slice only sees bounded, finite JSON.
/// </summary>
internal static class RequestJson
{
    /// <summary>Largest stored document, and so the largest document request body.</summary>
    public const int MaxDocumentBytes = 1024 * 1024;

    /// <summary>Deepest nesting of a client value, the document itself counting as the first level.</summary>
    public const int MaxDepth = 16;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private const int MaxNodes = 4096;

    private static readonly JsonSerializerOptions Options = new(TerraBaseJsonContext.Default.Options)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        TypeInfoResolver = TerraBaseJsonContext.Default.WithAddedModifier(RejectNullElements),
    };

    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };

    /// <summary>
    /// Deserializes a request body: exact member names, no unknown or duplicate members, numbers as numbers and no
    /// null list elements. Any failure is a 400 with <paramref name="code"/>.
    /// </summary>
    public static T Deserialize<T>(ReadOnlySpan<byte> json, string code)
    {
        try
        {
            return JsonSerializer.Deserialize(json, Options.GetTypeInfo<T>())
                ?? throw new ApiException(StatusCodes.Status400BadRequest, code, "the body must be a JSON object");
        }
        catch (JsonException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, code, "the body is not a valid request");
        }
    }

    /// <summary>
    /// Rejects invalid UTF-8 and lone UTF-16 surrogate escapes in every string and member name.
    /// Reading a token alone does not validate its text; decoding it does.
    /// </summary>
    public static void RequireText(ReadOnlySpan<byte> json, string code)
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
            throw new ApiException(StatusCodes.Status400BadRequest, code, "strings must be valid UTF-8 without lone UTF-16 surrogate escapes");
        }
    }

    /// <summary>Enforces the document limits on stored JSON: one object, at most 1 MiB, then <see cref="ValidateValue"/>.</summary>
    public static void ValidateDocument(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxDocumentBytes)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "payload_too_large", "a document is at most 1 MiB");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), DocumentOptions);
        }
        catch (JsonException)
        {
            throw new ApiException("the document must be valid JSON without duplicate members");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ApiException("the document must be a JSON object");
            }

            ValidateValue(document.RootElement, "invalid_request");
        }
    }

    /// <summary>
    /// Enforces the limits every client value shares with documents: at most 16 levels and 4096 values, and numbers
    /// that <see cref="ExactNumber.TryParse(ReadOnlySpan{byte}, out ExactNumber)"/> accepts. A patch value within them cannot make a patched document too
    /// deep to serialize (16 path segments plus 16 levels stay far below the serializer's 64).
    /// </summary>
    public static void ValidateValue(JsonElement value, string code)
    {
        var nodes = 0;
        Validate(value, 1, ref nodes, code);
    }

    private static void Validate(JsonElement element, int depth, ref int nodes, string code)
    {
        if (++nodes > MaxNodes)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, code, "a JSON value holds at most 4096 values");
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object or JsonValueKind.Array when depth > MaxDepth:
                throw new ApiException(StatusCodes.Status400BadRequest, code, "a JSON value nests at most 16 levels");
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    Validate(member.Value, depth + 1, ref nodes, code);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Validate(item, depth + 1, ref nodes, code);
                }

                break;
            case JsonValueKind.Number when !ExactNumber.TryParse(JsonMarshal.GetRawUtf8Value(element), out _):
                throw new ApiException(
                    StatusCodes.Status400BadRequest, code, "numbers have at most 128 significant digits and a magnitude of 0 or 1e-307 to below 1e308");
        }
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

/// <summary>
/// An exact decimal, <c>Mantissa × 10^Exponent</c>: the one number rule for client JSON and the arithmetic of
/// <c>inc</c>, which never rounds.
/// </summary>
/// <param name="Mantissa">Digits without trailing zeros.</param>
/// <param name="Exponent">Power of ten.</param>
internal readonly record struct ExactNumber(BigInteger Mantissa, int Exponent)
{
    private const int MaxSignificantDigits = 128;

    // Decimal exponent of the leading digit: inside the normal double range, so SQLite never reads an infinity.
    private const int MinMagnitude = -307;
    private const int MaxMagnitude = 307;

    /// <summary>
    /// Parses JSON number text with at most 128 significant digits (counted before parsing, so the work is bounded)
    /// and a value that is 0 or between 1e-307 and below 1e308 in magnitude.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> text, out ExactNumber number)
    {
        number = default;
        var e = text.IndexOfAny((byte)'e', (byte)'E');
        var mantissa = e < 0 ? text : text[..e];
        long exponent = 0;
        // A bounded exponent keeps the arithmetic below free of overflow; the magnitude check decides the rest.
        if (e >= 0 && (!long.TryParse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
            || exponent is < -1_000_000 or > 1_000_000))
        {
            return false;
        }

        var digits = new StringBuilder(mantissa.Length);
        var negative = false;
        foreach (var c in mantissa)
        {
            if (c == '-')
            {
                negative = true;
            }
            else if (c == '.')
            {
                exponent -= mantissa.Length - mantissa.IndexOf((byte)'.') - 1;
            }
            else if (digits.Length > 0 || c != '0')
            {
                if (digits.Length == MaxSignificantDigits)
                {
                    return false;
                }

                digits.Append((char)c);
            }
        }

        if (digits.Length == 0)
        {
            return true;
        }

        var value = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
        var magnitude = exponent + digits.Length - 1;
        if (magnitude is < MinMagnitude or > MaxMagnitude)
        {
            return false;
        }

        number = Normalize(negative ? -value : value, (int)exponent);
        return true;
    }

    /// <summary>Parses JSON number text; see <see cref="TryParse(ReadOnlySpan{byte}, out ExactNumber)"/>.</summary>
    public static bool TryParse(string text, out ExactNumber number) => TryParse(Encoding.UTF8.GetBytes(text), out number);

    /// <summary>The exact sum; the document limits then decide whether it can be stored.</summary>
    public static ExactNumber Add(ExactNumber a, ExactNumber b)
    {
        if (a.Mantissa.IsZero || b.Mantissa.IsZero)
        {
            return a.Mantissa.IsZero ? b : a;
        }

        // Valid operands keep exponents within about 750 of each other, so the alignment stays small.
        var low = Math.Min(a.Exponent, b.Exponent);
        return Normalize((a.Mantissa * BigInteger.Pow(10, a.Exponent - low)) + (b.Mantissa * BigInteger.Pow(10, b.Exponent - low)), low);
    }

    /// <summary>Plain JSON number text: digits and a decimal point, or an exponent for large integers.</summary>
    public override string ToString()
    {
        var digits = BigInteger.Abs(Mantissa).ToString(CultureInfo.InvariantCulture);
        var sign = Mantissa.Sign < 0 ? "-" : "";
        if (Exponent >= 0)
        {
            return sign + (digits.Length + Exponent <= MaxSignificantDigits ? digits + new string('0', Exponent) : $"{digits}e{Exponent}");
        }

        var point = digits.Length + Exponent;
        return sign + (point > 0 ? $"{digits[..point]}.{digits[point..]}" : $"0.{new string('0', -point)}{digits}");
    }

    private static ExactNumber Normalize(BigInteger mantissa, int exponent)
    {
        if (mantissa.IsZero)
        {
            return default;
        }

        while (mantissa % 10 == 0)
        {
            mantissa /= 10;
            exponent++;
        }

        return new ExactNumber(mantissa, exponent);
    }
}
