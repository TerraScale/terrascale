using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed class DeleteWebhookEndpointEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, WebhookEndpoints webhooks)
    : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Delete(Root + "/webhook-endpoints/{id}");
        this.Describe("delete_webhook_endpoint", "Delete a webhook endpoint", "Needs the admin project role.", "Project tenants", null, StatusCodes.Status204NoContent, null);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return await webhooks.DeleteAsync(projectId, this.RouteId("id"), cancellationToken) ? Results.NoContent() : throw PlatformDirectory.NotFound();
    }
}
