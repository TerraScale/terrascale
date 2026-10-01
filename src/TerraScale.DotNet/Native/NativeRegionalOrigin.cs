namespace TerraScale.DotNet.Native;

/// <summary>Validates the direct regional origins admitted by native Atlas discovery.</summary>
internal static class NativeRegionalOrigin
{
    internal static bool IsTrustedMemberHost(string value, string region) =>
        IsRegionCode(region) &&
        string.Equals(value, $"https://{region.ToLowerInvariant()}.api.terrascale.tech", StringComparison.Ordinal);

    internal static bool IsRegionCode(string value)
    {
        if (value.Length is < 1 or > 16 ||
            value[0] is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9')) ||
            value.EndsWith('-') || value.Contains("--", StringComparison.Ordinal) ||
            value.Any(character => character is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-')))
            return false;

        return value is not ("API" or "ATLAS" or "AUTH" or "SYNC" or "STATUS" or "WWW" or
            "APP" or "DASH" or "DOCS" or "GIT" or "CDN" or "ADMIN" or "MCP" or "MAIL");
    }
}
