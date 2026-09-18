namespace TerraScale.Dashboard2;

public sealed record DashboardUser(
    string Id,
    string Name,
    string Email,
    string Role,            // owner | admin | member
    string Status,          // Active | Suspended | Invited
    bool MfaEnabled,
    string? AvatarInitials = null,
    string CreatedAt = "2026-01-12T09:24:00Z",
    string? Provider = null,      // password | github | google | sso
    string? SupportNote = null,
    string OrganizationName = "TerraScale Labs");
