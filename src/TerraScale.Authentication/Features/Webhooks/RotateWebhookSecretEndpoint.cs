using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Webhooks;

/// <summary><c>POST /v1/{project}/webhook-endpoints/{id}/rotate-secret</c>.</summary>
public sealed class RotateWebhookSecretEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, WebhookEndpoints webhooks,
    StepUpProofs proofs) : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(Root + "/webhook-endpoints/{id}/rotate-secret");
        this.Describe("rotate_webhook_secret", "Rotate a webhook endpoint's secret", "Issues a new whsec_ signing secret and answers it exactly once. The previous secret keeps verifying for 24 hours, then is refused. Needs the admin project role and a rotate_webhook_secret step-up proof.", "Project tenants", null, StatusCodes.Status200OK, typeof(WebhookSecretBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        caller = await proofs.ConsumeHeaderAsync(caller, this.HttpContext.Request, "rotate_webhook_secret", null, cancellationToken);
        var (endpoint, secret) = await webhooks.RotateAsync(projectId, this.RouteId("id"), cancellationToken);
        return Results.Json(new WebhookSecretBody(endpoint.Id, secret, endpoint.PreviousSecretExpiresAt), WebhooksJson.Default.WebhookSecretBody);
    }
}
