namespace TerraScale.Dashboard2;

// Feature-scoped DashboardStore extensions for Team & Access mutations.
public sealed partial class DashboardStore
{
    public void UpdateMemberRole(string userId, string role)
    {
        var normalized = role is "owner" or "member" or "viewer" ? role : "member";
        TeamMembers = [.. TeamMembers.Select(m => m.UserId == userId ? m with { Role = normalized } : m)];
    }

    public void RemoveMember(string userId) =>
        TeamMembers = [.. TeamMembers.Where(m => m.UserId != userId)];
}
