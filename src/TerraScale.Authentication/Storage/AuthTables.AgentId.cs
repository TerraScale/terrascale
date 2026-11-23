using TerraScale.Authentication.Features.AgentId;

namespace TerraScale.Authentication.Storage;

public static partial class AuthTables
{
    public static readonly Table<AgentIdClientRecord> AgentIdClients = new(
        "agentid_clients",
        ["id"],
        ["id", "created_at", "name", "owner_user_id", "previous_secret_digest", "previous_secret_expires_at", "redirect_uris", "secret_digest"],
        static row =>
        [
            row.Id,
            AuthDb.Instant(row.CreatedAt),
            row.Name,
            row.OwnerUserId,
            row.PreviousSecretDigest,
            AuthDb.Instant(row.PreviousSecretExpiresAt),
            AuthDb.Json(row.RedirectUris, StorageJson.Default.IReadOnlyListString),
            row.SecretDigest,
        ],
        static row => row.Version);

    public static readonly Table<AgentIdTransactionRecord> AgentIdTransactions = new(
        "agentid_transactions",
        ["id"],
        ["id", "agent_id", "client_id", "completed_at", "created_at", "expires_at", "kid", "owner_user_id", "proved_at", "request_hash"],
        static row =>
        [
            row.Id,
            row.AgentId,
            row.ClientId,
            AuthDb.Instant(row.CompletedAt),
            AuthDb.Instant(row.CreatedAt),
            AuthDb.Instant(row.ExpiresAt),
            row.Kid,
            row.OwnerUserId,
            AuthDb.Instant(row.ProvedAt),
            row.RequestHash,
        ],
        static row => row.Version);
}
