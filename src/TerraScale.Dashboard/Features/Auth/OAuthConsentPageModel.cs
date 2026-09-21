using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Auth;

public sealed record OAuthConsentPageModel(
    bool ValidRequest,
    string ClientId,
    string ClientName,
    string[] Scopes,
    string? Claims,
    string? Error,
    string? Outcome) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
