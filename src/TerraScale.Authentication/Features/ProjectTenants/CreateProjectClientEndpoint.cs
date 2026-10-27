using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.Oidc;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary><c>POST /v1/{project}/clients</c>.</summary>
public sealed class CreateProjectClientEndpoint(TenantDirectory tenants, IOpenIddictApplicationManager applications, PlatformAuthentication authentication,
    ProjectAuthSettings settings, StepUpProofs proofs) : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(Root + "/clients");
        this.Document("create_project_client", "Register an OAuth client", "An authorization code client with S256 PKCE, refresh tokens and logout. type is public or confidential; redirect_uris holds 1 to 10 absolute https URLs (http only on loopback hosts) without userinfo or fragments. Each URI must additionally exactly match an entry in the project's redirect allowlist when nonempty; an empty list adds no restriction. A confidential client's client_secret is answered once and never again. Needs the admin project role.", "Project tenants", typeof(CreateClientRequest), StatusCodes.Status201Created, typeof(ClientBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(ProjectTenantJson.Default.CreateClientRequest, cancellationToken);
        var name = PlatformDirectory.Name(request.Name) ?? throw Invalid("name must be 1 to 128 characters with a letter or digit.");
        var type = request.Type is ClientTypes.Public or ClientTypes.Confidential ? request.Type : throw Invalid("type must be public or confidential.");
        List<Uri> redirects = [.. (request.RedirectUris ?? []).Select(value => Redirect(value) ?? throw Invalid("Each redirect URI must be an absolute https URL, or http on a loopback host, without userinfo or fragments."))];
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

        caller = await proofs.ConsumeHeaderAsync(caller, this.HttpContext.Request, "create_oauth_client", cancellationToken);
        var (client, secret) = await this.Tenants.CreateClientAsync(this.HttpContext, caller, projectId, name, type, redirects, applications, cancellationToken);
        return Results.Json(Map(client, secret), ProjectTenantJson.Default.ClientBody, statusCode: StatusCodes.Status201Created)
            .WithLocation($"/v1/{projectId}/clients/{client.ClientId}");
    }

    private static Uri? Redirect(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo) && !value.Contains('#', StringComparison.Ordinal)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? uri : null;
}
