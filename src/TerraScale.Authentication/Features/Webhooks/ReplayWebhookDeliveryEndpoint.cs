using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed class ReplayWebhookDeliveryEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, WebhookHistory history)
    : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(Root + "/webhook-endpoints/{id}/deliveries/{delivery}/replay");
        this.Describe("replay_webhook_delivery", "Replay a webhook delivery with the original event id", "Needs the admin project role.", "Project tenants", null, StatusCodes.Status204NoContent, null, conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        await history.ReplayAsync(projectId, this.RouteId("id"), this.RouteId("delivery"), cancellationToken);
        return Results.NoContent();
    }
}
