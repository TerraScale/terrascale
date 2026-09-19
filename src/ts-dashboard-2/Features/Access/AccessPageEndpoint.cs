using System.Globalization;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Access;

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

        var canManageOrganization = Store.CurrentUser.Role == "owner";
        var canManageProject = canManageOrganization
            || Store.TeamMembers.Any(m => m.UserId == Store.CurrentUser.Id && m.Role is "owner" or "admin");

        return new AccessPageModel(
            MemberCount: members.Count,
            GroupCount: team.Count,
            ProjectCount: projects.Count,
            HasMembers: Store.TeamMembers.Length > 0,
            CanManageOrganization: canManageOrganization,
            CanManageProject: canManageProject,
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
