using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed class CreateWebhookEndpointEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, WebhookEndpoints webhooks,
    StepUpProofs proofs) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(Root + "/webhook-endpoints");
        this.Describe("create_webhook_endpoint", "Register a webhook endpoint", "Subscribes an https URL to a non-empty subset of the end-user lifecycle events user.signed_up, user.signed_in, user.disabled, user.enabled, user.deleted, user.provider_linked and user.provider_unlinked. The URL is at most 2048 characters, without userinfo or fragment; a project registers at most 3 endpoints (409 webhook_endpoint_limit). The whsec_ signing secret is answered exactly once and stored sealed under the master key. Needs the admin project role and a create_webhook_endpoint step-up proof.", "Project tenants", typeof(CreateWebhookEndpointRequest), StatusCodes.Status201Created, typeof(WebhookEndpointBody), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(WebhooksJson.Default.CreateWebhookEndpointRequest, cancellationToken);
        if (request.Url is not { Length: > 0 and <= 2048 }
            || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            throw Invalid("url must be an absolute https URL of at most 2048 characters, without userinfo or fragment.");
        }

        HashSet<string> events = [.. request.EventTypes ?? []];
        if (events.Count == 0 || events.Count != request.EventTypes!.Count || !events.IsSubsetOf(WebhookEndpoints.LifecycleEvents))
        {
            throw Invalid("event_types must be a non-empty subset of the lifecycle events without duplicates.");
        }

        caller = await proofs.ConsumeHeaderAsync(caller, this.HttpContext.Request, "create_webhook_endpoint", null, cancellationToken);
        var (endpoint, secret) = await webhooks.CreateAsync(projectId, request.Url, [.. events], cancellationToken);
        return Results.Json(new WebhookEndpointBody(endpoint.Id, endpoint.Url, endpoint.EventTypes, endpoint.CreatedAt, secret),
            WebhooksJson.Default.WebhookEndpointBody, statusCode: StatusCodes.Status201Created)
            .WithLocation($"/v1/{projectId}/webhook-endpoints/{endpoint.Id}");
    }
}
