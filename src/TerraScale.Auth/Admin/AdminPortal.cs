namespace TerraScale.Auth.Admin;

using TerraScale.Auth.Api;
using TerraScale.Auth.Connections;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;

internal static class AdminPortal
{
    /// <summary>Resolves the session and the organizations the user administers;
    /// null (redirect) for unknown instance, anonymous, expired, or banned sessions.</summary>
    internal static async Task<(Instance Instance, User User, List<Organization> AdminOrgs)?> RequireOrgAdminAsync(
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

    internal static async Task<IResult> PageAsync(
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

    internal static IResult Forbidden() =>
        Ui.Page("Not authorized", """
            <h1>Not authorized</h1>
            <p class="muted">Only organization admins can use the admin portal.</p>
            """, status: 403);
}
