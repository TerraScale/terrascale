namespace TerraScale.Dashboard.Features.Access.Models;

public sealed record Invitation(
    string Id,
    string Email,
    string Role,
    string Status,          // Pending | Accepted | Revoked | Expired
    string InvitedByEmail,
    string CreatedAt,
    string? Token = null);
