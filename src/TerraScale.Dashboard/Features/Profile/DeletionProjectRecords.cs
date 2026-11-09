using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class DeletionProjectRecords(IDbContextFactory<DashboardDbContext> storage)
    {
        public async Task CleanAsync(string organizationId, string projectId, CancellationToken cancellationToken)
        {
            if (!TerraScaleIds.IsValid(organizationId) || !TerraScaleIds.IsValid(projectId))
            {
                throw new IOException("Project cleanup scope is invalid.");
            }

            await SqlWorkspace.EraseAsync(storage, row => row.OrganizationId == organizationId && row.ProjectId == projectId, cancellationToken);
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            await db.SavedQueries.Where(row => row.OrganizationId == organizationId && row.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.AlertRules.Where(row => row.OrganizationId == organizationId && row.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.LogDrains.Where(row => row.OrganizationId == organizationId && row.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
