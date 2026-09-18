using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Auth;

public sealed record LoginPageModel(string? Error = null) : IPageModel
{
    public ShellModel Shell { get; init; } = null!; // login renders without the shell
}
