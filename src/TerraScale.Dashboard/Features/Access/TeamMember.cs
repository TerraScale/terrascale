namespace TerraScale.Dashboard;

public sealed record TeamMember(
    string UserId,
    string Name,
    string Email,
    string Role,
    string Status,
    bool MfaEnabled,
    string LastActiveAt,
    int SessionCount);
