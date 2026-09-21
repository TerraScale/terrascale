namespace TerraScale.Dashboard.Features.Access.ViewModels;

public sealed record AccessInvitationView(
    string InvitationId,
    string Email,
    string Role,
    string Status,
    string StatusLabel,
    string ExpiresAtText);
