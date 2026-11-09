using TerraBase.Errors;

namespace TerraBase.Features.Backup;

public static class BackupDestinationNames
{
    public const string Platform = "platform";

    public const string Customer = "customer";
}

public sealed record ProjectBackupDestination(Uri Endpoint, string Region, string Bucket, string Mode, DateTimeOffset UpdatedAt);

public sealed record ProjectBackupDestinationRequest(string? Endpoint, string? Region, string? Bucket, string? AccessKeyId, string? SecretKey, string? Mode)
{
    public override string ToString() => "customer backup destination request";
}

public sealed record ProjectBackupDestinationRecord(int Version, string Revision, ProjectBackupDestination Destination, SealedCredentials Credentials);

public readonly union ReadBackupDestinationResult(ProjectBackupDestinationRecord, NotFound, Unavailable);

public readonly union SaveBackupDestinationResult(ProjectBackupDestination, NotFound, Forbidden, Unavailable);

public readonly union DeleteBackupDestinationResult(Done, NotFound, Forbidden, Unavailable);

public interface IProjectBackupDestinations
{
    ValueTask<ReadBackupDestinationResult> ReadAsync(string organizationId, string projectId, CancellationToken cancellationToken);

    Task<SaveBackupDestinationResult> SaveAsync(string userId, string organizationId, string projectId, ProjectBackupDestinationRequest request, CancellationToken cancellationToken);

    Task<DeleteBackupDestinationResult> DeleteAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken);
}
