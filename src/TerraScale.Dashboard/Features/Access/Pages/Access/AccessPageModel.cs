using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Access.Pages.Access;

public sealed record AccessPageModel(
    bool HasMembers,
    bool CanManageOrganization,
    bool CanManageProject,
    IReadOnlyList<AccessMemberView> Members,
    IReadOnlyList<AccessProjectView> Projects,
    IReadOnlyList<AccessInvitationView> Invitations,
    IReadOnlyList<AccessTeamMemberView> TeamMembers,
    bool FiltersActive,
    string Tab = "overview",
    int Page = 1,
    int PageSize = 10,
    int TotalItems = 0,
    string? Error = null) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public int PageCount => TotalItems is 0 ? 1 : (TotalItems + PageSize - 1) / PageSize;
    public int FirstItem => TotalItems is 0 ? 0 : ((Page - 1) * PageSize) + 1;
    public int LastItem => TotalItems is 0 ? 0 : Math.Min(TotalItems, Page * PageSize);
}
