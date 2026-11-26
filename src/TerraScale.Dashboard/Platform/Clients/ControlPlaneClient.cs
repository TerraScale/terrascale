using System.Text.Json;
using static TerraScale.Dashboard.Platform.Clients.ServiceTransport;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed class ControlPlaneClient(ServiceTransport catalog)
{
    public const string HttpClientName = "control-plane";

    public Task<TerraBaseJsonOutcome> ListPlatformDatabasesAsync(CancellationToken cancellationToken) =>
        catalog.SendAsync<JsonElement, TerraBaseJsonOutcome>(ServiceCaller.Operator, HttpMethod.Get, "/v1/platform/databases", ClientJson.Default.JsonElement, cancellationToken);

    public Task<TerraBaseActionOutcome> SetOrganizationPlanAsync(string organizationId, string plan, CancellationToken cancellationToken) =>
        catalog.SendAsync(ServiceCaller.Operator, HttpMethod.Put, string.Concat("/admin/organizations/", Segment(organizationId), "/plan"),
            Json(new TerraBasePlanBody(plan), ClientJson.Default.TerraBasePlanBody), cancellationToken);

    public Task<TerraBaseActionOutcome> SetProjectPlanAsync(string projectId, string plan, CancellationToken cancellationToken) =>
        catalog.SendAsync(ServiceCaller.Operator, HttpMethod.Put, string.Concat("/admin/projects/", Segment(projectId), "/plan"),
            Json(new TerraBasePlanBody(plan), ClientJson.Default.TerraBasePlanBody), cancellationToken);
}
