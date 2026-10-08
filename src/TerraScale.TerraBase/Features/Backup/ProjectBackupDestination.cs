using TerraBase.Errors;

namespace TerraBase.Features.Backup;

/// <summary>The public names of the continuous backup destinations.</summary>
public static class BackupDestinationNames
{
    /// <summary>The built-in bucket; also the platform target's revision.</summary>
    public const string Platform = "platform";

    /// <summary>The project's configured S3-compatible bucket.</summary>
    public const string Customer = "customer";
}

/// <summary>A project's customer bucket. Credentials are held only in OCI Vault.</summary>
/// <param name="Endpoint">S3-compatible HTTPS endpoint.</param>
/// <param name="Region">S3 signing region.</param>
/// <param name="Bucket">Customer bucket name.</param>
/// <param name="Mode"><c>both</c> or <c>customer</c>.</param>
/// <param name="UpdatedAt">When the destination was saved.</param>
public sealed record ProjectBackupDestination(Uri Endpoint, string Region, string Bucket, string Mode, DateTimeOffset UpdatedAt);

/// <summary>Configures one customer bucket; credentials are write-only.</summary>
/// <param name="Endpoint">Public S3-compatible HTTPS endpoint.</param>
/// <param name="Region">S3 signing region.</param>
/// <param name="Bucket">Customer bucket name.</param>
/// <param name="AccessKeyId">Write-only S3 access key.</param>
/// <param name="SecretKey">Write-only S3 secret key.</param>
/// <param name="Mode"><c>both</c> or <c>customer</c>.</param>
public sealed record ProjectBackupDestinationRequest(string? Endpoint, string? Region, string? Bucket, string? AccessKeyId, string? SecretKey, string? Mode)
{
    /// <summary>Never includes credentials.</summary>
    public override string ToString() => "customer backup destination request";
}

/// <summary>The versioned catalog record read by the shipper, never returned through HTTP.</summary>
/// <param name="Version">Persisted format, currently 1.</param>
/// <param name="Revision">Unique revision; changing it starts a new generation.</param>
/// <param name="Destination">Public destination settings.</param>
/// <param name="SecretId">OCI Vault reference; no credential material is persisted in TerraBase.</param>
public sealed record ProjectBackupDestinationRecord(int Version, string Revision, ProjectBackupDestination Destination, string SecretId);

/// <summary>A saved destination, or why it could not be saved.</summary>
public readonly union SaveBackupDestinationResult(ProjectBackupDestination, NotFound, Forbidden, Unavailable);

/// <summary>A detached destination, or why it could not be detached.</summary>
public readonly union DeleteBackupDestinationResult(Done, NotFound, Forbidden);

/// <summary>Project backup configuration owned by TerraBase.</summary>
public interface IProjectBackupDestinations
{
    /// <summary>Reads the catalog record for a shipper or restore; credentials stay in OCI Vault.</summary>
    ValueTask<ProjectBackupDestinationRecord?> ReadAsync(string organizationId, string projectId, CancellationToken cancellationToken);

    /// <summary>Validates bucket access, creates a Vault secret, and saves the destination with a fresh authorization check.</summary>
    Task<SaveBackupDestinationResult> SaveAsync(string userId, string organizationId, string projectId, ProjectBackupDestinationRequest request, CancellationToken cancellationToken);

    /// <summary>Detaches a customer bucket, preserves its objects, schedules its Vault secret for retirement, and resumes platform shipping.</summary>
    Task<DeleteBackupDestinationResult> DeleteAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken);
}
