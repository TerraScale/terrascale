namespace TerraScale.Auth.Admin;

using TerraScale.Auth.Api;
using TerraScale.Auth.Connections;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;

/// <summary>
/// Hosted admin portal (WorkOS Admin Portal parity): organization admins
/// self-serve their SSO connection and SCIM directory from the browser.
/// Every route requires a live session whose user is an admin of at least
/// one organization in the instance; writes are further scoped to those orgs.
/// </summary>
public static class AdminPortalEndpoints
{
    /// <summary>Maps admin portal endpoints.</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet("/ui/{slug}/admin", async (
            string slug, HttpRequest request, AuthStore store, Users.SessionService sessions, CancellationToken ct) =>
        {
            var gate = await RequireOrgAdminAsync(slug, request, store, sessions, ct);
            if (gate is null)
                return Results.Redirect($"/ui/{slug}/sign-in?next={Uri.EscapeDataString($"/ui/{slug}/admin")}");
            if (gate.Value.AdminOrgs.Count == 0)
                return Forbidden();
            return await PageAsync(gate.Value, store, ct);
        });

        app.MapPost("/ui/{slug}/admin/connections", async (
            string slug, HttpRequest request, AuthStore store, Users.SessionService sessions, CancellationToken ct) =>
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
            using var certificate = ConnectionEndpoints.TryLoadCertificate(pem);
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
        });

        app.MapPost("/ui/{slug}/admin/connections/{connectionId}/{action}", async (
            string slug, string connectionId, string action, HttpRequest request, AuthStore store, Users.SessionService sessions, CancellationToken ct) =>
        {
            var gate = await RequireOrgAdminAsync(slug, request, store, sessions, ct);
            if (gate is null)
                return Results.Redirect($"/ui/{slug}/sign-in");
            var (instance, user, adminOrgs) = gate.Value;
            if (adminOrgs.Count == 0)
                return Forbidden();

            if (action is not ("enable" or "disable"))
                return ApiResults.NotFound();
            var connection = await store.GetConnectionAsync(connectionId, ct);
            if (connection is null || connection.InstanceId != instance.Id)
                return ApiResults.NotFound();
            // Only connections owned by an org the user administers; instance-level
            // connections are provisioned through the management API, not the portal.
            if (connection.OrgId is null || adminOrgs.All(o => o.Id != connection.OrgId))
                return Forbidden();

            await store.UpsertConnectionAsync(connection with { Enabled = action == "enable" }, ct);
            await store.CreateAuditEventAsync(new AuditEvent
            {
                Id = "evt_" + SecureTokens.RandomToken(20),
                InstanceId = instance.Id,
                Type = action == "enable" ? "connection.enabled" : "connection.disabled",
                ActorId = user.Id,
                Data = new Dictionary<string, object> { ["org_id"] = connection.OrgId, ["connection_id"] = connection.Id },
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, ct);
            return Results.Redirect($"/ui/{slug}/admin");
        });
    }

    /// <summary>Resolves the session and the organizations the user administers;
    /// null (redirect) for unknown instance, anonymous, expired, or banned sessions.</summary>
    private static async Task<(Instance Instance, User User, List<Organization> AdminOrgs)?> RequireOrgAdminAsync(
        string slug, HttpRequest request, AuthStore store, Users.SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return null;
        var token = request.Cookies[SessionCookies.Name];
        if (string.IsNullOrEmpty(token))
            return null;
        var resolved = await sessions.ResolveAsync(instance.Id, token, ct);
        if (resolved is not { } pair)
            return null;

        var adminOrgs = new List<Organization>();
        foreach (var org in await store.ListOrganizationsAsync(instance.Id, ct))
            if ((await store.ListMembershipsByOrgAsync(org.Id, ct)).Any(m => m.UserId == pair.User.Id && m.Role == "admin"))
                adminOrgs.Add(org);
        return (instance, pair.User, adminOrgs);
    }

    private static async Task<IResult> PageAsync(
        (Instance Instance, User User, List<Organization> AdminOrgs) gate, AuthStore store, CancellationToken ct,
        string? error = null, int status = 200)
    {
        var (instance, user, adminOrgs) = gate;
        var slug = instance.Slug;
        var orgIds = adminOrgs.Select(o => o.Id).ToHashSet();

        var connections = (await store.ListConnectionsAsync(instance.Id, ct)).Where(c => c.OrgId is not null && orgIds.Contains(c.OrgId)).ToList();
        var directories = (await store.ListScimDirectoriesAsync(instance.Id, ct)).Where(d => d.OrgId is not null && orgIds.Contains(d.OrgId)).ToList();

        var rows = new System.Text.StringBuilder();
        foreach (var connection in connections)
        {
            rows.AppendLine($"""<div class="row"><div><strong>{Ui.Esc(connection.Name)}</strong> <span class="muted">{Ui.Esc(connection.Type.ToUpperInvariant())}{(connection.Enabled ? "" : " · disabled")}</span></div><form method="post" action="/ui/{slug}/admin/connections/{connection.Id}/{(connection.Enabled ? "disable" : "enable")}"><button class="secondary">{(connection.Enabled ? "Disable" : "Enable")}</button></form></div>""");
        }
        var directoryRows = new System.Text.StringBuilder();
        foreach (var directory in directories)
        {
            directoryRows.AppendLine($"""<div class="row"><div><strong>SCIM directory</strong> <span class="muted">/scim/{Ui.Esc(directory.Id)}/Users</span></div></div>""");
        }

        return Ui.Page("Admin", $"""
                <h1>Admin portal</h1>
                {(error is null ? "" : $"""<div class="error">{Ui.Esc(error)}</div>""")}
                <p class="muted">Signed in as {Ui.Esc(user.Email ?? user.Id)}. Organizations you administer: {adminOrgs.Count}.</p>
                <h1 style="font-size:16px">SSO connections</h1>
                <div class="list">{(rows.Length == 0 ? "<div class=\"muted\">No connections yet.</div>" : rows.ToString())}</div>
                <form method="post" action="/ui/{slug}/admin/connections">
                  <label>Name</label><input name="name" required>
                  <label>IdP entity id</label><input name="idp_entity_id" required>
                  <label>SSO redirect URL</label><input name="sso_redirect_url" placeholder="https://idp.example/sso" required>
                  <label>X.509 certificate (PEM)</label><input name="x509_certificate_pem" placeholder="-----BEGIN CERTIFICATE-----" required>
                  <button>Add SAML connection</button>
                </form>
                <h1 style="font-size:16px;margin-top:24px">Directories</h1>
                <div class="list">{(directoryRows.Length == 0 ? "<div class=\"muted\">No directories yet.</div>" : directoryRows.ToString())}</div>
                """, status: status);
    }

    private static IResult Forbidden() =>
        Ui.Page("Not authorized", """
            <h1>Not authorized</h1>
            <p class="muted">Only organization admins can use the admin portal.</p>
            """, status: 403);
}
