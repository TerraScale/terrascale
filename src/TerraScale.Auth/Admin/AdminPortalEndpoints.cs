namespace TerraScale.Auth.Admin;

using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;

/// <summary>
/// Hosted admin portal (WorkOS Admin Portal parity): organization admins
/// self-serve their SSO connection and SCIM directory from the browser.
/// </summary>
public static class AdminPortalEndpoints
{
    /// <summary>Maps admin portal endpoints.</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet("/ui/{slug}/admin", async (
            string slug, HttpRequest request, AuthStore store, Users.SessionService sessions, CancellationToken ct) =>
        {
            var (instance, user, session) = await RequireAsync(slug, request, store, sessions, ct);
            if (instance is null)
                return Results.Redirect($"/ui/{slug}/sign-in?next={Uri.EscapeDataString($"/ui/{slug}/admin")}");

            var orgs = (await store.ListOrganizationsAsync(instance.Id, ct))
                .Where(org => IsOrgAdmin(store, org.Id, user.Id, ct).GetAwaiter().GetResult())
                .ToList();
            var connections = (await store.ListConnectionsAsync(instance.Id, ct)).Where(c => c.OrgId is null || orgs.Any(o => o.Id == c.OrgId)).ToList();
            var directories = (await store.ListScimDirectoriesAsync(instance.Id, ct)).Where(d => d.OrgId is null || orgs.Any(o => o.Id == d.OrgId)).ToList();

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
                <p class="muted">Signed in as {Ui.Esc(user.Email ?? user.Id)}. Organizations you administer: {orgs.Count}.</p>
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
                """);
        });

        app.MapPost("/ui/{slug}/admin/connections", async (
            string slug, HttpRequest request, AuthStore store, Users.SessionService sessions, CancellationToken ct) =>
        {
            var (instance, user, _) = await RequireAsync(slug, request, store, sessions, ct);
            if (instance is null)
                return Results.Redirect($"/ui/{slug}/sign-in");
            var org = (await store.ListOrganizationsAsync(instance.Id, ct))
                .FirstOrDefault(candidate => IsOrgAdmin(store, candidate.Id, user.Id, ct).GetAwaiter().GetResult());
            var form = await request.ReadFormAsync(ct);
            await store.CreateConnectionAsync(new Connection
            {
                Id = "con_" + SecureTokens.RandomToken(20),
                InstanceId = instance.Id,
                Type = "saml",
                Name = form["name"].ToString(),
                OrgId = org?.Id,
                IdpEntityId = form["idp_entity_id"].ToString(),
                SsoRedirectUrl = form["sso_redirect_url"].ToString(),
                X509CertificatePem = form["x509_certificate_pem"].ToString(),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, ct);
            await store.CreateAuditEventAsync(new AuditEvent
            {
                Id = "evt_" + SecureTokens.RandomToken(20),
                InstanceId = instance.Id,
                Type = "connection.created",
                ActorId = user.Id,
                Data = new Dictionary<string, object> { ["org_id"] = org?.Id ?? (object)"" },
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, ct);
            return Results.Redirect($"/ui/{slug}/admin");
        });

        app.MapPost("/ui/{slug}/admin/connections/{connectionId}/{action}", async (
            string slug, string connectionId, string action, HttpRequest request, AuthStore store, Users.SessionService sessions, CancellationToken ct) =>
        {
            var (instance, user, _) = await RequireAsync(slug, request, store, sessions, ct);
            if (instance is null)
                return Results.Redirect($"/ui/{slug}/sign-in");
            if (action is not ("enable" or "disable"))
                return ApiResults.NotFound();
            var connection = await store.GetConnectionAsync(connectionId, ct);
            if (connection is null || connection.InstanceId != instance.Id)
                return ApiResults.NotFound();
            await store.UpsertConnectionAsync(connection with { Enabled = action == "enable" }, ct);
            return Results.Redirect($"/ui/{slug}/admin");
        });
    }

    private static async Task<(Store.Instance? Instance, User User, Session Session)> RequireAsync(
        string slug, HttpRequest request, AuthStore store, Users.SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return (null, null!, null!);
        var token = request.Cookies[SessionCookies.Name];
        if (string.IsNullOrEmpty(token))
            return (null, null!, null!);
        var resolved = await sessions.ResolveAsync(instance.Id, token, ct);
        return resolved is { } pair ? (instance, pair.User, pair.Session) : (null, null!, null!);
    }

    private static async Task<bool> IsOrgAdmin(AuthStore store, string orgId, string userId, CancellationToken ct) =>
        (await store.ListMembershipsByOrgAsync(orgId, ct)).Any(m => m.UserId == userId && m.Role == "admin");
}
