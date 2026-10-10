using TerraBase.Errors;

namespace TerraBase.Features.Management.Backups;

/// <summary>An uncertain-intent page, or an unavailable catalog.</summary>
public readonly union ListBackupSecretRecoveryResult(BackupSecretRecoveryPage, Unavailable);

/// <summary>Whether an absent intent was removed, or an unavailable catalog.</summary>
public readonly union ConfirmBackupSecretAbsentResult(bool, Unavailable);

/// <summary>Operator recovery of uncertain creates after independent OCI investigation.</summary>
public interface IBackupSecretRecovery
{
    /// <summary>Reads at most eight metadata-only uncertain intents after the supplied ULID.</summary>
    ValueTask<ListBackupSecretRecoveryResult> ListAsync(string? after, CancellationToken cancellationToken);

    /// <summary>Removes an unknown, fenced intent only after the operator confirms absence and OCI still reports no matching secret.</summary>
    Task<ConfirmBackupSecretAbsentResult> ConfirmAbsentAsync(string id, string secretName, CancellationToken cancellationToken);
}

/// <summary>An uncertain create's identity; recoverable means publication has already been fenced.</summary>
public sealed record BackupSecretRecoveryIntent(string Id, string SecretName, bool Recoverable);

/// <summary>A bounded operator page and its next ULID, or null at the end.</summary>
public sealed record BackupSecretRecoveryPage(IReadOnlyList<BackupSecretRecoveryIntent> Items, string? Next);

/// <summary>Confirms that OCI investigation found no accepted or still-running create with this exact name.</summary>
public sealed record BackupSecretRecoveryRequest(string SecretName, bool ConfirmAbsent);
