using TerraBase.Errors;
using TerraBase.Features.Backup;

namespace TerraBase.Features.Management.Backups;

internal static class BackupDestinationRoute
{
    public const string Path = ManagementRoute.Project + "/backup-destination";
    public const string Description = "One customer S3-compatible bucket per project. mode=both mirrors encrypted backups to both buckets; mode=customer sends project backups only to the customer bucket. Credentials are write-only and stored encrypted in OCI Vault. Replacing or removing a destination schedules its old Vault secret for retirement. Removing the destination resumes platform shipping and preserves customer objects; removing an unset destination returns 204. " + ManagementRoute.IdentityDescription;

    public static Validation? Validate(ProjectBackupDestinationRequest request)
    {
        if (CustomerBackupNetwork.Endpoint(request.Endpoint) is Validation error)
        {
            return error;
        }

        if (!Token(request.Region, 128))
        {
            return new Validation("region must be 1 to 128 characters without whitespace");
        }

        if (request.Bucket is not { Length: >= 3 and <= 63 } bucket
            || !bucket.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
        {
            return new Validation("bucket must be 3 to 63 lowercase letters, digits, dots or hyphens");
        }

        if (!Token(request.AccessKeyId, 512) || !Token(request.SecretKey, 1024))
        {
            return new Validation("accessKeyId and secretKey must be non-empty without whitespace and within their size limits");
        }

        return request.Mode is "both" or BackupDestinationNames.Customer ? null : new Validation("mode must be both or customer");
    }

    private static bool Token(string? value, int maximum) =>
        value is { Length: > 0 } && value.Length <= maximum && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    public static Valid<string> Source(string? source) => source switch
    {
        null or "" or BackupDestinationNames.Platform => BackupDestinationNames.Platform,
        BackupDestinationNames.Customer => BackupDestinationNames.Customer,
        _ => new Validation("destination must be platform or customer"),
    };

    public static bool IsUnavailable(Exception error, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && error is HttpRequestException or IOException or OperationCanceledException;

    public static Unavailable Unavailable() => new("backup bucket or OCI Vault could not be accessed");
}
