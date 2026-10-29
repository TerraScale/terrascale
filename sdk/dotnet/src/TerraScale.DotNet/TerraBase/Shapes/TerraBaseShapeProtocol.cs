using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Grammar checks of shape reads protocol v1 (sections 2, 3 and 5).</summary>
internal static class TerraBaseShapeProtocol
{
    internal const int MaxWhereBytes = 8 * 1024;
    private const int MaxFilterDepth = 8;
    private const int MaxFilterNodes = 256;
    private const int MaxGroupChildren = 64;
    private const int MaxInValues = 256;
    private static readonly System.Buffers.SearchValues<char> LowerHex = System.Buffers.SearchValues.Create("0123456789abcdef");
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>A canonical decimal: no sign, no leading zeros, at most <see cref="long.MaxValue"/>.</summary>
    internal static bool TryParseDecimal(string? value, out long result)
    {
        result = 0;
        return value is { Length: > 0 } && (value.Length == 1 || value[0] != '0') && value.All(char.IsAsciiDigit)
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    /// <summary><c>snap.&lt;S&gt;.&lt;P&gt;</c>; <paramref name="snapshot"/> is <c>S</c>.</summary>
    internal static bool TryParseSnapshotOffset(string? value, out long snapshot, out string page)
    {
        snapshot = 0;
        page = string.Empty;
        if (value?.Split('.') is not ["snap", var s, var p] || !TryParseDecimal(s, out snapshot)) return false;
        page = p;
        return p == "0" || IsCanonicalUtf8Base64Url(p);
    }

    /// <summary>An offset of section 3: <c>-1</c>, <c>snap.&lt;S&gt;.&lt;P&gt;</c> or a decimal commit boundary.</summary>
    internal static bool IsValidOffset(string? value) =>
        value == "-1" || TryParseDecimal(value, out _) || TryParseSnapshotOffset(value, out _, out _);

    /// <summary><c>h1-</c> and 16 lowercase hexadecimal digits.</summary>
    internal static bool IsValidHandle(string? value) =>
        value is { Length: 19 } && value.StartsWith("h1-", StringComparison.Ordinal) && !value.AsSpan(3).ContainsAnyExcept(LowerHex);

    internal static bool IsValidShapeId(string? value) => value is { Length: 32 } && !value.AsSpan().ContainsAnyExcept(LowerHex);

    internal static bool IsBase64UrlToken(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // A document id is the canonical unpadded base64url of its UTF-8 bytes.
    private static bool IsCanonicalUtf8Base64Url(string value)
    {
        if (!IsBase64UrlToken(value)) return false;
        try
        {
            var bytes = Base64Url.DecodeFromChars(value);
            if (bytes.Length == 0 || Base64Url.EncodeToString(bytes) != value) return false;
            StrictUtf8.GetString(bytes);
            return true;
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks a raw <c>where</c> against the query v1 filter grammar and limits. Canonicalization is the server's; the
    /// string is sent as written.
    /// </summary>
    /// <exception cref="ArgumentException">The filter is empty, too large, not JSON, or outside the grammar.</exception>
    internal static void ValidateWhere(string where)
    {
        const string Rule = "Where must be a query v1 filter of at most 8 KiB UTF-8, 8 levels and 256 nodes, for example {\"eq\":[\"/read\",false]}.";
        if (string.IsNullOrEmpty(where) || Encoding.UTF8.GetByteCount(where) > MaxWhereBytes) throw new ArgumentException(Rule, nameof(where));
        try
        {
            using var document = JsonDocument.Parse(where);
            var nodes = 0;
            if (!IsFilter(document.RootElement, 1, ref nodes)) throw new ArgumentException(Rule, nameof(where));
        }
        catch (JsonException error)
        {
            throw new ArgumentException(Rule, nameof(where), error);
        }
    }

    private static bool IsFilter(JsonElement filter, int depth, ref int nodes)
    {
        if (depth > MaxFilterDepth || ++nodes > MaxFilterNodes || filter.ValueKind != JsonValueKind.Object) return false;
        using var members = filter.EnumerateObject();
        if (!members.MoveNext()) return false;
        var (op, value) = (members.Current.Name, members.Current.Value);
        if (members.MoveNext()) return false;
        switch (op)
        {
            case "and" or "or":
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > MaxGroupChildren) return false;
                foreach (var child in value.EnumerateArray())
                {
                    if (!IsFilter(child, depth + 1, ref nodes)) return false;
                }

                return true;
            case "not":
                return IsFilter(value, depth + 1, ref nodes);
            case "exists":
                return IsPath(value);
            case "eq" or "ne" or "lt" or "lte" or "gt" or "gte" or "contains":
                return value is { ValueKind: JsonValueKind.Array } && value.GetArrayLength() == 2 && IsPath(value[0]) && IsScalar(value[1]);
            case "prefix":
                return value is { ValueKind: JsonValueKind.Array } && value.GetArrayLength() == 2 && IsPath(value[0]) && value[1].ValueKind == JsonValueKind.String;
            case "in" or "nin":
                return value is { ValueKind: JsonValueKind.Array } && value.GetArrayLength() == 2 && IsPath(value[0])
                    && value[1] is { ValueKind: JsonValueKind.Array } values && values.GetArrayLength() is > 0 and <= MaxInValues
                    && values.EnumerateArray().All(IsScalar);
            default:
                return false;
        }
    }

    // "$id" or a JSON Pointer whose '~' escapes are ~0 or ~1.
    private static bool IsPath(JsonElement path)
    {
        if (path.ValueKind != JsonValueKind.String) return false;
        var text = path.GetString()!;
        if (text == "$id") return true;
        if (text.Length > 0 && text[0] != '/') return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '~' && (i + 1 == text.Length || text[i + 1] is not ('0' or '1'))) return false;
        }

        return true;
    }

    private static bool IsScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
        JsonValueKind.Number => value.TryGetInt64(out _) || (value.TryGetDouble(out var number) && double.IsFinite(number)),
        _ => false,
    };
}
