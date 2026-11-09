using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed class ListWebhookEndpointsEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, WebhookEndpoints webhooks)
    : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/webhook-endpoints");
        this.Describe("list_webhook_endpoints", "List webhook endpoints", "The project's registered webhook endpoints, without secrets. Needs the admin project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(WebhookEndpointPageBody));
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var endpoints = await webhooks.ListAsync(projectId, cancellationToken);
        return Results.Json(new WebhookEndpointPageBody([.. endpoints.Select(endpoint => new WebhookEndpointBody(endpoint.Id, endpoint.Url, endpoint.EventTypes, endpoint.CreatedAt))], null), WebhooksJson.Default.WebhookEndpointPageBody);
    }
}
