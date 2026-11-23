using Dapper;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Profile;

public sealed class DeletionRelatedRecords(DashboardDb storage, AccountStore accounts)
{
    private const string EraseOrganization = """
        DELETE FROM internal_billing WHERE organization_id = @OrganizationId;
        DELETE FROM billing_changes WHERE organization_id = @OrganizationId;
        DELETE FROM background_jobs WHERE id = @Plan OR id IN (SELECT 'allowance/' || id FROM allowance_alerts WHERE organization_id = @OrganizationId);
        DELETE FROM allowance_alerts WHERE organization_id = @OrganizationId
        """;

    private const string EraseProject = "DELETE FROM background_jobs WHERE id LIKE @Prefix || '%' OR id = @Alerts";

    public Task CleanAccountAsync(string subject, CancellationToken cancellationToken) =>
        Data.SqlWorkspace.EraseUserAsync(storage, subject, cancellationToken);

    public async Task CleanOrganizationAsync(string organizationId, CancellationToken cancellationToken)
    {
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(EraseOrganization, new { OrganizationId = organizationId, Plan = "plan/" + organizationId });
        }

        await accounts.RemoveNotificationsAsync(organizationId, null, cancellationToken);
    }

    public async Task CleanProjectAsync(string organizationId, string projectId, CancellationToken cancellationToken)
    {
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(EraseProject, new { Prefix = "log-drain/" + projectId + "/", Alerts = "alerts/" + projectId });
        }

        await accounts.RemoveNotificationsAsync(organizationId, projectId, cancellationToken);
    }
}
