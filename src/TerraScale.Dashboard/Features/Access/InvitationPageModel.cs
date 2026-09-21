using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Access;

public sealed record InvitationPageModel(
    string InvitationId,
    string? Error,
    string? Outcome) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
