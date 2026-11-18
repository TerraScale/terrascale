namespace TerraBase.Configuration;

internal sealed class ShapeStorageSettings
{
    private const string ProductionBucket = "ts-terrabase-shapes-prod";
    private const string ProductionCdnHost = "shapes-ts-terrabase-shapes-prod.terrascale.tech";
    private static readonly string[] CredentialKeys = ["shapes-access-key-id", "shapes-secret-access-key", "shapes-cdn-api-token"];
    private static readonly string[] LocationKeys = ["shapes-bucket", "shapes-cdn-host"];
    internal static string[] Keys => [.. CredentialKeys, .. LocationKeys];

    public Uri Endpoint { get; init; } = new("https://6cd3f3639e928077593d9b470765efeb.r2.cloudflarestorage.com");
    public string Region { get; init; } = "auto";
    public required string Bucket { get; init; }
    public required Uri Cdn { get; init; }
    public required string AccessKeyId { get; init; }
    public required string SecretKey { get; init; }
    public required string CloudflareApiToken { get; init; }

    public override string ToString() => "shape object storage";

    internal static ShapeStorageSettings? Parse(IReadOnlyDictionary<string, string> values)
    {
        if (!Keys.Any(values.ContainsKey))
        {
            return null;
        }

        if (!CredentialKeys.All(key => values.TryGetValue(key, out var value) && IsToken(value)))
        {
            throw new TerraBaseConfigurationException($"{string.Join(", ", CredentialKeys)} must be set together without whitespace");
        }

        var bucket = values.GetValueOrDefault(LocationKeys[0]);
        var cdnHost = values.GetValueOrDefault(LocationKeys[1]);
        if ((bucket is null) != (cdnHost is null))
        {
            throw new TerraBaseConfigurationException($"{string.Join(" and ", LocationKeys)} must be set together");
        }

        bucket ??= ProductionBucket;
        cdnHost ??= ProductionCdnHost;
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
            AccessKeyId = values[CredentialKeys[0]],
            SecretKey = values[CredentialKeys[1]],
            CloudflareApiToken = values[CredentialKeys[2]],
        };
    }

    private static bool IsToken(string value) =>
        value.Length is > 0 and <= 512 && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
}
