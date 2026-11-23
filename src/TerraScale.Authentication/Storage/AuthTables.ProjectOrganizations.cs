using TerraScale.Authentication.Features.ProjectOrganizations;

namespace TerraScale.Authentication.Storage;

public static partial class AuthTables
{
    public static readonly Table<ProjectOrganizationDismissal> ProjectOrganizationDismissals = new(
        "project_organization_dismissals",
        ["project_id", "organization_id", "user_id"],
        ["project_id", "organization_id", "user_id"],
        static row =>
        [
            row.ProjectId,
            row.OrganizationId,
            row.UserId,
        ],
        static row => row.Version);

    public static readonly Table<ProjectOrganizationDomain> ProjectOrganizationDomains = new(
        "project_organization_domains",
        ["project_id", "organization_id", "domain"],
        ["project_id", "organization_id", "domain", "checked_at", "failed_checks", "lease_until", "verification_token", "verified_at"],
        static row =>
        [
            row.ProjectId,
            row.OrganizationId,
            row.Domain,
            AuthDb.Instant(row.CheckedAt),
            row.FailedChecks,
            AuthDb.Instant(row.LeaseUntil),
            row.VerificationToken,
            AuthDb.Instant(row.VerifiedAt),
        ],
        static row => row.Version);

    public static readonly Table<ProjectOrganizationInvitation> ProjectOrganizationInvitations = new(
        "project_organization_invitations",
        ["project_id", "organization_id", "id"],
        ["project_id", "organization_id", "id", "created_at", "email", "expires_at", "role", "status", "token_hash"],
        static row =>
        [
            row.ProjectId,
            row.OrganizationId,
            row.Id,
            AuthDb.Instant(row.CreatedAt),
            row.Email,
            AuthDb.Instant(row.ExpiresAt),
            row.Role,
            row.Status,
            row.TokenHash,
        ],
        static row => row.Version);

    public static readonly Table<ProjectOrganizationMembership> ProjectOrganizationMemberships = new(
        "project_organization_memberships",
        ["project_id", "organization_id", "user_id"],
        ["project_id", "organization_id", "user_id", "created_at", "role"],
        static row =>
        [
            row.ProjectId,
            row.OrganizationId,
            row.UserId,
            AuthDb.Instant(row.CreatedAt),
            row.Role,
        ],
        static row => row.Version);

    public static readonly Table<ProjectOrganizationRecord> ProjectOrganizations = new(
        "project_organizations",
        ["project_id", "id"],
        ["project_id", "id", "admins", "created_at", "domain_join", "image_url", "max_members", "members", "name", "private_metadata", "public_metadata", "slug"],
        static row =>
        [
            row.ProjectId,
            row.Id,
            row.Admins,
            AuthDb.Instant(row.CreatedAt),
            row.DomainJoin,
            row.ImageUrl,
            row.MaxMembers,
            row.Members,
            row.Name,
            AuthDb.Json(row.PrivateMetadata, StorageJson.Default.JsonElement),
            AuthDb.Json(row.PublicMetadata, StorageJson.Default.JsonElement),
            row.Slug,
        ],
        static row => row.Version);
}
