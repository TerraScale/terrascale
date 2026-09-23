namespace TerraScale.Auth.Admin;

using TerraScale.Auth.Configuration;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;

internal static class AdminPortal
{
    /// <summary>Resolves the session and the organizations the user administers.</summary>
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
        (Instance Instance, User User, List<Organization> AdminOrgs) gate, AuthStore store, AuthOptions options, CancellationToken ct,
        string? error = null, int status = 200)
    {
        var (instance, user, adminOrgs) = gate;
        var orgIds = adminOrgs.Select(org => org.Id).ToHashSet();
        var connections = (await store.ListConnectionsAsync(instance.Id, ct))
            .Where(connection => connection.OrgId is not null && orgIds.Contains(connection.OrgId))
            .OrderBy(connection => connection.CreatedAt).ToList();
        var directories = (await store.ListScimDirectoriesAsync(instance.Id, ct))
            .Where(directory => directory.OrgId is not null && orgIds.Contains(directory.OrgId)).ToList();
        var model = new AdminPortalPageModel(instance, user, adminOrgs, connections, directories,
            options.PublicOrigin.ToString().TrimEnd('/'), error);
        var page = AdminPortalPage.Create(model);
        page.StatusCode = status;
        return page;
    }

    internal static string? SetupHtml(string provider, string slug, IReadOnlyList<Organization> adminOrgs) => provider switch
    {
        "github" => Setup(slug, provider, adminOrgs, "GitHub", "Create an OAuth app in GitHub Developer Settings. Request access to the user's verified email address.", ""),
        "microsoft" => Setup(slug, provider, adminOrgs, "Microsoft Entra ID", "Create an app registration and add its client secret. Use a single tenant application.", "<div class=\"field\"><label for=\"provider-tenant\">Directory (tenant) ID</label><input class=\"input\" id=\"provider-tenant\" name=\"tenant_id\" required placeholder=\"00000000-0000-0000-0000-000000000000\"><small>Use the GUID from your Entra overview.</small></div>"),
        "google" => Setup(slug, provider, adminOrgs, "Google", "Create a Web application OAuth client in Google Cloud Console.", ""),
        "oidc" => Setup(slug, provider, adminOrgs, "Other OpenID Connect", "Enter an issuer with a public discovery document.", "<div class=\"field\"><label for=\"provider-issuer\">Issuer URL</label><input class=\"input\" id=\"provider-issuer\" name=\"issuer_url\" type=\"url\" required placeholder=\"https://idp.example.com\"></div>"),
        _ => null,
    };

    private static string Setup(string slug, string provider, IReadOnlyList<Organization> adminOrgs, string title, string description, string extra) =>
        $"""<div id="provider-setup" class="setup"><div class="setup-head"><div><h3>{title}</h3><p>{description}</p></div><span class="pill">New connection</span></div><form method="post" action="/ui/{Ui.Esc(slug)}/admin/connections"><input type="hidden" name="provider" value="{provider}">{OrganizationSelect(adminOrgs)}<div class="field"><label for="provider-name">Display name</label><input class="input" id="provider-name" name="name" value="{title}" required></div>{extra}<div class="field"><label for="provider-client-id">Client ID</label><input class="input" id="provider-client-id" name="client_id" autocomplete="off" required></div><div class="field"><label for="provider-client-secret">Client secret</label><input class="input" id="provider-client-secret" name="client_secret" type="password" autocomplete="new-password" required></div><div class="form-actions"><button class="btn btn-primary" type="submit">Add {title}</button><small>The callback URL appears after you save.</small></div></form></div>""";

    private static string OrganizationSelect(IReadOnlyList<Organization> adminOrgs) => adminOrgs.Count < 2
        ? ""
        : $"""<div class="field"><label for="connection-org">Organization</label><select class="input" id="connection-org" name="org_id">{string.Join("", adminOrgs.Select(org => $"<option value=\"{Ui.Esc(org.Id)}\">{Ui.Esc(org.Name)}</option>"))}</select></div>""";

    internal static Organization? SelectOrg(IReadOnlyList<Organization> adminOrgs, string? orgId) =>
        string.IsNullOrWhiteSpace(orgId) ? adminOrgs.FirstOrDefault() : adminOrgs.FirstOrDefault(org => org.Id == orgId);

    internal static IResult Forbidden() => Ui.Page("Not authorized", """
        <h1>Not authorized</h1><p class="muted">Only organization admins can use the admin portal.</p>
        """, status: 403);
}
