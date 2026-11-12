using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Features.ProjectOrganizations;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Identity;

public sealed partial class Users
{
    private async Task<IReadOnlyList<Change>?> RemoveOrganizationMembershipsAsync(string project, string userId, CancellationToken cancellationToken)
    {
        var memberships = await data.ReadAsync((db, token) => db.ProjectOrganizationMemberships
            .Where(row => row.ProjectId == project && row.UserId == userId).WithVersion().ToListAsync(token), cancellationToken).ConfigureAwait(false);
        List<Change> changes = [];
        foreach (var membership in memberships)
        {
            var org = await data.ReadAsync((db, token) => db.ProjectOrganizations
                .Where(row => row.ProjectId == project && row.Id == membership.Value.OrganizationId).WithVersion().SingleOrDefaultAsync(token),
                cancellationToken).ConfigureAwait(false);
            if (org is null)
            {
                return null;
            }
            var members = org.Value.Members - 1;
            var admins = org.Value.Admins - (membership.Value.Role == "admin" ? 1 : 0);
            if (admins == 0 && members > 0)
            {
                var successor = await data.ReadAsync((db, token) => db.ProjectOrganizationMemberships
                    .Where(row => row.ProjectId == project && row.OrganizationId == org.Value.Id && row.UserId != userId)
                    .OrderBy(row => row.CreatedAt).ThenBy(row => row.UserId).WithVersion().FirstOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
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
