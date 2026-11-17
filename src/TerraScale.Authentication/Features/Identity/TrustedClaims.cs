using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Features.Identity;

public static partial class TrustedClaims
{
    public const string Claim = "claims";

    public const int MaxMembers = 32;

    public const int MaxBytes = 1000;

    public const int MaxStringLength = 256;

    public const int MaxArrayItems = 64;

    public static string? Replace(string? current, JsonElement patch) => patch.ValueKind switch
    {
        JsonValueKind.Undefined => current,
        JsonValueKind.Null => null,
        JsonValueKind.Object => Normalize(patch),
        _ => throw Invalid(),
    };

    private static string Normalize(JsonElement claims)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var member in claims.EnumerateObject())
            {
                if (names.Count == MaxMembers || !names.Add(member.Name) || !Name().IsMatch(member.Name) || !Allowed(member.Value))
                {
                    throw Invalid();
                }

                writer.WritePropertyName(member.Name);
                member.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return buffer.Length <= MaxBytes ? Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length) : throw Invalid();
    }

    private static bool Allowed(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Text(value),
        JsonValueKind.Number => Finite(value),
        JsonValueKind.True or JsonValueKind.False => true,
        JsonValueKind.Array => value.GetArrayLength() <= MaxArrayItems
            && (value.EnumerateArray().All(Text) || value.EnumerateArray().All(Finite)),
        _ => false,
    };

    private static bool Text(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= MaxStringLength;

    private static bool Finite(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number);

    private static ApiException Invalid() => new(StatusCodes.Status400BadRequest, "invalid_request",
        "custom_claims must be null or a JSON object of at most 32 members and 1000 bytes serialized, named [a-z][a-z0-9_]{0,62}, whose values are strings of at most 256 characters, finite numbers, booleans, or arrays of at most 64 strings or 64 numbers.");

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex Name();
}
