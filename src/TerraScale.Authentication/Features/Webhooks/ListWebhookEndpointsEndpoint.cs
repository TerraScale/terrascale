using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Webhooks;

/// <summary><c>GET /v1/{project}/webhook-endpoints</c>.</summary>
public sealed class ListWebhookEndpointsEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, WebhookEndpoints webhooks)
    : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(Root + "/webhook-endpoints");
        this.Describe("list_webhook_endpoints", "List webhook endpoints", "The project's registered webhook endpoints, without secrets. Needs the admin project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(WebhookEndpointPageBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var endpoints = await webhooks.ListAsync(projectId, cancellationToken);
        return Results.Json(new WebhookEndpointPageBody([.. endpoints.Select(endpoint => new WebhookEndpointBody(endpoint.Id, endpoint.Url, endpoint.EventTypes, endpoint.CreatedAt))], null), WebhooksJson.Default.WebhookEndpointPageBody);
    }
}
