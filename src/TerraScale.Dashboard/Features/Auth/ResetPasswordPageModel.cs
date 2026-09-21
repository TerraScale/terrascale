using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Auth;

public sealed record ResetPasswordPageModel(string? Error, bool Sent, bool Reset, bool Updated) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
