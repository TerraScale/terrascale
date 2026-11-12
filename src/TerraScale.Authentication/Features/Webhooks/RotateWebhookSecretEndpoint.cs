using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed class RotateWebhookSecretEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, WebhookEndpoints webhooks,
    StepUpProofs proofs) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(Root + "/webhook-endpoints/{id}/rotate-secret");
        this.Describe("rotate_webhook_secret", "Rotate a webhook endpoint's secret", "Issues a new whsec_ signing secret and answers it exactly once. The previous secret keeps verifying for 24 hours, then is refused. A concurrent change to the endpoint answers 409 version_conflict. Needs the admin project role and a rotate_webhook_secret step-up proof.", "Project tenants", null, StatusCodes.Status200OK, typeof(WebhookSecretBody), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        _ = caller.Person;
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var prepared = await proofs.PrepareHeaderAsync(caller.Person, this.HttpContext.Request, "rotate_webhook_secret", null, cancellationToken) ?? throw StepUpProofs.Required();
        var (endpoint, secret) = await webhooks.RotateAsync(projectId, this.RouteId("id"), prepared.Operations, cancellationToken);
        return Results.Json(new WebhookSecretBody(endpoint.Id, secret, endpoint.PreviousSecretExpiresAt), WebhooksJson.Default.WebhookSecretBody);
    }
}
