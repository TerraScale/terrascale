using TerraScale.Authentication.Features.Impersonation;

namespace TerraScale.Authentication.Storage;

public static partial class AuthTables
{
    public static readonly Table<ImpersonationRecord> Impersonations = new(
        "impersonations",
        ["id"],
        ["id", "actor_email", "actor_epoch", "actor_id", "actor_stamp", "client_id", "cookie_hash", "created_at", "ended_at", "expires_at", "instance_id", "organization_id", "reason", "redeemed_at", "ticket_expires_at", "ticket_hash", "ticket_owner", "user_epoch", "user_id", "user_stamp"],
        static row =>
        [
            row.Id,
            row.ActorEmail,
            row.ActorEpoch,
            row.ActorId,
            row.ActorStamp,
            row.ClientId,
            row.CookieHash,
            AuthDb.Instant(row.CreatedAt),
            AuthDb.Instant(row.EndedAt),
            AuthDb.Instant(row.ExpiresAt),
            row.InstanceId,
            row.OrganizationId,
            row.Reason,
            AuthDb.Instant(row.RedeemedAt),
            AuthDb.Instant(row.TicketExpiresAt),
            row.TicketHash,
            row.TicketOwner,
            row.UserEpoch,
            row.UserId,
            row.UserStamp,
        ],
        static row => row.Version);
}
