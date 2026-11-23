namespace TerraScale.DotNet.TerraBase;

public static class TerraBaseRegion
{
    public const string PinHeader = "X-TerraScale-Region-Pin";

    public static readonly Uri PublicOrigin = new("https://api.terrascale.tech/");

    internal static Uri? OriginOrPublic(Uri? baseAddress, ITerraBaseEndpointResolver? endpointResolver) =>
        baseAddress ?? (endpointResolver is null ? PublicOrigin : null);

    public static bool IsValid(string? region) =>
        region is { Length: >= 1 and <= 63 } && region[0] is >= 'a' and <= 'z' && region[^1] != '-'
        && region.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
