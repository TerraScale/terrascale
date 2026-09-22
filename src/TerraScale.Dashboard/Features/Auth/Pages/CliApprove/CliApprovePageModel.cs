using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Auth.Pages.CliApprove;

public sealed record CliApprovePageModel(
    bool Authenticated,
    string? Code,
    string? Error,
    string? Outcome,
    string[] Scopes,
    string[] Resources,
    string AccountEmail) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
