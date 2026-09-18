using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Auth;

public sealed record ResetPasswordPageModel(string? Error, bool Sent, bool Reset) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
