namespace TerraScale.Dashboard.Features.Access;

public sealed record AccessTeamMemberView(
    string UserId,
    string Email,
    string DisplayName,
    string Role,
    string JoinedAtText);
