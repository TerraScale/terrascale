using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
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
        this.Describe("update_project_client", "Update an OAuth client", "Changes a client's display name and redirect URIs, or a machine app's allowed_scopes, allowed_audiences and token_lifetime_minutes (5 to 60); machine apps require no redirect URIs; its client_id stays the same and no secret is answered. redirect_uris holds 1 to 10 absolute https URLs (http only on loopback hosts) without userinfo or fragments, and each URI must additionally exactly match an entry in the project's redirect allowlist when nonempty. allowed_origins holds at most 10 exact browser origins (https://host[:port], or http on a loopback host; lowercase, no default port, path, trailing slash, wildcard, userinfo, query or fragment) whose cross-origin token, userinfo, revocation and guest requests get CORS; allow_anonymous (default false) admits guests at /{project}/oauth/anonymous. Machine apps take neither. An omitted allowed_origins or allow_anonymous stays unchanged. Needs the admin project role.", "Project tenants", typeof(UpdateClientRequest), StatusCodes.Status200OK, typeof(ClientBody));
    }

    protected override async Task<Answer> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var person = caller.RequirePerson();
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(ProjectTenantJson.Default.UpdateClientRequest, cancellationToken);
        var name = PlatformDirectory.Name(request.Name);
        if (name is null)
        {
            return new Failure(new Validation("name must be 1 to 128 characters with a letter or digit.", "invalid_request"));
        }

        var origins = ClientOrigins.Validate(request.AllowedOrigins);
        List<Uri> redirects = [];
        foreach (var value in request.RedirectUris ?? [])
        {
            var redirect = ClientRedirect(value);
            if (redirect is null)
            {
                return new Failure(new Validation("Each redirect URI must be an absolute https URL, or http on a loopback host, without userinfo or fragments.", "invalid_request"));
            }

            redirects.Add(redirect);
        }

        if (redirects.Count > 10)
        {
            throw Invalid("redirect_uris must hold 1 to 10 URIs.");
        }

        foreach (var redirect in request.RedirectUris ?? [])
        {
            if (!await OidcResults.AllowsRedirectAsync(settings, projectId, redirect, cancellationToken))
            {
                return new Failure(new Validation("Each redirect URI must also exactly match the project's redirect allowlist.", "invalid_request"));
            }
        }

        _ = await proofs.ConsumeHeaderAsync(person, this.HttpContext.Request, "update_oauth_client", null, cancellationToken);
        var previous = this.HttpContext.Items[Tenancy.ItemKey];
        this.HttpContext.Items[Tenancy.ItemKey] = projectId;
        try
        {
            if (await applications.FindByClientIdAsync(this.RouteId("client_id"), cancellationToken) is not OidcApplication application)
            {
                return new Failure(new NotFound("No such resource.", "not_found"));
            }

            var policy = MachineClients.Policy(application);
            if (policy is null && (request.AllowedScopes is not null || request.AllowedAudiences is not null || request.TokenLifetimeMinutes is not null))
            {
                return new Failure(new Validation("Machine policies require a machine app.", "invalid_request"));
            }

            if (policy is not null ? redirects.Count != 0 : redirects.Count == 0)
            {
                return new Failure(new Validation("Interactive apps need redirect URIs; machine apps need none.", "invalid_request"));
            }

            if (policy is not null && (origins is { Count: > 0 } || request.AllowAnonymous == true))
            {
                return new Failure(new Validation("Machine apps take no allowed_origins or allow_anonymous.", "invalid_request"));
            }

            var descriptor = new OpenIddictApplicationDescriptor();
            await applications.PopulateAsync(descriptor, application, cancellationToken);
            if (policy is not null)
            {
                MachineClients.Configure(descriptor, MachineClients.Validate(request.AllowedScopes ?? policy.AllowedScopes,
                    request.AllowedAudiences ?? policy.AllowedAudiences, request.TokenLifetimeMinutes ?? policy.TokenLifetimeMinutes));
            }

            if (request.AllowAnonymous is { } anonymous && policy is null)
            {
                _ = anonymous ? descriptor.Permissions.Add(GuestSignIn.Permission) : descriptor.Permissions.Remove(GuestSignIn.Permission);
            }

            application.AllowedOrigins = origins ?? application.AllowedOrigins;
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
