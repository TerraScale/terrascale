using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Access;

public sealed record AccessPageModel(
    int MemberCount,
    int GroupCount,
    int ProjectCount,
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
