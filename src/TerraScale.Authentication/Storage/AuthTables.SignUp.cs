using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Storage;

public static partial class AuthTables
{
    public static readonly Table<ProjectInvitationRecord> ProjectInvitations = new(
        "project_invitations",
        ["id"],
        ["id", "created_at", "email", "expires_at", "project_id", "redirect_url", "status", "token_hash"],
        static row =>
        [
            row.Id,
            AuthDb.Instant(row.CreatedAt),
            row.Email,
            AuthDb.Instant(row.ExpiresAt),
            row.ProjectId,
            row.RedirectUrl,
            row.Status,
            row.TokenHash,
        ],
        static row => row.Version);

    public static readonly Table<WaitlistRecord> Waitlist = new(
        "project_waitlist",
        ["id"],
        ["id", "created_at", "email", "project_id", "status"],
        static row =>
        [
            row.Id,
            AuthDb.Instant(row.CreatedAt),
            row.Email,
            row.ProjectId,
            row.Status,
        ],
        static row => row.Version);
}
