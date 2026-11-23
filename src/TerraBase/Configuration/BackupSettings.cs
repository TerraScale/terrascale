namespace TerraBase.Configuration;

public sealed class BackupSettings
{
    internal static readonly string[] Required =
    [
        "backup-endpoint", "backup-region", "backup-bucket", "backup-access-key-id", "backup-secret-key",
    ];

    internal static readonly (string Variable, string Key)[] TigrisVariables =
    [
        ("AWS_ENDPOINT_URL_S3", "backup-endpoint"), ("AWS_REGION", "backup-region"), ("BUCKET_NAME", "backup-bucket"),
        ("AWS_ACCESS_KEY_ID", "backup-access-key-id"), ("AWS_SECRET_ACCESS_KEY", "backup-secret-key"),
    ];

    internal static IEnumerable<string> Keys => [.. Required, "cloudflare-api-token"];

    public required Uri Endpoint { get; init; }

    public required string Region { get; init; }

    public required string Bucket { get; init; }

    public required string AccessKeyId { get; init; }

    public required string SecretKey { get; init; }

    public string? CloudflareApiToken { get; init; }

    public override string ToString() => string.Concat("backup to ", Bucket);

    internal static BackupSettings? Parse(SettingValues values)
    {
        if (!Keys.Any(values.Has))
        {
            return null;
        }

        if (!Required.All(values.Has))
        {
            values.Fail(string.Join(", ", Required) + " must be set together");
            return null;
        }

        if (!Uri.TryCreate(values.Get("backup-endpoint"), UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
            || endpoint.UserInfo.Length > 0 || endpoint.PathAndQuery != "/" || endpoint.Fragment.Length > 0)
        {
            values.Fail("backup-endpoint must be an https://host[:port] URL");
            return null;
        }

        if (values.Get("backup-bucket") is not { Length: >= 3 and <= 63 } bucket || !bucket.All(static character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
        {
            values.Fail("backup-bucket must be 3 to 63 lowercase letters, digits, dots or hyphens");
            return null;
        }

        var r2 = endpoint.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase);
        if (r2 != values.Has("cloudflare-api-token"))
        {
            values.Fail(r2 ? "cloudflare-api-token must be set with an R2 backup-endpoint" : "cloudflare-api-token is used only with an R2 backup-endpoint");
            return null;
        }

        return new BackupSettings
        {
            Endpoint = endpoint,
            Region = values.Token("backup-region"),
            Bucket = bucket,
            AccessKeyId = values.Token("backup-access-key-id"),
            SecretKey = values.Token("backup-secret-key"),
            CloudflareApiToken = r2 ? values.Token("cloudflare-api-token") : null,
        };
    }
}
