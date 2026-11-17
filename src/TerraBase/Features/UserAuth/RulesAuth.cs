using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using TerraBase.Primitives;

namespace TerraBase.Features.UserAuth;

internal readonly union RulesScalar(string, long, double);

internal readonly union RulesClaim(string, long, double, IReadOnlyList<RulesScalar>);

internal sealed record RulesAuth(string Uid, IReadOnlyDictionary<string, RulesClaim> Claims, string? OrgId, string? OrgRole, bool IsAnonymous)
{
    private const int MaxClaims = 32;
    private const int MaxClaimsBytes = 1000;
    private const int MaxText = 256;
    private const int MaxItems = 64;
    private const int MaxRole = 128;

    public FrozenSet<string> Roles { get; } = RolesOf(Claims);

    public static RulesAuth? Read(string uid, JsonElement payload, bool custom)
    {
        var claims = FrozenDictionary<string, RulesClaim>.Empty;
        if (payload.TryGetProperty("claims", out var given))
        {
            if (ReadClaims(given) is not { } read)
            {
                return null;
            }

            claims = read;
        }

        if (!OptionalText(payload, "org_id", out var organization) || !OptionalText(payload, "org_role", out var role)
            || !OptionalFlag(payload, "is_anonymous", out var anonymous)
            || organization is not null && !EntityIds.IsValid(organization)
            || role is not null && (organization is null || role.Length is 0 or > MaxRole || role.Any(char.IsControl))
            || custom && (organization is not null || anonymous))
        {
            return null;
        }

        return new RulesAuth(uid, claims, organization, role, anonymous);
    }

    private static FrozenSet<string> RolesOf(IReadOnlyDictionary<string, RulesClaim> claims) =>
        claims.TryGetValue("roles", out var roles) ? roles switch
        {
            string role => FrozenSet.Create(StringComparer.Ordinal, role),
            IReadOnlyList<RulesScalar> items => items.Select(Text).OfType<string>().ToFrozenSet(StringComparer.Ordinal),
            _ => FrozenSet<string>.Empty,
        } : FrozenSet<string>.Empty;

    private static string? Text(RulesScalar item) => item switch
    {
        string text => text,
        _ => null,
    };

    private static bool OptionalText(JsonElement payload, string name, out string? value)
    {
        value = null;
        if (!payload.TryGetProperty(name, out var element))
        {
            return true;
        }

        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is not null;
    }

    private static bool OptionalFlag(JsonElement payload, string name, out bool value)
    {
        value = payload.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.True;
        return !payload.TryGetProperty(name, out element) || element.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static FrozenDictionary<string, RulesClaim>? ReadClaims(JsonElement claims)
    {
        if (claims.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(claims.GetRawText()) > MaxClaimsBytes)
        {
            return null;
        }

        Dictionary<string, RulesClaim> read = new(StringComparer.Ordinal);
        foreach (var claim in claims.EnumerateObject())
        {
            if (read.Count == MaxClaims || !IsName(claim.Name) || Value(claim.Value) is not { } value || !read.TryAdd(claim.Name, value))
            {
                return null;
            }
        }

        return read.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static RulesClaim? Value(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return (RulesClaim)1L;
            case JsonValueKind.False:
                return (RulesClaim)0L;
            case JsonValueKind.Array when value.GetArrayLength() <= MaxItems:
                List<RulesScalar> items = [];
                foreach (var item in value.EnumerateArray())
                {
                    if (Scalar(item) is not { } scalar)
                    {
                        return null;
                    }

                    items.Add(scalar);
                }

                IReadOnlyList<RulesScalar> list = items.AsReadOnly();
                return (RulesClaim)list;
            default:
                return Scalar(value) is not { } single ? (RulesClaim?)null : single switch
                {
                    string text => (RulesClaim)text,
                    long integer => (RulesClaim)integer,
                    double real => (RulesClaim)real,
                };
        }
    }

    private static RulesScalar? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when value.GetString() is { Length: <= MaxText } text => (RulesScalar)text,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => (RulesScalar)integer,
        JsonValueKind.Number when value.TryGetDouble(out var real) && double.IsFinite(real) => (RulesScalar)real,
        _ => null,
    };

    private static bool IsName(string name) => name is { Length: >= 1 and <= 63 } && name[0] is >= 'a' and <= 'z'
        && name.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
