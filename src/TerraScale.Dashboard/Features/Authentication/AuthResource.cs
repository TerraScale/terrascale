using System.Collections.Immutable;

namespace TerraScale.Dashboard.Features.Authentication;

/// <summary>An editable demo resource. Field names match the ts-auth management API.</summary>
public sealed record AuthResource(
    string Id,
    string Section,
    ImmutableDictionary<string, string> Values,
    string Status,
    DateTimeOffset CreatedAt)
{
    public string Value(string key) => Values.GetValueOrDefault(key, "");
    public string Name => Value("name") is { Length: > 0 } name ? name
        : Value("email") is { Length: > 0 } email ? email
        : Value("url") is { Length: > 0 } url ? url : Id;
}
