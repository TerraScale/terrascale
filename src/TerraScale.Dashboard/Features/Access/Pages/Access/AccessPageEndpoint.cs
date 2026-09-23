using System.Globalization;
using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Access.Pages.Access;

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
                Initials: Initials(m.Name, m.Email)))
            .ToList();

        var projects = Store.Projects.Select(p => new AccessProjectView(
            p.Id, p.Name, p.Id == Store.ActiveProjectId, FormatUtc(p.CreatedAt))).ToList();

        var invitations = Store.Invitations
            // The seeded 01J… token exists so /invitations renders; it has no
            // invitee email and never appears in v1's organization list.
            .Where(i => i.Email.Length > 0)
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
            HasMembers: Store.TeamMembers.Length > 0,
            CanManageOrganization: canManageOrganization,
            CanManageProject: canManageProject,
            Members: members,
            Projects: projects,
            Invitations: invitations,
            TeamMembers: team,
            FiltersActive: search.Length > 0 || roleFilter != "all",
            Error: query["error"].FirstOrDefault())
        {
            Shell = ShellFactory.Create(Store, PageRoute),
        };
    }

    // v1 initials (AccessMembersTable.tsx): split on [ .@_-]+, first two
    // parts, "TS" fallback — not the store's first+last helper.
    private static string Initials(string name, string email)
    {
        var source = name.Trim().Length > 0 ? name : email;
        var parts = System.Text.RegularExpressions.Regex.Split(source, @"[ .@_-]+").Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0)
        {
            return "TS";
        }

        return string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
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
