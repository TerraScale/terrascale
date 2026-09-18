using System.Globalization;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Access;

// --- View records (1:1 with src/features/access/access-data.ts views) ---

public sealed record AccessMemberView(
    string Id,
    string Email,
    string DisplayName,
    bool IsOrganizationOwner,
    string RoleLabel,
    string JoinedAtText,
    string Initials);

public sealed record AccessInvitationView(
    string InvitationId,
    string Email,
    string Role,
    string Status,
    string StatusLabel,
    string ExpiresAtText);

public sealed record AccessTeamMemberView(
    string UserId,
    string Email,
    string DisplayName,
    string Role,
    string JoinedAtText);

public sealed record AccessProjectView(
    string Id,
    string Name,
    bool IsCurrent,
    string CreatedAtText);

public sealed record AccessPageModel(
    string OrganizationLine,
    int MemberCount,
    int GroupCount,
    int ProjectCount,
    IReadOnlyList<AccessMemberView> Members,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<AccessProjectView> Projects,
    IReadOnlyList<AccessInvitationView> Invitations,
    IReadOnlyList<AccessTeamMemberView> TeamMembers,
    bool FiltersActive,
    string? Error = null) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class AccessPageEndpoint(DashboardStore store) : SlicePageEndpoint<AccessSlice, AccessPageModel>(store, "/access")
{
    protected override AccessPageModel Load()
    {
        var query = HttpContext.Request.Query;
        var search = (query["q"].ToString() ?? "").Trim();
        var roleFilter = query["role"].ToString() is "owner" or "member" ? query["role"].ToString() : "all";

        var members = Store.TeamMembers
            .Where(m => roleFilter == "all" || (roleFilter == "owner") == (m.Role == "owner"))
            .Where(m => search.Length == 0
                || m.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || m.Email.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(m => new AccessMemberView(
                Id: m.UserId,
                Email: m.Email,
                DisplayName: m.Name,
                IsOrganizationOwner: m.Role == "owner",
                RoleLabel: m.Role == "owner" ? "Organization Owner" : "Member",
                JoinedAtText: FormatUtc(m.LastActiveAt),
                Initials: Store.Initials(m.Name, m.Email)))
            .ToList();

        var projects = Store.Projects.Select(p => new AccessProjectView(
            p.Id, p.Name, p.Id == Store.ActiveProjectId, FormatUtc(p.CreatedAt))).ToList();

        var invitations = Store.Invitations
            .OrderBy(i => i.Status == "Pending" ? 0 : 1)
            .Select(i => new AccessInvitationView(
                InvitationId: i.Id,
                Email: i.Email,
                Role: "Member",
                Status: i.Status.ToLowerInvariant(),
                StatusLabel: i.Status switch
                {
                    "Pending" => "Pending",
                    "Accepted" => "Accepted",
                    "Revoked" => "Canceled",
                    "Expired" => "Expired",
                    _ => i.Status,
                },
                ExpiresAtText: FormatUtcPlusDays(i.CreatedAt, 7)))
            .ToList();

        var team = Store.TeamMembers.Select(m => new AccessTeamMemberView(
            m.UserId, m.Email, m.Name,
            m.Role == "owner" ? "owner" : m.Role == "admin" ? "member" : "viewer",
            FormatUtc(m.LastActiveAt))).ToList();

        return new AccessPageModel(
            OrganizationLine: $"{Store.ActiveOrganizationName} · {Store.TeamMembers.Length} members · {Store.Projects.Length} projects · loaded {FormatUtc(DateTimeOffset.UtcNow.ToString("O"))}",
            MemberCount: members.Count,
            GroupCount: team.Count,
            ProjectCount: projects.Count,
            Members: members,
            Roles: ["organization:owner", "project:admin"],
            Permissions: ["members:read", "invitations:write", "projects:read", "usage:read"],
            Projects: projects,
            Invitations: invitations,
            TeamMembers: team,
            FiltersActive: search.Length > 0 || roleFilter != "all",
            Error: query["error"].FirstOrDefault())
        {
            Shell = ShellFactory.Create(Store, PageRoute),
        };
    }

    internal static string FormatUtc(string iso)
    {
        if (iso.Length == 0 || !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return "Unknown";
        }

        return date.UtcDateTime.ToString("MMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture);
    }

    private static string FormatUtcPlusDays(string iso, int days)
    {
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return "Unknown";
        }

        return FormatUtc(date.AddDays(days).ToString("O"));
    }
}
