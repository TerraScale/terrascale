using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class UpdateProjectClientEndpoint(TenantDirectory tenants, IOpenIddictApplicationManager applications, PlatformAuthentication authentication,
    ProjectAuthSettings settings, StepUpProofs proofs) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Put(Root + "/clients/{client_id}");
        this.Describe("update_project_client", "Update an OAuth client", "Changes a client's display name and redirect URIs; its client_id stays the same and no secret is answered. redirect_uris holds 1 to 10 absolute https URLs (http only on loopback hosts) without userinfo or fragments, and each URI must additionally exactly match an entry in the project's redirect allowlist when nonempty. Needs the admin project role.", "Project tenants", typeof(UpdateClientRequest), StatusCodes.Status200OK, typeof(ClientBody));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        _ = caller.Person;
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(ProjectTenantJson.Default.UpdateClientRequest, cancellationToken);
        var name = PlatformDirectory.Name(request.Name) ?? throw Invalid("name must be 1 to 128 characters with a letter or digit.");
        List<Uri> redirects = [.. (request.RedirectUris ?? []).Select(value => ClientRedirect(value) ?? throw Invalid("Each redirect URI must be an absolute https URL, or http on a loopback host, without userinfo or fragments."))];
        if (redirects.Count is < 1 or > 10)
        {
            throw Invalid("redirect_uris must hold 1 to 10 URIs.");
        }

        foreach (var redirect in request.RedirectUris!)
        {
            if (!await OidcResults.AllowsRedirectAsync(settings, projectId, redirect, cancellationToken))
            {
                throw Invalid("Each redirect URI must also exactly match the project's redirect allowlist.");
            }
        }

        _ = await proofs.ConsumeHeaderAsync(caller.Person, this.HttpContext.Request, "update_oauth_client", null, cancellationToken);
        var previous = this.HttpContext.Items[Tenancy.ItemKey];
        this.HttpContext.Items[Tenancy.ItemKey] = projectId;
        try
        {
            if (await applications.FindByClientIdAsync(this.RouteId("client_id"), cancellationToken) is not OidcApplication application)
            {
                throw PlatformDirectory.NotFound();
            }

            var descriptor = new OpenIddictApplicationDescriptor();
            await applications.PopulateAsync(descriptor, application, cancellationToken);
            descriptor.DisplayName = name;
            descriptor.RedirectUris.Clear();
            descriptor.RedirectUris.UnionWith(redirects);
            descriptor.PostLogoutRedirectUris.Clear();
            descriptor.PostLogoutRedirectUris.UnionWith(redirects);
            await applications.UpdateAsync(application, descriptor, cancellationToken);
            return Results.Json(Map(application), ProjectTenantJson.Default.ClientBody);
        }
        finally
        {
            this.HttpContext.Items[Tenancy.ItemKey] = previous;
        }
    }
}
