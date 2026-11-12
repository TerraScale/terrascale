using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed class TestWebhookEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, WebhookHistory history)
    : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(Root + "/webhook-endpoints/{id}/test");
        this.Describe("test_webhook", "Send a signed test event to a webhook endpoint", "Needs the admin project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(WebhookEventBody), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return Results.Json(await history.TestAsync(projectId, this.RouteId("id"), cancellationToken), WebhooksJson.Default.WebhookEventBody);
    }
}
