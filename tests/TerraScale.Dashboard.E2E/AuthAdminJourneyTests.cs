using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
public sealed class AuthAdminJourneyTests(DashboardE2EFixture fixture)
{
    private static readonly string[] AdminPages =
    [
        "/admin", "/admin/users", "/admin/organizations", "/admin/projects",
        "/admin/databases", "/admin/estate?tab=hosts&q=owned%20project",
        "/admin/usage", "/admin/plans", "/admin/pricing", "/admin/unknown"
    ];

    private static readonly string[] AdminActions =
    [
        "/api/v1/admin/users/foreign-account",
        "/api/v1/admin/organizations/foreign-account/foreign-org",
        "/api/v1/admin/projects/foreign-account/foreign-org/foreign-project",
        "/api/v1/admin/estate/action", "/api/v1/admin/plans",
        "/api/v1/admin/plans/foreign-plan/visibility",
        "/api/v1/admin/plans/foreign-plan/delete"
    ];

    private static readonly string[] AdminTools =
    [
        "admin_list_users", "admin_list_organizations", "admin_list_projects",
        "admin_list_databases", "admin_get_estate", "admin_get_usage",
        "admin_list_plans", "admin_update_user", "admin_rename_organization",
        "admin_rename_project", "admin_record_estate_action", "admin_save_plan",
        "admin_set_plan_visibility", "admin_delete_plan"
    ];

    [Test]
    public Task DesktopAndMobileAuthOnboardingAndCliBrowserJourneys() =>
        BrowserJourneys.RunAsync(fixture, "auth-browser-flow.mjs");

    [Test]
    public async Task NativeAuthenticationAndAdminAuthorizationAcrossRunningReplicas()
    {
        using var client = fixture.CreateClient();
        using var replica = fixture.CreateClient(replica: true);

        foreach (var path in AdminPages)
        {
            using var anonymous = await client.GetAsync(path);
            Check(anonymous.StatusCode == HttpStatusCode.SeeOther &&
                anonymous.Headers.Location?.OriginalString == "/login?returnTo=" + Uri.EscapeDataString(path),
                "Anonymous admin GET preserves the complete local return path: " + path);
        }
        foreach (var path in AdminActions)
        {
            using var anonymous = await client.PostAsync(path, new StringContent("untrusted form"));
            Check(anonymous.StatusCode == HttpStatusCode.SeeOther && anonymous.Headers.Location?.OriginalString == "/login",
                "Anonymous admin POST returns to login before parsing: " + path);
        }
        using (var anonymousHead = await client.SendAsync(new(HttpMethod.Head, "/admin/users")))
            Check(anonymousHead.StatusCode == HttpStatusCode.SeeOther && anonymousHead.Headers.Location?.OriginalString == "/login",
                "Anonymous HEAD matches the original login redirect");

        using var loginPage = await client.GetAsync("/login?returnTo=%2Fadmin%2Fusers");
        Check(loginPage.StatusCode == HttpStatusCode.OK, "Native login form is available");
        var csrf = Hidden(await loginPage.Content.ReadAsStringAsync(), "_csrf");
        Check(csrf.Length == 64 && loginPage.Headers.CacheControl?.NoStore == true, "Auth form supplies CSRF and prevents caching");
        var credentials = new Dictionary<string, string>
        {
            ["email"] = "user@example.com",
            ["password"] = "test-password",
            ["redirectTo"] = "/admin/users"
        };
        using (var missing = await client.PostAsync("/login", new FormUrlEncodedContent(credentials)))
            Check(missing.StatusCode == HttpStatusCode.Forbidden, "A missing form token cannot sign in");
        credentials["_csrf"] = (csrf[0] == 'a' ? "b" : "a") + csrf[1..];
        using (var forged = await client.PostAsync("/login", new FormUrlEncodedContent(credentials)))
            Check(forged.StatusCode == HttpStatusCode.Forbidden, "A forged form token cannot sign in");
        credentials["_csrf"] = csrf;
        using (var foreignRequest = new HttpRequestMessage(HttpMethod.Post, "/login") { Content = new FormUrlEncodedContent(credentials) })
        {
            foreignRequest.Headers.Add("Origin", "https://untrusted.example");
            using var foreign = await client.SendAsync(foreignRequest);
            Check(foreign.StatusCode == HttpStatusCode.Forbidden, "Foreign Origin cannot sign in using a valid token");
        }
        using var signedIn = await client.PostAsync("/login", new FormUrlEncodedContent(credentials));
        Check(signedIn.StatusCode == HttpStatusCode.SeeOther && signedIn.Headers.Location?.OriginalString == "/admin/users",
            "Native login preserves a local admin return path");
        var sessionCookie = SessionCookie(signedIn);
        Check(signedIn.Headers.GetValues("Set-Cookie").Any(value => value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal) &&
            value.Contains("httponly", StringComparison.OrdinalIgnoreCase)), "Browser session is HttpOnly");

        using (var current = await client.GetAsync("/api/v1/auth/me"))
        using (var replicated = await Request(replica, HttpMethod.Get, "/api/v1/auth/me", sessionCookie))
        {
            Check(current.StatusCode == HttpStatusCode.OK && replicated.StatusCode == HttpStatusCode.OK,
                "The issued browser cookie authenticates on both running replicas");
            using var first = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
            using var second = JsonDocument.Parse(await replicated.Content.ReadAsStringAsync());
            Check(first.RootElement.GetProperty("userId").GetString() == second.RootElement.GetProperty("userId").GetString() &&
                first.RootElement.GetProperty("accountId").GetString() == second.RootElement.GetProperty("accountId").GetString() &&
                first.RootElement.GetProperty("role").GetString() == "owner", "Replicas resolve the same persisted ordinary owner");
        }

        foreach (var server in new[] { client, replica })
        {
            foreach (var path in AdminPages)
                await Denied(server, HttpMethod.Get, path, sessionCookie);
            foreach (var path in AdminActions)
                await Denied(server, HttpMethod.Post, path, sessionCookie);
            using var deniedHead = await Request(server, HttpMethod.Head, "/admin/users", sessionCookie);
            Check(deniedHead.StatusCode == HttpStatusCode.Forbidden, "HEAD cannot bypass platform authorization");
        }
        using (var unknown = await client.PostAsync("/api/v1/admin/unmapped", new StringContent("{}")))
            Check(unknown.StatusCode == HttpStatusCode.NotFound, "Unknown native administration APIs remain unmapped");

        using var tokenPage = await client.GetAsync("/profile?tab=access-tokens");
        Check(tokenPage.StatusCode == HttpStatusCode.OK, "Native personal access token form is available");
        var tokenCsrf = Hidden(await tokenPage.Content.ReadAsStringAsync(), "_csrf");
        var tokenName = "TUnit ordinary-owner " + Guid.NewGuid().ToString("N");
        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/access-tokens", new Dictionary<string, string>
        {
            ["_csrf"] = tokenCsrf,
            ["name"] = tokenName,
            ["expiresInDays"] = "30",
            ["password"] = "test-password"
        });
        Check(created.StatusCode == HttpStatusCode.OK, "A fresh password grant creates the PAT through its native form");
        var createdHtml = await created.Content.ReadAsStringAsync();
        var secret = InputById(createdHtml, "access-token-secret");
        Check(Regex.IsMatch(secret, "^tspat_[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant), "PAT plaintext is displayed after creation");
        var tokenRow = Regex.Matches(createdHtml, "<tr\\b[^>]*>.*?</tr>", RegexOptions.CultureInvariant | RegexOptions.Singleline)
            .Single(row => row.Value.Contains(tokenName, StringComparison.Ordinal)).Value;
        var revokePath = Regex.Match(tokenRow, "action=\"(/api/v1/dashboard/profile/access-tokens/[^\"/]+/revoke)\"", RegexOptions.CultureInvariant).Groups[1].Value;
        Check(revokePath.Length > 0, "The new token has a native revoke form");

        using (var listing = await Mcp(replica, "tools/list", new { }, secret))
        {
            var result = await McpJson(listing);
            var names = result.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString()).ToArray();
            Check(names.Length > 0 && !names.Intersect(AdminTools).Any(), "Ordinary owners see no platform administration tools");
        }
        foreach (var name in AdminTools)
        {
            using var unknown = await Mcp(replica, "tools/call", new
            {
                name,
                arguments = new { platformAdmin = true, accountId = "foreign-account", organizationId = "foreign-org" }
            }, secret);
            var result = await McpJson(unknown);
            Check(result.GetProperty("error").GetProperty("code").GetInt32() == -32602,
                "Unlisted administration tools cannot dispatch: " + name);
        }
        using (var validation = await Mcp(replica, "tools/call", new { name = "set_language", arguments = new { locale = "invalid-language" } }, secret))
        {
            var result = await McpJson(validation);
            Check(result.GetProperty("result").GetProperty("isError").GetBoolean() &&
                result.GetProperty("result").GetProperty("content").EnumerateArray().Any(content => content.GetProperty("text").GetString() == "Choose an available language."),
                "The real MCP pipeline preserves safe validation errors");
        }

        using (var logoutMissing = await client.PostAsync("/api/v1/auth/logout", new FormUrlEncodedContent(new Dictionary<string, string>())))
            Check(logoutMissing.StatusCode == HttpStatusCode.Forbidden, "Logout rejects a missing CSRF token");
        using (var stillSignedIn = await Request(replica, HttpMethod.Get, "/api/v1/auth/me", sessionCookie))
            Check(stillSignedIn.StatusCode == HttpStatusCode.OK, "Rejected logout does not invalidate the browser session");
        using (var logout = await client.PostAsync("/api/v1/auth/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = tokenCsrf })))
        {
            Check(logout.StatusCode == HttpStatusCode.SeeOther && logout.Headers.Location?.OriginalString == "/login", "Native logout returns to login");
            Check(logout.Headers.GetValues("Set-Cookie").Any(value => value.StartsWith("ts-dashboard-session=;", StringComparison.Ordinal)), "Native logout clears the browser cookie");
        }
        using (var ended = await Request(replica, HttpMethod.Get, "/api/v1/auth/me", sessionCookie))
            Check(ended.StatusCode == HttpStatusCode.Unauthorized, "Logout invalidates a captured browser cookie on another replica");
        using (var endedAdmin = await Request(replica, HttpMethod.Get, "/admin/users", sessionCookie))
            Check(endedAdmin.StatusCode == HttpStatusCode.SeeOther && endedAdmin.Headers.Location?.OriginalString == "/login?returnTo=%2Fadmin%2Fusers",
                "The ended browser returns to sign-in on admin routes");
        using (var independent = await Mcp(replica, "tools/list", new { }, secret))
            Check(independent.StatusCode == HttpStatusCode.OK, "The independent PAT family survives browser logout");

        using var freshLoginPage = await client.GetAsync("/login");
        credentials["_csrf"] = Hidden(await freshLoginPage.Content.ReadAsStringAsync(), "_csrf");
        credentials["redirectTo"] = "https://untrusted.example/";
        using (var freshLogin = await client.PostAsync("/login", new FormUrlEncodedContent(credentials)))
            Check(freshLogin.StatusCode == HttpStatusCode.SeeOther && freshLogin.Headers.Location?.OriginalString == "/",
                "A successful login cannot redirect to an external origin");
        using var freshTokenPage = await client.GetAsync("/profile?tab=access-tokens");
        var freshHtml = await freshTokenPage.Content.ReadAsStringAsync();
        Check(!freshHtml.Contains(secret, StringComparison.Ordinal), "Reload never redisplays token plaintext");
        using (var revoked = await client.PostAsync(revokePath, new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = Hidden(freshHtml, "_csrf") })))
            Check(revoked.StatusCode == HttpStatusCode.SeeOther, "Native token revocation succeeds");
        using (var revoked = await Mcp(replica, "tools/list", new { }, secret))
            Check(revoked.StatusCode == HttpStatusCode.Unauthorized, "A revoked PAT cannot reach MCP on another replica");
        using (var cleanupLogout = await HttpJourney.PostFormAsync(client, "/api/v1/auth/logout", new() { ["_csrf"] = Hidden(freshHtml, "_csrf") }))
            Check(cleanupLogout.StatusCode == HttpStatusCode.SeeOther, "The fresh browser grant is ended after the journey");
    }

    private static async Task Denied(HttpClient client, HttpMethod method, string path, string cookie)
    {
        using var response = await Request(client, method, path, cookie);
        Check(response.StatusCode == HttpStatusCode.Forbidden && await response.Content.ReadAsStringAsync() == "forbidden\n" &&
            response.Content.Headers.ContentType?.MediaType == "text/plain", "Ordinary owner cannot access administration: " + path);
    }

    private static Task<HttpResponseMessage> Request(HttpClient client, HttpMethod method, string path, string cookie)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-Platform-Admin", "true");
        request.Headers.Add("X-Role", "platform-admin");
        if (method == HttpMethod.Post) request.Content = new StringContent("invalid form: platform_admin=true");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Mcp(HttpClient client, string method, object parameters, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> McpJson(HttpResponseMessage response)
    {
        Check(response.StatusCode == HttpStatusCode.OK, "Authenticated MCP request succeeds");
        var body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = string.Join('\n', body.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].TrimStart()));
        using var parsed = JsonDocument.Parse(body);
        return parsed.RootElement.Clone();
    }

    private static string Hidden(string html, string name)
    {
        var input = Regex.Match(html, "<input\\b[^>]*\\bname=\"" + Regex.Escape(name) + "\"[^>]*>", RegexOptions.CultureInvariant).Value;
        var value = WebUtility.HtmlDecode(Regex.Match(input, "\\bvalue=\"([^\"]*)\"", RegexOptions.CultureInvariant).Groups[1].Value);
        Check(value.Length > 0, "The rendered native form supplies " + name);
        return value;
    }

    private static string InputById(string html, string id)
    {
        var input = Regex.Match(html, "<input\\b[^>]*\\bid=\"" + Regex.Escape(id) + "\"[^>]*>", RegexOptions.CultureInvariant).Value;
        return WebUtility.HtmlDecode(Regex.Match(input, "\\bvalue=\"([^\"]*)\"", RegexOptions.CultureInvariant).Groups[1].Value);
    }

    private static string SessionCookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal)).Split(';')[0];

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
