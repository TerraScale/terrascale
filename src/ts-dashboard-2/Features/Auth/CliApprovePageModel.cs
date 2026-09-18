using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Auth;

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
