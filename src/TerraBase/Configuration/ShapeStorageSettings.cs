namespace TerraBase.Configuration;

internal sealed class ShapeStorageSettings
{
    internal const string BucketName = "ts-terrabase-shapes-prod";
    internal const string CdnHost = "shapes-ts-terrabase-shapes-prod.terrascale.tech";
    internal static string[] Keys => ["shapes-access-key-id", "shapes-secret-access-key", "shapes-cdn-api-token"];

    public Uri Endpoint { get; init; } = new("https://6cd3f3639e928077593d9b470765efeb.r2.cloudflarestorage.com");
    public string Region { get; init; } = "auto";
    public string Bucket { get; init; } = BucketName;
    public Uri? Cdn { get; init; } = new("https://" + CdnHost + "/");
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

        if (!Keys.All(key => values.TryGetValue(key, out var value) && value.Length is > 0 and <= 512
            && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))))
        {
            throw new TerraBaseConfigurationException($"{string.Join(", ", Keys)} must be set together without whitespace");
        }

        return new ShapeStorageSettings
        {
            AccessKeyId = values[Keys[0]],
            SecretKey = values[Keys[1]],
            CloudflareApiToken = values[Keys[2]],
        };
    }
}
