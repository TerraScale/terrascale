using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Access.Pages.Access;

public sealed record AccessPageModel(
    bool HasMembers,
    bool CanManageOrganization,
    bool CanManageProject,
    IReadOnlyList<AccessMemberView> Members,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<AccessProjectView> Projects,
    IReadOnlyList<AccessInvitationView> Invitations,
    IReadOnlyList<AccessTeamMemberView> TeamMembers,
    bool FiltersActive,
    string? Error = null) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
