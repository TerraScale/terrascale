namespace TerraScale.DotNet.TerraBase;

/// <summary>
/// TerraBase's regional contract (monorepo docs/terrabase/region-identity.md): every region serves at
/// <c>https://&lt;region&gt;.api.terrascale.tech</c>, names itself in <c>X-TerraBase-Region</c>, and answers <c>421</c>
/// to a request pinned with <c>X-TerraScale-Region-Pin</c> to another region.
/// </summary>
public static class TerraBaseRegion
{
    /// <summary>The request header that pins a request to one region.</summary>
    public const string PinHeader = "X-TerraScale-Region-Pin";

    /// <summary>Whether <paramref name="region"/> is a region id: a lowercase letter, then lowercase letters, digits or hyphens, at most 63 characters, not ending in a hyphen.</summary>
    public static bool IsValid(string? region) =>
        region is { Length: >= 1 and <= 63 } && region[0] is >= 'a' and <= 'z' && region[^1] != '-'
        && region.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>The HTTPS origin of <paramref name="region"/>'s client listener, <c>https://&lt;region&gt;.api.terrascale.tech/</c>.</summary>
    public static Uri Origin(string region) =>
        IsValid(region) ? new Uri($"https://{region}.api.terrascale.tech/") : throw new ArgumentException("Not a TerraBase region id.", nameof(region));
}
