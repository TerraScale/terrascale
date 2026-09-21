using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Auth;

public sealed record LoginPageModel(string? Error = null) : IPageModel
{
    public ShellModel Shell { get; init; } = null!; // login renders without the shell
}
