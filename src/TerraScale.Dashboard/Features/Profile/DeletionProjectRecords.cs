using Dapper;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Profile;

public sealed class DeletionProjectRecords(DashboardDb storage)
{
    private const string Erase = """
        DELETE FROM saved_queries WHERE organization_id = @OrganizationId AND project_id = @ProjectId;
        DELETE FROM alert_events WHERE rule_id IN (SELECT id FROM alert_rules WHERE organization_id = @OrganizationId AND project_id = @ProjectId);
        DELETE FROM alert_rules WHERE organization_id = @OrganizationId AND project_id = @ProjectId;
        DELETE FROM log_drain_cursors WHERE drain_id IN (SELECT id FROM log_drains WHERE organization_id = @OrganizationId AND project_id = @ProjectId);
        DELETE FROM log_drains WHERE organization_id = @OrganizationId AND project_id = @ProjectId
        """;

    public async Task CleanAsync(string organizationId, string projectId, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(organizationId) || !TerraScaleIds.IsValid(projectId))
        {
            throw new IOException("Project cleanup scope is invalid.");
        }

        await SqlWorkspace.EraseProjectAsync(storage, organizationId, projectId, cancellationToken);
        await using var connection = await storage.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(Erase, new { OrganizationId = organizationId, ProjectId = projectId });
    }
}
