using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraBase.Features.Backup;

public sealed record BackupDestinationInput(string? Endpoint, string? Region, string? Bucket, string? AccessKeyId, string? SecretKey, string? Mode)
{
    public override string ToString() => "Customer backup destination request";
}

public sealed record BackupDestinationBody(Uri Endpoint, string Region, string Bucket, string Mode, DateTimeOffset UpdatedAt);

public readonly union GetBackupDestinationResult(BackupDestinationBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable);

public readonly union SetBackupDestinationResult(BackupDestinationBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable);

public readonly union DeleteBackupDestinationResult(Done, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable);

internal sealed record BackupDestinationConfig(Uri Endpoint, string Region, string Bucket, string AccessKeyId, string SecretKey, string Mode)
{
    public override string ToString() => "Customer backup destination configuration";

    public static BackupDestinationValidation Validate(BackupDestinationInput input)
    {
        if (BackupDestinationNetwork.Endpoint(input.Endpoint) is not { } endpoint)
        {
            return new Validation("endpoint must be a public https://host[:port] URL", "invalid_endpoint");
        }

        if (input.Region is not { } region || !Token(region, 128))
        {
            return new Validation("region must be 1 to 128 characters without whitespace", "invalid_region");
        }

        if (input.Bucket is not { Length: >= 3 and <= 63 } bucket
            || !bucket.All(static character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
        {
            return new Validation("bucket must be 3 to 63 lowercase letters, digits, dots or hyphens", "invalid_bucket");
        }

        if (input.AccessKeyId is not { } accessKeyId || input.SecretKey is not { } secretKey || !Token(accessKeyId, 512) || !Token(secretKey, 1024))
        {
            return new Validation("accessKeyId and secretKey must be non-empty without whitespace and within their size limits", "invalid_credentials");
        }

        return input.Mode is "both" or "customer"
            ? new BackupDestinationConfig(endpoint, region, bucket, accessKeyId, secretKey, input.Mode)
            : new Validation("mode must be both or customer", "invalid_mode");
    }

    private static bool Token(string value, int maximum) =>
        value.Length is > 0 && value.Length <= maximum && !value.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character));
}

internal readonly union BackupDestinationValidation(BackupDestinationConfig, Validation);

internal sealed record BackupDestinationCredentials(string AccessKeyId, string SecretKey)
{
    public override string ToString() => "Customer backup credentials";
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BackupDestinationBody))]
[JsonSerializable(typeof(BackupDestinationInput))]
[JsonSerializable(typeof(BackupDestinationCredentials))]
internal sealed partial class BackupDestinationJson : JsonSerializerContext;
