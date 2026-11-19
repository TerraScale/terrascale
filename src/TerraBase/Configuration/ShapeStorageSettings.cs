namespace TerraBase.Configuration;

internal sealed class ShapeStorageSettings
{
    private const string ProductionBucket = "ts-terrabase-shapes-prod";
    private const string ProductionCdnHost = "shapes-ts-terrabase-shapes-prod.terrascale.tech";
    private const string ProductionCdnZone = "4cc466e19478c116fae7dc445a6d0911";
    private const string ProductionZoneName = "terrascale.tech";
    internal const string ProductionIssuer = "https://auth.terrascale.tech/terrascale";
    private static readonly string[] CredentialKeys = ["shapes-access-key-id", "shapes-secret-access-key", "shapes-cdn-api-token"];
    private static readonly string[] LocationKeys = ["shapes-bucket", "shapes-cdn-host", "shapes-cdn-zone-id"];
    internal static string[] Keys => [.. CredentialKeys, .. LocationKeys];

    public Uri Endpoint { get; init; } = new("https://6cd3f3639e928077593d9b470765efeb.r2.cloudflarestorage.com");
    public string Region { get; init; } = "auto";
    public required string Bucket { get; init; }
    public required Uri Cdn { get; init; }
    public required string CdnZone { get; init; }
    public Uri PurgeUri => new($"https://api.cloudflare.com/client/v4/zones/{this.CdnZone}/purge_cache");
    public required string AccessKeyId { get; init; }
    public required string SecretKey { get; init; }
    public required string CloudflareApiToken { get; init; }

    public override string ToString() => "shape object storage";

    internal static ShapeStorageSettings? Parse(IReadOnlyDictionary<string, string> values, string? authIssuer)
    {
        if (!Keys.Any(values.ContainsKey))
        {
            return null;
        }

        if (!CredentialKeys.All(key => values.TryGetValue(key, out var value) && IsToken(value)))
        {
            throw new TerraBaseConfigurationException($"{string.Join(", ", CredentialKeys)} must be set together without whitespace");
        }

        var location = LocationKeys.Select(values.GetValueOrDefault).ToArray();
        if (location.Any(value => value is null) && location.Any(value => value is not null))
        {
            throw new TerraBaseConfigurationException($"{string.Join(", ", LocationKeys)} must be set together");
        }

        var bucket = location[0] ?? ProductionBucket;
        var cdnHost = location[1] ?? ProductionCdnHost;
        var cdnZone = location[2] ?? ProductionCdnZone;
        var production = (bucket, cdnHost, cdnZone) == (ProductionBucket, ProductionCdnHost, ProductionCdnZone);
        if (!production && (bucket == ProductionBucket || cdnZone == ProductionCdnZone
            || cdnHost == ProductionZoneName || cdnHost.EndsWith("." + ProductionZoneName, StringComparison.Ordinal)))
        {
            throw new TerraBaseConfigurationException($"{string.Join(", ", LocationKeys)} must name production's shape storage entirely or share none of it, and another environment's CDN host must lie outside production's zone");
        }

        if (authIssuer is not null && production != (authIssuer == ProductionIssuer))
        {
            throw new TerraBaseConfigurationException(production
                ? "production's shape storage and CDN zone serve only the production auth-issuer; set this environment's shapes-bucket, shapes-cdn-host and shapes-cdn-zone-id"
                : "the production auth-issuer uses only production's shape storage and CDN zone");
        }

        if (cdnZone.Length != 32 || cdnZone.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new TerraBaseConfigurationException("shapes-cdn-zone-id must be a Cloudflare zone id of 32 lowercase hexadecimal digits");
        }

        if (bucket.Length is < 3 or > 63 || !bucket.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            throw new TerraBaseConfigurationException("shapes-bucket must be 3 to 63 lowercase letters, digits or hyphens");
        }

        if (Uri.CheckHostName(cdnHost) != UriHostNameType.Dns || cdnHost.Any(char.IsAsciiLetterUpper))
        {
            throw new TerraBaseConfigurationException("shapes-cdn-host must be a lowercase DNS host name");
        }

        return new ShapeStorageSettings
        {
            Bucket = bucket,
            Cdn = new Uri("https://" + cdnHost + "/"),
            CdnZone = cdnZone,
            AccessKeyId = values[CredentialKeys[0]],
            SecretKey = values[CredentialKeys[1]],
            CloudflareApiToken = values[CredentialKeys[2]],
        };
    }

    private static bool IsToken(string value) =>
        value.Length is > 0 and <= 512 && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
}
