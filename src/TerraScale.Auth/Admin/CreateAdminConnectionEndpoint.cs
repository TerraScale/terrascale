namespace TerraScale.Auth.Admin;

using TerraScale.Auth.Api;
using TerraScale.Auth.Connections;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Endpoints;
using static TerraScale.Auth.Admin.AdminPortal;

internal sealed class CreateAdminConnectionEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/admin/connections", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, HttpRequest request, AuthStore store, Users.SessionService sessions, CancellationToken ct)
    {
        var gate = await RequireOrgAdminAsync(slug, request, store, sessions, ct);
        if (gate is null)
            return Results.Redirect($"/ui/{slug}/sign-in");
        var (instance, user, adminOrgs) = gate.Value;
        if (adminOrgs.Count == 0)
            return Forbidden();

        var form = await request.ReadFormAsync(ct);
        var name = form["name"].ToString().Trim();
        var idpEntityId = form["idp_entity_id"].ToString().Trim();
        var ssoRedirectUrl = form["sso_redirect_url"].ToString().Trim();
        var pem = form["x509_certificate_pem"].ToString();

        if (name.Length == 0 || idpEntityId.Length == 0)
            return await PageAsync(gate.Value, store, ct, "Name and IdP entity id are required.", 400);
        if (!Uri.TryCreate(ssoRedirectUrl, UriKind.Absolute, out var sso) || sso.Scheme is not ("http" or "https"))
            return await PageAsync(gate.Value, store, ct, "SSO redirect URL must be an absolute http(s) URL.", 400);
        using var certificate = ConnectionFlow.TryLoadCertificate(pem);
        if (certificate is null)
            return await PageAsync(gate.Value, store, ct, "The X.509 certificate is not a parsable PEM certificate.", 400);

        var org = adminOrgs[0];
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
}
