namespace TerraScale.Auth.Admin;

using System.Text;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Connections;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Endpoints;
using static TerraScale.Auth.Admin.AdminPortal;

internal sealed class CreateAdminConnectionEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/admin/connections", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, HttpRequest request, AuthStore store, Users.SessionService sessions, AuthOptions options, CancellationToken ct)
    {
        var gate = await RequireOrgAdminAsync(slug, request, store, sessions, ct);
        if (gate is null)
            return Results.Redirect($"/ui/{slug}/sign-in");
        var (instance, user, adminOrgs) = gate.Value;
        if (adminOrgs.Count == 0)
            return Forbidden();

        var form = await request.ReadFormAsync(ct);
        var provider = form["provider"].ToString();
        if (provider.Length != 0)
            return await CreateProviderAsync(gate.Value, form, provider, options, store, ct);

        var name = form["name"].ToString().Trim();
        var idpEntityId = form["idp_entity_id"].ToString().Trim();
        var ssoRedirectUrl = form["sso_redirect_url"].ToString().Trim();
        var pem = form["x509_certificate_pem"].ToString();

        if (name.Length == 0 || idpEntityId.Length == 0)
            return await PageAsync(gate.Value, store, options, ct, "Name and IdP entity id are required.", 400);
        if (!Uri.TryCreate(ssoRedirectUrl, UriKind.Absolute, out var sso) || sso.Scheme is not ("http" or "https"))
            return await PageAsync(gate.Value, store, options, ct, "SSO redirect URL must be an absolute http(s) URL.", 400);
        using var certificate = ConnectionFlow.TryLoadCertificate(pem);
        if (certificate is null)
            return await PageAsync(gate.Value, store, options, ct, "The X.509 certificate is not a parsable PEM certificate.", 400);

        var org = SelectOrg(adminOrgs, form["org_id"].ToString());
        if (org is null)
            return Forbidden();
        var connection = new Connection
        {
            Id = "con_" + SecureTokens.RandomToken(20),
            InstanceId = instance.Id,
            Type = "saml",
            Name = name,
            Enabled = true,
            OrgId = org.Id,
            IdpEntityId = idpEntityId,
            SsoRedirectUrl = ssoRedirectUrl,
            X509CertificatePem = pem,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        await store.CreateConnectionAsync(connection, ct);
        await store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instance.Id,
            Type = "connection.created",
            ActorId = user.Id,
            Data = new Dictionary<string, object> { ["org_id"] = org.Id, ["connection_id"] = connection.Id },
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        return Results.Redirect($"/ui/{slug}/admin");
    }

    private static async Task<IResult> CreateProviderAsync(
        (Instance Instance, User User, List<Organization> AdminOrgs) gate, IFormCollection form,
        string provider, AuthOptions options, AuthStore store, CancellationToken ct)
    {
        var (instance, user, adminOrgs) = gate;
        if (provider is not ("github" or "microsoft" or "google" or "oidc"))
            return await PageAsync(gate, store, options, ct, "Unknown provider.", 400);
        var name = form["name"].ToString().Trim();
        var clientId = form["client_id"].ToString().Trim();
        var clientSecret = form["client_secret"].ToString();
        if (name.Length is 0 or > 120 || clientId.Length is 0 or > 512 || clientSecret.Length is 0 or > 2048)
            return await PageAsync(gate, store, options, ct, "Name, client ID, and client secret are required and must fit their limits.", 400);

        string? issuerUrl = null;
        string? authorize = null;
        string? token = null;
        string? userinfo = null;
        if (provider == "github")
        {
            authorize = "https://github.com/login/oauth/authorize";
            token = "https://github.com/login/oauth/access_token";
            userinfo = "https://api.github.com/user/emails";
        }
        else
        {
            if (provider == "microsoft")
            {
                var tenant = form["tenant_id"].ToString().Trim();
                if (!Guid.TryParse(tenant, out var tenantId))
                    return await PageAsync(gate, store, options, ct, "Enter a valid Microsoft directory (tenant) ID.", 400);
                issuerUrl = $"https://login.microsoftonline.com/{tenantId:D}/v2.0";
            }
            else if (provider == "google")
                issuerUrl = "https://accounts.google.com";
            else
                issuerUrl = form["issuer_url"].ToString().Trim();

            if (!Uri.TryCreate(issuerUrl, UriKind.Absolute, out var issuer) || issuer.Scheme != "https"
                || issuer.UserInfo.Length != 0 || issuer.Fragment.Length != 0 || issuer.Query.Length != 0)
                return await PageAsync(gate, store, options, ct, "Issuer URL must be an absolute HTTPS URL without a query or fragment.", 400);
            var discovery = await OidcLogin.FetchDiscoveryAsync(issuer, ct);
            if (discovery?.Issuer is null || discovery.AuthorizationEndpoint is null || discovery.TokenEndpoint is null
                || !Uri.TryCreate(discovery.AuthorizationEndpoint, UriKind.Absolute, out var authorizationUri) || authorizationUri.Scheme != "https"
                || !Uri.TryCreate(discovery.TokenEndpoint, UriKind.Absolute, out var tokenUri) || tokenUri.Scheme != "https"
                || !string.Equals(discovery.Issuer.TrimEnd('/'), issuerUrl.TrimEnd('/'), StringComparison.Ordinal))
                return await PageAsync(gate, store, options, ct, "The provider's OpenID Connect discovery document is unavailable or does not match the issuer.", 400);
            authorize = discovery.AuthorizationEndpoint;
            token = discovery.TokenEndpoint;
            userinfo = discovery.UserinfoEndpoint;
        }

        var org = SelectOrg(adminOrgs, form["org_id"].ToString());
        if (org is null)
            return Forbidden();
        var connection = new Connection
        {
            Id = "con_" + SecureTokens.RandomToken(20),
            InstanceId = instance.Id,
            Type = provider == "github" ? "github" : "oidc",
            Name = name,
            Enabled = true,
            OrgId = org.Id,
            IssuerUrl = issuerUrl,
            ClientId = clientId,
            ClientSecretBox = SecretBox.Encrypt(options.MasterKey, Encoding.UTF8.GetBytes(clientSecret)),
            AuthorizationEndpoint = authorize,
            TokenEndpoint = token,
            UserinfoEndpoint = userinfo,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        await store.CreateConnectionAsync(connection, ct);
        await store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instance.Id,
            Type = "connection.created",
            ActorId = user.Id,
            Data = new Dictionary<string, object> { ["org_id"] = org.Id, ["connection_id"] = connection.Id, ["provider"] = provider },
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        return Results.Redirect($"/ui/{instance.Slug}/admin#connections");
    }
}
