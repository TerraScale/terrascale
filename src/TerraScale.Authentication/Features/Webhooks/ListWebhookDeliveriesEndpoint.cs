using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed class ListWebhookDeliveriesEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, WebhookHistory history)
    : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/webhook-endpoints/{id}/deliveries");
        this.Describe("list_webhook_deliveries", "List recent webhook deliveries and attempts", "Needs the admin project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(WebhookDeliveryPage));
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return Results.Json(await history.DeliveriesAsync(projectId, this.RouteId("id"), cancellationToken), WebhooksJson.Default.WebhookDeliveryPage);
    }
}
