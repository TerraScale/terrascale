using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;

namespace TerraBase.Features.Backup;

internal sealed class BackupDestinationStore(SystemDatabase system, DatabaseAccess access, CatalogStore catalog, DatabaseKeyring keyring,
    BackupDestinationProbe probe, TimeProvider time)
{
    public const string Route = "/v1/projects/{project}/backup-destination";

    private static readonly ResourceNotFound Missing = new("no backup destination is set for this project", "backup_destination_not_found");

    public async ValueTask<GetBackupDestinationResult> GetAsync(Caller caller, string? project, CancellationToken cancellationToken)
    {
        var authorized = await AuthorizeAsync(caller, project, Scopes.DatabasesRead, cancellationToken);
        return authorized switch
        {
            ResolvedProject resolved => await GetAsync(resolved.Project, cancellationToken),
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<SetBackupDestinationResult> SetAsync(Caller caller, string? project, BackupDestinationInput input, CancellationToken cancellationToken)
    {
        var authorized = await AuthorizeAsync(caller, project, Scopes.DatabasesWrite, cancellationToken);
        return authorized switch
        {
            ResolvedProject resolved => await SetAsync(resolved.Project, input, cancellationToken),
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DeleteBackupDestinationResult> DeleteAsync(Caller caller, string? project, CancellationToken cancellationToken)
    {
        var authorized = await AuthorizeAsync(caller, project, Scopes.DatabasesWrite, cancellationToken);
        return authorized switch
        {
            ResolvedProject resolved => await DeleteAsync(resolved.Project.Id, cancellationToken),
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ProjectResolution> AuthorizeAsync(
        Caller caller, string? project, string scope, CancellationToken cancellationToken)
    {
        if (caller is ProjectCaller { Database: not null } or ApiKeyCaller { Key.DatabaseId: not null } or EndUserCaller { Database: not null })
        {
            return new ResourceNotFound("the project does not exist", "unknown_project");
        }

        var authorized = await access.ProjectAsync(caller, project, scope, cancellationToken);
        return authorized switch
        {
            ResolvedProject resolved when scope == Scopes.DatabasesWrite => await RequireWritableAsync(resolved, cancellationToken),
            ResolvedProject resolved => resolved,
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ProjectResolution> RequireWritableAsync(
        ResolvedProject resolved, CancellationToken cancellationToken)
    {
        var organization = await catalog.GetOrganizationAsync(resolved.Project.OrganizationId, cancellationToken);
        return organization switch
        {
            OrganizationRecord { Suspended: false } => resolved,
            OrganizationRecord => new CallerForbidden("the organization is suspended", "organization_suspended"),
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<GetBackupDestinationResult> GetAsync(ProjectRecord project, CancellationToken cancellationToken)
    {
        var read = await system.QueryAsync(SqlBuild.Statement(
            "SELECT endpoint, region, bucket, mode, updated_at FROM backup_destinations WHERE project_id = ?", SqlBuild.Id(project.Id)), cancellationToken);
        return read switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> { Count: > 0 } rows => new BackupDestinationBody(
                new Uri(rows[0].TextAt(0)), rows[0].TextAt(1), rows[0].TextAt(2), rows[0].TextAt(3), rows[0].TimeAt(4)),
            IReadOnlyList<IReadOnlyList<SqlValue>> => Missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<SetBackupDestinationResult> SetAsync(ProjectRecord project, BackupDestinationInput input, CancellationToken cancellationToken)
    {
        var validated = BackupDestinationConfig.Validate(input);
        return validated switch
        {
            BackupDestinationConfig destination => await ProbeAndSaveAsync(project, destination, cancellationToken),
            InvalidInput invalid => invalid,
        };
    }

    private async ValueTask<SetBackupDestinationResult> ProbeAndSaveAsync(ProjectRecord project, BackupDestinationConfig destination, CancellationToken cancellationToken)
    {
        if (await probe.ProbeAsync(destination, project.OrganizationId, project.Id, cancellationToken) is { } inaccessible)
        {
            return inaccessible;
        }

        var key = keyring.Derive(project.Id, keyring.ActiveKeyId);
        return key switch
        {
            DatabaseKey material => await SaveAsync(project.Id, destination, material, cancellationToken),
            MasterKeyMissing => new BackendUnavailable("the backup credential key is unavailable", "backup_key_unavailable"),
        };
    }

    private async ValueTask<SetBackupDestinationResult> SaveAsync(Ulid project, BackupDestinationConfig destination, DatabaseKey key, CancellationToken cancellationToken)
    {
        var credentials = Seal(destination, key);
        var updated = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        var written = await system.WriteAsync(SqlBuild.Statement(
            """
            INSERT INTO backup_destinations (project_id, endpoint, region, bucket, mode, updated_at, key_id, credentials)
            SELECT p.id, ?, ?, ?, ?, ?, ?, ?
            FROM projects p JOIN organizations o ON o.id = p.organization_id
            WHERE p.id = ? AND o.suspended = 0
            ON CONFLICT (project_id) DO UPDATE SET endpoint = excluded.endpoint, region = excluded.region, bucket = excluded.bucket,
                mode = excluded.mode, updated_at = excluded.updated_at, key_id = excluded.key_id, credentials = excluded.credentials
            """,
            SqlBuild.Text(destination.Endpoint.AbsoluteUri), SqlBuild.Text(destination.Region), SqlBuild.Text(destination.Bucket),
            SqlBuild.Text(destination.Mode), SqlBuild.Time(updated), SqlBuild.Text(key.KeyId), SqlValue.FromBlob(credentials), SqlBuild.Id(project)),
            ConsistencyMode.Strong, cancellationToken);
        return written switch
        {
            SqlWriteResult { ResultSets: [{ RowsAffected: > 0 }] } => new BackupDestinationBody(destination.Endpoint, destination.Region, destination.Bucket, destination.Mode, updated),
            SqlWriteResult => new StateConflict("the project changed while the bucket was being verified", "project_changed"),
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<DeleteBackupDestinationResult> DeleteAsync(Ulid project, CancellationToken cancellationToken)
    {
        var written = await system.WriteAsync(SqlBuild.Statement(
            "DELETE FROM backup_destinations WHERE project_id = ?", SqlBuild.Id(project)), ConsistencyMode.Strong, cancellationToken);
        return written switch
        {
            SqlWriteResult => new Completed(),
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private static byte[] Seal(BackupDestinationConfig destination, DatabaseKey key)
    {
        const int nonceBytes = 12;
        const int tagBytes = 16;
        Span<byte> sealingKey = stackalloc byte[32];
        byte[]? plaintext = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(new BackupDestinationCredentials(destination.AccessKeyId, destination.SecretKey),
                BackupDestinationJson.Default.BackupDestinationCredentials);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, key.RawKey, sealingKey, salt: default, info: "terrabase/v2/backup-credentials"u8);
            var sealedBytes = new byte[nonceBytes + tagBytes + plaintext.Length];
            var nonce = sealedBytes.AsSpan(0, nonceBytes);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(sealingKey, tagBytes);
            aes.Encrypt(nonce, plaintext, sealedBytes.AsSpan(nonceBytes + tagBytes), sealedBytes.AsSpan(nonceBytes, tagBytes));
            return sealedBytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sealingKey);
            CryptographicOperations.ZeroMemory(key.RawKey);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }
}
