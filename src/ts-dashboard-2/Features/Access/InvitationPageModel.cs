using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Access;

public sealed record InvitationPageModel(
    string InvitationId,
    string? Error,
    string? Outcome) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
