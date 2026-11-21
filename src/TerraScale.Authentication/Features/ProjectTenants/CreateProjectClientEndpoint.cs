using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.Oidc;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class CreateProjectClientEndpoint(TenantDirectory tenants, IOpenIddictApplicationManager applications, PlatformAuthentication authentication,
    ProjectAuthSettings settings, StepUpProofs proofs) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(Root + "/clients");
        this.Describe("create_project_client", "Register an OAuth client", "An authorization code client with S256 PKCE, refresh tokens and logout. type is public, confidential or machine; machine apps use client_credentials with 1 to 64 allowed_scopes, optional allowed_audiences and token_lifetime_minutes (5 to 60, default 15), and no redirect URIs; redirect_uris holds 1 to 10 absolute https URLs (http only on loopback hosts) without userinfo or fragments. Each URI must additionally exactly match an entry in the project's redirect allowlist when nonempty; an empty list adds no restriction. A confidential client's client_secret is answered once and never again. allowed_origins holds at most 10 exact browser origins (https://host[:port], or http on a loopback host; lowercase, no default port, path, trailing slash, wildcard, userinfo, query or fragment) whose cross-origin token, userinfo, revocation and guest requests get CORS; allow_anonymous (default false) admits guests at /{project}/oauth/anonymous. Machine apps take neither. Needs the admin project role.", "Project tenants", typeof(CreateClientRequest), StatusCodes.Status201Created, typeof(ClientBody));
    }

    protected override async Task<Answer> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var person = caller.RequirePerson();
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(ProjectTenantJson.Default.CreateClientRequest, cancellationToken);
        var name = PlatformDirectory.Name(request.Name);
        if (name is null)
        {
            return ErrorResults.ToResult(new Validation("name must be 1 to 128 characters with a letter or digit.", "invalid_request"));
        }

        if (request.Type is not (ClientTypes.Public or ClientTypes.Confidential or "machine"))
        {
            return ErrorResults.ToResult(new Validation("type must be public, confidential or machine.", "invalid_request"));
        }

        var type = request.Type;
        List<Uri> redirects = [];
        foreach (var value in request.RedirectUris ?? [])
        {
            var redirect = ClientRedirect(value);
            if (redirect is null)
            {
                return ErrorResults.ToResult(new Validation("Each redirect URI must be an absolute https URL, or http on a loopback host, without userinfo or fragments.", "invalid_request"));
            }

            redirects.Add(redirect);
        }

        var policy = type == "machine" ? MachineClients.Validate(request.AllowedScopes, request.AllowedAudiences, request.TokenLifetimeMinutes) : null;
        if (policy is null && (request.AllowedScopes is not null || request.AllowedAudiences is not null || request.TokenLifetimeMinutes is not null))
        {
            return ErrorResults.ToResult(new Validation("Machine policies require type machine.", "invalid_request"));
        }

        var origins = ClientOrigins.Validate(request.AllowedOrigins) ?? [];
        if (policy is not null && (origins.Count != 0 || request.AllowAnonymous == true))
        {
            return ErrorResults.ToResult(new Validation("Machine apps take no allowed_origins or allow_anonymous.", "invalid_request"));
        }

        if (policy is not null ? redirects.Count != 0 : redirects.Count is < 1 or > 10)
        {
            return ErrorResults.ToResult(new Validation("Interactive apps need 1 to 10 redirect URIs; machine apps need none.", "invalid_request"));
        }

        foreach (var redirect in request.RedirectUris ?? [])
        {
            if (!await OidcResults.AllowsRedirectAsync(settings, projectId, redirect, cancellationToken))
            {
                return ErrorResults.ToResult(new Validation("Each redirect URI must also exactly match the project's redirect allowlist.", "invalid_request"));
            }
        }

        person = await proofs.ConsumeHeaderAsync(person, this.HttpContext.Request, "create_oauth_client", null, cancellationToken);
        var (client, secret) = await this.Tenants.CreateClientAsync(this.HttpContext, person, projectId,
            new(name, type, redirects, policy, origins, request.AllowAnonymous == true), applications, cancellationToken);
        return Results.Json(Map(client, secret), ProjectTenantJson.Default.ClientBody, statusCode: StatusCodes.Status201Created)
            .WithLocation($"/v1/{projectId}/clients/{client.ClientId}");
    }
}
