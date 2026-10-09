namespace TerraBase.Features.Management.Backups;

// Metadata only. A claimed create can never publish; unknown names remain until Vault positively identifies them.
internal sealed record BackupSecretWork(int Version, string Id, string? SecretName, string? SecretId, bool Retiring, DateTimeOffset DueAt);
