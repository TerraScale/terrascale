using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
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

    protected override async Task<Answer> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        if (caller.RequirePerson() is not UserRecord person)
        {
            return ErrorResults.ToResult(ProjectCaller.PersonRequired);
        }

        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var prepared = await proofs.PrepareHeaderAsync(person, this.HttpContext.Request, "rotate_webhook_secret", null, cancellationToken) ?? throw StepUpProofs.Required();
        return await webhooks.RotateAsync(projectId, this.RouteId("id"), prepared.Operations, cancellationToken) switch
        {
            WebhookEndpointSecret issued => Results.Json(new WebhookSecretBody(issued.Endpoint.Id, issued.Secret, issued.Endpoint.PreviousSecretExpiresAt), WebhooksJson.Default.WebhookSecretBody),
            NotFound failure => ErrorResults.ToResult(failure),
            Forbidden failure => ErrorResults.ToResult(failure),
            Conflict failure => ErrorResults.ToResult(failure),
        };
    }
}
