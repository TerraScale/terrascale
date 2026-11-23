using Dapper;
using TerraScale.Authentication.Features.ProjectOrganizations;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Identity;

public sealed partial class Users
{
    private async Task<IReadOnlyList<Change>?> RemoveOrganizationMembershipsAsync(string project, string userId, CancellationToken cancellationToken)
    {
        var memberships = Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<ProjectOrganizationMembership>(
            "SELECT * FROM project_organization_memberships WHERE project_id = @project AND user_id = @userId", new { project, userId }), cancellationToken));
        List<Change> changes = [];
        foreach (var membership in memberships)
        {
            var organizationId = membership.Value.OrganizationId;
            var org = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectOrganizationRecord>(
                "SELECT * FROM project_organizations WHERE project_id = @project AND id = @organizationId", new { project, organizationId }), cancellationToken));
            if (org is null)
            {
                return null;
            }
            var members = org.Value.Members - 1;
            var admins = org.Value.Admins - (membership.Value.Role == "admin" ? 1 : 0);
            if (admins == 0 && members > 0)
            {
                var successor = Versioned.Of(await data.ReadAsync(connection => connection.QueryFirstOrDefaultAsync<ProjectOrganizationMembership>(
                    """
                    SELECT * FROM project_organization_memberships WHERE project_id = @project AND organization_id = @organizationId AND user_id <> @userId
                    ORDER BY created_at, user_id LIMIT 1
                    """, new { project, organizationId, userId }), cancellationToken));
                if (successor is null)
                {
                    return null;
                }

                changes.Add(Change.Update(successor, successor.Value with { Role = "admin" }));
                changes.Add(Change.Insert(outbox.NewEvent(project, "organizationMembership.updated", successor.Value.UserId,
                    organizationId: org.Value.Id, organizationRole: "admin")));
                admins = 1;
            }

            changes.Add(Change.Update(org, org.Value with { Members = members, Admins = admins }));
            changes.Add(Change.Delete(membership));
            changes.Add(Change.Insert(outbox.NewEvent(project, "organizationMembership.deleted", userId,
                organizationId: org.Value.Id, organizationRole: membership.Value.Role)));
        }

        return changes;
    }
}
