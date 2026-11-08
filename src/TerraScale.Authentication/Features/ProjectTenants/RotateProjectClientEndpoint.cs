using Microsoft.AspNetCore.Http.Features;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class RotateProjectClientEndpoint(TenantDirectory tenants, IOpenIddictApplicationManager applications, PlatformAuthentication authentication,
    StepUpProofs proofs, TimeProvider clock) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(Root + "/clients/{client_id}/secret/rotate");
        this.Describe("rotate_project_client_secret", "Rotate an OAuth client's secret", "Issues a new client_secret for a confidential client and answers it exactly once; only its OpenIddict hash is kept. The previous secret keeps working for grace_seconds (default 3600, 0 to 86400), then is refused like any wrong secret; rotating again inside the grace replaces the previous secret. Needs the admin project role.", "Project tenants", typeof(RotateClientRequest), StatusCodes.Status200OK, typeof(ClientSecretBody));
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var sent = this.HttpContext.Request;
        var empty = sent.ContentLength is 0
            || sent.ContentLength is null && this.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true;
        var body = empty ? new RotateClientRequest(null) : await this.BodyAsync(ProjectTenantJson.Default.RotateClientRequest, cancellationToken);
        var grace = TimeSpan.FromSeconds(body.GraceSeconds ?? 3600);
        if (grace < TimeSpan.Zero || grace > TimeSpan.FromDays(1))
        {
            throw Invalid("grace_seconds must be 0 to 86400 seconds.");
        }

        caller = await proofs.ConsumeHeaderAsync(caller, this.HttpContext.Request, "rotate_oauth_client", cancellationToken);
        var previous = this.HttpContext.Items[Tenancy.ItemKey];
        this.HttpContext.Items[Tenancy.ItemKey] = projectId;
        try
        {
            if (await applications.FindByClientIdAsync(this.RouteId("client_id"), cancellationToken) is not OidcApplication application
                || application.InstanceId != projectId)
            {
                throw PlatformDirectory.NotFound();
            }

            if (application.ClientType != ClientTypes.Confidential)
            {
                throw Invalid("type must be confidential.");
            }

            application.PreviousSecretHash = grace > TimeSpan.Zero ? application.ClientSecret : null;
            application.PreviousSecretExpiresAt = grace > TimeSpan.Zero ? clock.GetUtcNow() + grace : null;
            var secret = Tokens.RandomToken(48);
            var descriptor = new OpenIddictApplicationDescriptor();
            await applications.PopulateAsync(descriptor, application, cancellationToken);
            descriptor.ClientSecret = secret;
            await applications.UpdateAsync(application, descriptor, cancellationToken);
            await this.Tenants.AuditClientRotationAsync(caller.Id, projectId, application.ClientId!, cancellationToken);
            return Results.Json(new ClientSecretBody(application.ClientId!, secret, application.PreviousSecretExpiresAt), ProjectTenantJson.Default.ClientSecretBody);
        }
        finally
        {
            this.HttpContext.Items[Tenancy.ItemKey] = previous;
        }
    }
}
