namespace TerraBase.Configuration;

public sealed class FileStorageSettings
{
    internal static string[] Keys => ["files-s3-endpoint", "files-s3-access-key-id", "files-s3-secret-access-key"];

    public required Uri Endpoint { get; init; }

    public required string AccessKeyId { get; init; }

    public required string SecretKey { get; init; }

    public override string ToString() => "customer file storage";

    internal static FileStorageSettings? Parse(IReadOnlyDictionary<string, string> values)
    {
        if (!Keys.All(key => values.TryGetValue(key, out var value) && value.Length is > 0 and <= 512
            && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            || !Uri.TryCreate(values["files-s3-endpoint"], UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || !endpoint.IsDefaultPort || endpoint.UserInfo.Length != 0
            || endpoint.PathAndQuery != "/" || endpoint.Fragment.Length != 0)
        {
            return null;
        }

        return new FileStorageSettings
        {
            Endpoint = endpoint,
            AccessKeyId = values["files-s3-access-key-id"],
            SecretKey = values["files-s3-secret-access-key"],
        };
    }
}
