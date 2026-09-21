namespace TerraScale.Dashboard.Features.Access;

// View records (1:1 with src/features/access/access-data.ts views)
public sealed record AccessMemberView(
    string Id,
    string Email,
    string DisplayName,
    bool IsOrganizationOwner,
    string RoleLabel,
    string JoinedAtText,
    string Initials);
