namespace TerraScale.Dashboard2.Features.Access;

public sealed record AccessInvitationView(
    string InvitationId,
    string Email,
    string Role,
    string Status,
    string StatusLabel,
    string ExpiresAtText);
