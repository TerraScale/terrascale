using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bogus;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.AuthenticationManagement;

// The console manages the current project's ts-auth tenant: the demo host's in-memory tenant stands in for
// ts-auth's project management API, except in the pre-live journey, which uses the real gateway and SDK.
[ClassDataSource<DashboardRootFixture>]
public sealed class AuthenticationConsoleJourneyTests(DashboardRootFixture fixture)
{
    private static readonly string[] Sections = ["overview", "users", "sessions", "sign-in", "social", "configuration", "clients", "branding", "danger"];
    private static readonly string[] Removed = ["organizations", "memberships", "invitations", "domains", "connections", "mfa", "directories",
        "templates", "keys", "webhooks", "emails", "audit", "quickstart"];

    [Test]
    public async Task OwnerManagesUsersSessionsApplicationsAndSignInOfTheCurrentProject()
    {
        var faker = new Faker();
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        using (var anonymous = await browser.GetAsync("/auth/users"))
            Check(anonymous.StatusCode == HttpStatusCode.Redirect, "Signed-out visitors sign in first");
        using (var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
            Check(login.StatusCode == HttpStatusCode.SeeOther, "The owner signs in");

        var overview = await Page(browser, "/auth");
        var nav = Regex.Match(overview, "<nav aria-label=\"Authentication sections\">(.*?)</nav>", RegexOptions.Singleline).Groups[1].Value;
        Check(Regex.Matches(nav, "href=\"/auth/([^\"]+)\"").Select(match => match.Groups[1].Value).SequenceEqual(Sections), "The sidebar lists only sections ts-auth backs");
        foreach (var section in Removed)
        {
            using var missing = await browser.GetAsync("/auth/" + section);
            Check(missing.StatusCode == HttpStatusCode.NotFound, "Unbacked sections answer 404: " + section);
        }
        Check(!overview.Contains("id=\"auth-error\""), "The overview renders without an error");
        var project = Regex.Match(overview, "id=\"auth-issuer\">https://auth\\.terrascale\\.tech/([0-9A-Z]{26})</code>").Groups[1].Value;
        Check(project.Length == 26 && overview.Contains($"https://auth.terrascale.tech/ui/{project}/sign-in") &&
            overview.Contains("id=\"auth-count-users\">5<") && overview.Contains("id=\"auth-count-clients\">1<"), "The overview shows issuer, hosted sign-in and counts");

        var first = await Page(browser, "/auth/users");
        var next = Regex.Match(first, "href=\"/auth/users\\?after=([0-9A-Z]{26})\">Next page").Groups[1].Value;
        Check(Ids(first, "users", "disable").Length == 3 && next.Length == 26, "Users list in pages");
        var second = await Page(browser, "/auth/users?after=" + next);
        Check(Ids(second, "users", "disable").Length == 2 && !second.Contains("Next page") && second.Contains("First page"), "The last page ends paging");
        var search = await Page(browser, "/auth/users?q=GLOBEX");
        Check(search.Contains("alan@globex.io") && Ids(search, "users", "disable").Length == 1, "Search matches an email substring");

        var csrf = Csrf(first);
        var user = Ids(first, "users", "disable")[0];
        Check((await Post(browser, $"users/{user}/disable", csrf)).StatusCode == HttpStatusCode.SeeOther, "Disabling redirects back");
        var disabled = await Page(browser, "/auth/users");
        Check(Ids(disabled, "users", "enable").SequenceEqual([user]) && disabled.Contains(">Disabled</span>"), "The user shows as disabled");
        Check((await Post(browser, $"users/{user}/enable", csrf)).StatusCode == HttpStatusCode.SeeOther, "Enabling redirects back");
        Check((await Post(browser, $"users/{user}/delete", csrf)).StatusCode == HttpStatusCode.SeeOther, "Deleting redirects back");
        Check((await Page(browser, "/auth")).Contains("id=\"auth-count-users\">4<"), "The deleted user leaves the count");
        using (var gone = await Post(browser, $"users/{user}/delete", csrf))
            Check(gone.StatusCode == HttpStatusCode.NotFound && (await gone.Content.ReadAsStringAsync()).Contains("the requested record, is unavailable"), "A missing user reports a friendly 404");
        using (var forged = await HttpJourney.PostFormAsync(browser, $"/api/v1/auth-management/users/{Ids(second, "users", "disable")[0]}/disable", new()))
            Check(forged.StatusCode == HttpStatusCode.Forbidden, "A change without the form token is refused");

        var sessions = await Page(browser, "/auth/sessions");
        var live = Ids(sessions, "sessions", "revoke");
        Check(live.Length == 1, "The deleted user's session ended with it; one live session remains");
        Check((await Post(browser, $"sessions/{live[0]}/revoke", csrf)).StatusCode == HttpStatusCode.SeeOther, "Revoking redirects back");
        Check((await Page(browser, "/auth/sessions")).Contains("No live sessions"), "The revoked session is gone");

        var name = "App " + faker.Random.AlphaNumeric(12);
        var uris = $"https://{faker.Internet.DomainName()}/callback\nhttp://127.0.0.1:{faker.Random.Int(1024, 65535)}/callback";
        using (var invalid = await Post(browser, "clients", csrf, ("name", name), ("type", "machine"), ("redirect_uris", uris)))
            Check(invalid.StatusCode == HttpStatusCode.UnprocessableEntity && (await invalid.Content.ReadAsStringAsync()).Contains("Choose a public or confidential application."), "An unknown client type is refused");
        using var created = await Post(browser, "clients", csrf, ("name", name), ("type", "confidential"), ("redirect_uris", uris));
        var reveal = await created.Content.ReadAsStringAsync();
        var secret = Regex.Match(reveal, "id=\"auth-client-secret\">([^<]+)<").Groups[1].Value;
        Check(created.StatusCode == HttpStatusCode.OK && secret.Length > 16 && reveal.Contains(WebUtility.HtmlEncode(name)), "A confidential client's secret shows once after creation");
        var clients = await Page(browser, "/auth/clients");
        var client = Ids(clients, "clients", "delete");
        Check(client.Length == 2 && !clients.Contains(secret) && clients.Contains("http://127.0.0.1:"), "The list shows both applications and never the secret");
        Check((await Post(browser, $"clients/{client[1]}/delete", csrf)).StatusCode == HttpStatusCode.SeeOther, "Deleting an application redirects back");
        Check(Ids(await Page(browser, "/auth/clients"), "clients", "delete").Length == 1, "The application is gone");

        using (var none = await Post(browser, "settings", csrf, ("sign_up", "on")))
            Check(none.StatusCode == HttpStatusCode.UnprocessableEntity && (await none.Content.ReadAsStringAsync()).Contains("Keep email passcode or password sign-in on."), "One first factor stays on");
        Check((await Post(browser, "settings", csrf, ("passcode", "on"))).StatusCode == HttpStatusCode.SeeOther, "Saving sign-in methods redirects back");
        var methods = await Page(browser, "/auth/sign-in");
        Check(Regex.IsMatch(methods, "id=\"auth-method-passcode\" name=\"passcode\" checked") && !Regex.IsMatch(methods, "name=\"password\" checked") &&
            !Regex.IsMatch(methods, "name=\"sign_up\" checked"), "Only passcode sign-in stays on and sign-up is off");

        var color = faker.Internet.Color();
        using (var badColor = await Post(browser, "branding", csrf, ("name", name), ("theme_color", "blue")))
            Check(badColor.StatusCode == HttpStatusCode.UnprocessableEntity && (await badColor.Content.ReadAsStringAsync()).Contains("The theme color must look like #2158e8."), "A theme color must be #rrggbb");
        using (var badLogo = await Post(browser, "branding", csrf, ("logo_url", "http://example.com/logo.png")))
            Check(badLogo.StatusCode == HttpStatusCode.UnprocessableEntity, "A logo must be an https URL");
        Check((await Post(browser, "branding", csrf, ("name", name), ("logo_url", "https://" + faker.Internet.DomainName() + "/logo.svg"), ("theme_color", color.ToUpperInvariant()))).StatusCode == HttpStatusCode.SeeOther, "Saving branding redirects back");
        var branding = await Page(browser, "/auth/branding");
        Check(branding.Contains($"value=\"{color.ToLowerInvariant()}\"") && branding.Contains(WebUtility.HtmlEncode(name)), "Branding is saved with a normalized color");

        var social = await Page(browser, "/auth/social");
        Check(Regex.Matches(social, "<li class=\"ts-idp-setting\" id=\"auth-provider-(google|github|apple|oidc)\"").Count == 4 && !social.Contains(">Configured<") &&
            social.Contains($"https://auth.terrascale.tech/ui/{project}/social/apple/callback"), "Every provider lists as not configured with its callback URL");
        var (clientId, providerSecret) = (faker.Random.Number(100000, 999999) + ".apps.googleusercontent.com", "GOCSPX-" + faker.Random.AlphaNumeric(24));
        using (var noSecret = await Post(browser, "providers/google", csrf, ("client_id", clientId)))
            Check(noSecret.StatusCode == HttpStatusCode.UnprocessableEntity, "The first configuration needs a client secret");
        Check((await Post(browser, "providers/google", csrf, ("client_id", clientId), ("client_secret", providerSecret))).StatusCode == HttpStatusCode.SeeOther, "Configuring Google redirects back");
        Check((await Post(browser, "providers/google", csrf, ("client_id", clientId + "x"), ("client_secret", " "))).StatusCode == HttpStatusCode.SeeOther, "A blank secret keeps the stored one");
        using (var badApple = await Post(browser, "providers/apple", csrf, ("client_id", "app.example.web"), ("team_id", "SHORT"), ("key_id", "ABCDE12345"), ("client_secret", "-----BEGIN PRIVATE KEY-----")))
            Check(badApple.StatusCode == HttpStatusCode.UnprocessableEntity && (await badApple.Content.ReadAsStringAsync()).Contains("The Apple team ID and key ID are 10 letters or digits."), "Apple IDs are 10 characters");
        using (var badIssuer = await Post(browser, "providers/oidc", csrf, ("client_id", "web"), ("client_secret", providerSecret), ("issuer", "http://id.example.com")))
            Check(badIssuer.StatusCode == HttpStatusCode.UnprocessableEntity && (await badIssuer.Content.ReadAsStringAsync()).Contains("The issuer must be an https URL"), "An OpenID Connect issuer is https");
        using (var unknown = await Post(browser, "providers/facebook", csrf, ("client_id", "web")))
            Check(unknown.StatusCode == HttpStatusCode.NotFound, "Unknown providers answer 404");
        var configured = await Page(browser, "/auth/social");
        Check(configured.Contains($"<code>{clientId}x</code>") && configured.Contains(">Configured<") && !configured.Contains(providerSecret), "Google shows its client ID and never the secret");
        Check((await Post(browser, "providers/google/delete", csrf)).StatusCode == HttpStatusCode.SeeOther, "Removing a provider redirects back");
        Check(!(await Page(browser, "/auth/social")).Contains(">Configured<"), "The removed provider is not configured");
    }


    [Test]
    public async Task HostedSettingsValidatePersistAndStayWithinTheSelectedProject()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        var persona = scenario.Personas.Seeded;
        using var login = await HttpJourney.LoginAsync(browser, persona.Email, persona.Password);
        Check(login.Headers.Location?.OriginalString.StartsWith("/organizations/choose?returnTo=", StringComparison.Ordinal) == true, "Multi-organization dashboard sign-in opens the picker");
        using var chooser = scenario.CreateClient();
        const string pickerReturn = "/organizations/choose?returnTo=%2Fauth%2Fbranding";
        using var direct = await HttpJourney.LoginAsync(chooser, persona.Email, persona.Password, returnTo: pickerReturn);
        Check(direct.Headers.Location?.OriginalString == pickerReturn, "A direct picker sign-in does not wrap or loop its return path");
        var initial = await Page(browser, "/auth/branding");
        Check(initial.Contains("https://docs.terrascale.tech/legal/privacy") && initial.Contains("https://docs.terrascale.tech/legal/terms"),
            "Default legal links use the final docs URLs");
        var csrf = Csrf(initial);
        var name = "Brand " + new Faker().Random.AlphaNumeric(10);
        using var saved = await Post(browser, "branding", csrf, ("name", name), ("logo_url", "https://assets.example.com/logo.svg"),
            ("theme_color", "#2158e8"), ("title_size", "32"), ("default_theme", "dark"), ("background_color", "#151515"),
            ("text_color", "#f2f2f2"), ("footer_links", "Privacy | https://example.com/privacy\nTerms | https://example.com/terms"), ("email_message", "Welcome <script>alert(1)</script>\r\nYour team"));
        Check(saved.StatusCode == HttpStatusCode.SeeOther, "The complete branding object is accepted");
        var branding = await Page(browser, "/auth/branding");
        Check(branding.Contains(name) && branding.Contains("value=\"32\"") && branding.Contains("https://example.com/privacy") &&
            branding.Contains("data-brand-mode=\"dark\"") && branding.Contains("referrerpolicy=\"no-referrer\""), "The editor reads persisted fields and provides a private visual preview");
        Check(branding.Contains("Welcome &lt;script&gt;alert(1)&lt;/script&gt;") && !branding.Contains("Welcome <script>"), "The saved email message is escaped in the editor");
        foreach (var fields in new[] { new[] { ("title_size", "41") }, [("default_theme", "auto")], [("footer_links", "Help | javascript:alert(1)")], [("background_color", "red")], [("email_message", new string('a', 501))], [("email_message", "hidden\tcontrol")] })
        {
            using var invalid = await Post(browser, "branding", csrf, fields);
            Check(invalid.StatusCode == HttpStatusCode.UnprocessableEntity, "Invalid branding is refused before it is saved");
        }
        foreach (var url in new[] { "https://example.com/#fragment", "https://user:password@example.com/callback", "http://example.com/callback", "https://*.example.com/callback" })
        {
            using var invalid = await Post(browser, "configuration", csrf, ("redirect_urls", url), ("session_lifetime_seconds", "600"));
            Check(invalid.StatusCode == HttpStatusCode.UnprocessableEntity, "The redirect policy refuses unsafe or inexact URLs");
        }
        using (var shortSession = await Post(browser, "configuration", csrf, ("session_lifetime_seconds", "299")))
            Check(shortSession.StatusCode == HttpStatusCode.UnprocessableEntity, "Sessions have the API's minimum lifetime");
        using (var config = await Post(browser, "configuration", csrf, ("redirect_urls", "https://example.com/callback?source=app\nhttp://[::1]:3000/callback"), ("session_lifetime_seconds", "600")))
            Check(config.StatusCode == HttpStatusCode.SeeOther, "Exact HTTPS and loopback redirects are accepted");
        var configuration = await Page(browser, "/auth/configuration");
        Check(configuration.Contains("http://[::1]:3000/callback") && configuration.Contains("value=\"600\""), "The configuration persists");
        using (var forged = await HttpJourney.PostFormAsync(browser, "/api/v1/auth-management/configuration", new() { ["session_lifetime_seconds"] = "900" }))
            Check(forged.StatusCode == HttpStatusCode.Forbidden, "Configuration requires CSRF");
        var danger = await Page(browser, "/auth/danger");
        var id = Regex.Match(danger, "name=\"record_id\" required><option value=\"\">Choose a record</option><option value=\"([^\"]+)\"").Groups[1].Value;
        Check(id.Length == 26, "The final danger tab offers real records");
        using (var refused = await Post(browser, "danger/sessions", csrf, ("record_id", id), ("confirmation", "wrong name")))
            Check(refused.StatusCode == HttpStatusCode.UnprocessableEntity, "Destruction requires the exact project name");
        using var identity = await browser.GetFromJsonAsync<JsonDocument>("/api/v1/auth/me");
        var activeOrganization = identity!.RootElement.GetProperty("organizationId").GetString();
        var projectName = WebUtility.HtmlDecode(Regex.Match(initial, "data-project-name=\"([^\"]+)\"").Groups[1].Value);
        using (var revoked = await Post(browser, "danger/sessions", csrf, ("record_id", id), ("confirmation", projectName)))
            Check(revoked.StatusCode == HttpStatusCode.SeeOther, "Confirmed destruction calls the existing session API");
        Check(!Ids(await Page(browser, "/auth/sessions"), "sessions", "revoke").Contains(id), "The confirmed session is gone");
        var picker = await Page(browser, "/organizations/choose");
        var orgs = Regex.Matches(picker, "name=\"organizationId\" value=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();
        Check(orgs.Length > 1, "The picker lists the authenticated user's actual memberships");
        using var switchOrg = await HttpJourney.PostFormAsync(browser, "/api/v1/dashboard/switch-organization", new() { ["_csrf"] = Csrf(picker), ["organizationId"] = orgs.First(id => id != activeOrganization), ["returnUrl"] = "/auth/branding" });
        Check(switchOrg.StatusCode == HttpStatusCode.SeeOther, "Selection uses the existing authorized POST");
        Check(!(await Page(browser, switchOrg.Headers.Location!.OriginalString)).Contains(name), "Another project's settings never inherit this project's branding");
    }

    [Test]
    public async Task McpToolsCallProjectAuthenticationWithTheCallersRole()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        using var producer = scenario.CreateProviderClient();
        using (var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
            Check(login.StatusCode == HttpStatusCode.SeeOther, "The owner signs in");
        var secret = await HttpJourney.CreatePatAsync(browser, "Project authentication");
        var tools = (await HttpJourney.McpAsync(replica, secret, "tools/list", new { })).GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!).Where(name => name.StartsWith("auth_", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Check(tools.SequenceEqual(["auth_create_client", "auth_delete_client", "auth_delete_user", "auth_get_overview", "auth_list_clients", "auth_list_sessions",
            "auth_list_users", "auth_remove_provider", "auth_revoke_session", "auth_set_provider", "auth_set_user_disabled", "auth_update_settings"]), "The MCP inventory holds only working project authentication tools");

        var overview = await Tool(replica, secret, "auth_get_overview", new { });
        Check(overview.GetProperty("issuer").GetString()!.StartsWith("https://auth.terrascale.tech/", StringComparison.Ordinal) &&
            overview.GetProperty("counts").GetProperty("users").GetInt32() == 5, "The overview reads the project tenant");
        var users = await Tool(replica, secret, "auth_list_users", new { search = "northwind" });
        var user = users.GetProperty("items")[0].GetProperty("id").GetString()!;
        Check((await Tool(replica, secret, "auth_set_user_disabled", new { userId = user, disabled = true })).GetProperty("disabled").GetBoolean(), "An owner disables a user");
        Error(await Call(replica, secret, "auth_create_client", new { name = "CLI", type = "public", redirectUris = new[] { "http://127.0.0.1/callback" } }),
            TerraScale.Dashboard.Features.Auth.StepUpFlow.Required().Message);
        var clientsPage = await Page(browser, "/auth/clients");
        using (var created = await Post(browser, "clients", Csrf(clientsPage), ("name", "CLI"), ("type", "public"), ("redirect_uris", "http://127.0.0.1/callback")))
            Check(created.StatusCode == HttpStatusCode.OK && !(await created.Content.ReadAsStringAsync()).Contains("tsas_", StringComparison.Ordinal), "Hosted confirmation creates a public client without a secret");
        var clientId = (await Tool(replica, secret, "auth_list_clients", new { })).GetProperty("clients").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "CLI").GetProperty("client_id").GetString();
        var settings = await Tool(replica, secret, "auth_update_settings", new { signUp = false, themeColor = "#00AA55" });
        Check(!settings.GetProperty("methods").GetProperty("sign_up").GetBoolean() && settings.GetProperty("methods").GetProperty("password").GetBoolean() &&
            settings.GetProperty("branding").GetProperty("theme_color").GetString() == "#00aa55" && settings.GetProperty("branding").GetProperty("name").GetString() == "Northwind",
            "Omitted settings keep their values");
        Error(await Call(replica, secret, "auth_update_settings", new { passcode = false, password = false }), "Keep email passcode or password sign-in on.");
        var providerSecret = "oidc-" + Guid.NewGuid().ToString("N");
        var withOidc = await Tool(replica, secret, "auth_set_provider", new { provider = "oidc", clientId = "dashboard", clientSecret = providerSecret, issuer = "https://id.example.com/" });
        Check(withOidc.GetProperty("providers")[3] is var oidc && oidc.GetProperty("configured").GetBoolean() && oidc.GetProperty("issuer").GetString() == "https://id.example.com/" &&
            !withOidc.GetRawText().Contains(providerSecret, StringComparison.Ordinal), "An owner configures an OpenID Connect provider; the secret is never returned");
        Error(await Call(replica, secret, "auth_set_provider", new { provider = "apple", clientId = "app.example.web", clientSecret = "key", teamId = "1", keyId = "2" }), "The Apple team ID and key ID are 10 letters or digits.");
        Check(!(await Tool(replica, secret, "auth_remove_provider", new { provider = "oidc" })).GetProperty("providers")[3].GetProperty("configured").GetBoolean(), "An owner removes the provider");

        await Control(producer, "member");
        var readOnly = await Page(browser, "/auth/configuration");
        using (var refused = await Post(browser, "configuration", Csrf(readOnly), ("session_lifetime_seconds", "600")))
            Check(refused.StatusCode == HttpStatusCode.Forbidden, "A read-only member cannot change configuration");
        using (var refused = await Post(browser, "branding", Csrf(readOnly), ("name", "Forbidden")))
            Check(refused.StatusCode == HttpStatusCode.Forbidden, "A read-only member cannot change branding");
        Check((await Tool(replica, secret, "auth_list_clients", new { })).GetProperty("clients").GetArrayLength() == 2, "A member reads applications");
        Error(await Call(replica, secret, "auth_delete_client", new { clientId = clientId }), "Organization management is required.");
        await Control(producer, "");
        Error(await Call(replica, secret, "auth_get_overview", new { }), "Organization is unavailable.");
    }

    [Test]
    public async Task PagesShowAClearErrorUntilTsAuthServesTheProjectManagementApi()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root, liveProjects: true);
        using var browser = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        using (var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me"))
            Check(login.StatusCode == HttpStatusCode.SeeOther, "The owner signs in");
        foreach (var section in Sections)
            Check((await Page(browser, "/auth/" + section)).Contains("id=\"auth-error\"><p class=\"ts-alert-body\">Authentication for this project, or the requested record, is unavailable.</p>"),
                "The real gateway maps ts-auth's 404 to a clear error: " + section);
        using (var change = await Post(browser, "branding", Csrf(await Page(browser, "/auth/branding")), ("name", "Pending")))
            Check(change.StatusCode == HttpStatusCode.NotFound, "A change reports ts-auth's 404");
        // Six pages, the branding form, the refused change and its re-rendered page.
        using var observed = await producer.GetAsync("/__test/observations");
        using var observations = JsonDocument.Parse(await observed.Content.ReadAsStringAsync());
        Check(observations.RootElement.GetProperty("projectManagementRequests").GetInt32() == Sections.Length + 3 &&
            observations.RootElement.GetProperty("managementRequests").GetInt32() == 0 && observations.RootElement.GetProperty("contractFailures").GetInt32() == 0,
            "Every page called the project management API with the user's token and never the dashboard tenant's management key");
    }

    private static async Task<string> Page(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Check(response.StatusCode == HttpStatusCode.OK, "The page loads: " + path);
        return await response.Content.ReadAsStringAsync();
    }

    private static string[] Ids(string html, string family, string action) =>
        Regex.Matches(html, $"action=\"/api/v1/auth-management/{family}/([0-9A-Z]{{26}})/{action}\"").Select(match => match.Groups[1].Value).ToArray();

    private static string Csrf(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"_csrf\" value=\"([^\"]+)\"").Groups[1].Value);

    private static Task<HttpResponseMessage> Post(HttpClient client, string path, string csrf, params (string Key, string Value)[] fields) =>
        HttpJourney.PostConfirmedFormAsync(client, "/api/v1/auth-management/" + path, fields.Append(("_csrf", csrf)).ToDictionary(field => field.Item1, field => field.Item2));

    private static Task<JsonElement> Call(HttpClient client, string secret, string name, object arguments) =>
        HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });

    private static async Task<JsonElement> Tool(HttpClient client, string secret, string name, object arguments)
    {
        var result = (await Call(client, secret, name, arguments)).GetProperty("result");
        Check(!(result.TryGetProperty("isError", out var error) && error.GetBoolean()), name + " succeeds: " + result.GetRawText());
        return result.GetProperty("structuredContent");
    }

    private static void Error(JsonElement response, string expected)
    {
        var result = response.GetProperty("result");
        Check(result.GetProperty("isError").GetBoolean() && result.GetProperty("content")[0].GetProperty("text").GetString() == expected, "The tool reports: " + expected);
    }

    private static async Task Control(HttpClient producer, string role)
    {
        using var response = await producer.PostAsJsonAsync("/__test/control", new { command = "workspace-role", role });
        Check(response.StatusCode == HttpStatusCode.OK, "The workspace producer changes the caller's organization role");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
