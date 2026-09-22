using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Auth.Pages.AgentApprove;

public sealed record AgentApprovePageModel(
    DashboardStore.AgentApprovalRequest? Request,
    string? UserCode,
    string? Outcome,
    string? Error) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
