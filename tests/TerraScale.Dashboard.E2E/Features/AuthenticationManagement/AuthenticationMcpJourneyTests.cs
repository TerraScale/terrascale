using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.AuthenticationManagement;

[ClassDataSource<DashboardRootFixture>]
public sealed class AuthenticationMcpJourneyTests(DashboardRootFixture fixture)
{
    private static readonly Dictionary<string, object> UnprovisionedCalls = new()
    {
        ["auth_get_overview"] = new { },
        ["auth_list_resources"] = new { section = "users" },
        ["auth_list_audit_events"] = new { },
        ["auth_save_resource"] = new { section = "users", values = new { name = "Foreign tenant", email = "foreign@example.com" } },
        ["auth_delete_resource"] = new { section = "users", id = "foreign-user" },
        ["auth_run_resource_action"] = new { section = "users", id = "foreign-user", action = "block" },
        ["auth_set_strategy"] = new { strategy = "password", enabled = false },
        ["auth_update_branding"] = new { name = "Foreign tenant", logoUrl = "https://example.com/logo.png", themeColor = "#123456" },
        ["auth_get_emails"] = new { template = "sign_in_code", locale = "en" },
        ["auth_update_email_branding"] = new { branding = new { brand_name = "Foreign tenant", sender_name = "Foreign sender" }, clear = new[] { "reply_to" } },
        ["auth_update_email_template"] = new { template = "sign_in_code", locale = "en", fields = new { subject = "Foreign tenant email" } },
        ["auth_reset_email_template"] = new { template = "sign_in_code", locale = "en" },
        ["auth_preview_email"] = new { template = "sign_in_code", locale = "en", colorScheme = "dark" },
        ["auth_send_test_email"] = new { template = "sign_in_code", locale = "en" }
    };
    private const string Unavailable = "Authentication management is unavailable for this project. ts-auth has a project-scoped tenant, but its customer management API and matching project credential binding are unavailable to the dashboard.";

    [Test]
    public async Task DangerZoneIsTheLastNavSectionAndStaysUnavailable()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
        Check(login.StatusCode == HttpStatusCode.SeeOther, "A signed-in user opens the authentication console");
        using var response = await browser.GetAsync("/auth/danger");
        var html = await response.Content.ReadAsStringAsync();
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable, "The danger zone route answers 503 while ts-auth management is unavailable");
        Check(html.Contains("id=\"auth-danger\"") && html.Contains("Sign out all users") && html.Contains("Reset emails"),
            "The danger zone lists both rows with their actions");
        Check(Regex.Matches(html, "<button[^>]*disabled>(Sign out all users|Reset emails)</button>").Count == 2, "Both danger actions are disabled");
        var nav = Regex.Match(html, "<nav aria-label=\"Authentication sections\">(.*?)</nav>", RegexOptions.Singleline).Groups[1].Value;
        var links = Regex.Matches(nav, "href=\"/auth/([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();
        Check(links.Length > 0 && links[^1] == "danger", "The danger zone is the last authentication nav section");
    }

    [Test]
    public async Task NativePatDiscoversTypedAuthContractsAndNeverBorrowsDashboardTenantAuthority()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        using var producer = scenario.CreateProviderClient();
        using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
        Check(login.StatusCode == HttpStatusCode.SeeOther, "A real signed native provider grant admits the auth-management journey");
        var secret = await HttpJourney.CreatePatAsync(browser, "Native auth-management contract");
        var tools = await Tools(replica, secret);
        using var manifest = await ReferenceFixtures.ReadManifestAsync(fixture.Root);
        var sourceTools = manifest.RootElement.GetProperty("mcp").GetProperty("toolsList").EnumerateArray()
            .SelectMany(page => page.GetProperty("result").GetProperty("tools").EnumerateArray()).Where(tool => tool.GetProperty("name").GetString()!.StartsWith("auth_", StringComparison.Ordinal)).ToArray();
        Check(sourceTools.Length == 15, "The authenticated original Go manifest contains the fifteen auth-management contracts");
        foreach (var source in sourceTools)
        {
            var name = source.GetProperty("name").GetString()!;
            var actual = tools.Single(tool => tool.GetProperty("name").GetString() == name);
            Check(Properties(actual.GetProperty("inputSchema")).SequenceEqual(Properties(source.GetProperty("inputSchema"))),
                "The native MCP inventory preserves the original flat argument names: " + name);
            Check(Properties(actual.GetProperty("outputSchema")).SequenceEqual(Properties(source.GetProperty("outputSchema"))),
                "The native MCP inventory advertises concrete original output fields: " + name);
        }
        Check(!tools.Any(tool => tool.GetProperty("name").GetString() == "set_project_authentication"),
            "There is no deployment feature switch in the migrated inventory");
        var described = await Call(replica, secret, "auth_describe_sections", new { });
        var catalog = described.GetProperty("result").GetProperty("structuredContent");
        Check(catalog.GetProperty("sections").GetArrayLength() == 11 && catalog.GetProperty("connectionProviders").GetArrayLength() == 5 &&
            catalog.GetProperty("strategies").GetArrayLength() == 8, "Authenticated static metadata describes all record sections, connection providers and source strategy defaults");
        var sections = catalog.GetProperty("sections").EnumerateArray().ToArray();
        var users = sections.Single(section => section.GetProperty("key").GetString() == "users");
        Check(users.GetProperty("editable").GetBoolean() && users.GetProperty("actions")[0].GetString() == "block" &&
            users.GetProperty("fields").EnumerateArray().Single(field => field.GetProperty("key").GetString() == "email").GetProperty("unique").GetBoolean(),
            "The catalog preserves field uniqueness and user actions rather than fabricating tenant records");
        var membership = sections.Single(section => section.GetProperty("key").GetString() == "memberships");
        Check(membership.GetProperty("fields").EnumerateArray().Where(field => field.GetProperty("key").GetString() is "organization" or "user")
            .All(field => field.GetProperty("immutable").GetBoolean()), "Membership identities remain creation-only metadata");
        var providers = catalog.GetProperty("connectionProviders").EnumerateArray().ToArray();
        Check(providers.Single(provider => provider.GetProperty("protocol").GetString() == "OIDC").GetProperty("fields")
            .EnumerateArray().Single(field => field.GetProperty("key").GetString() == "client_secret").GetProperty("type").GetString() == "password",
            "A provider secret is explicitly described as a write-only field");
        using var native = await browser.GetAsync("/auth");
        Check(native.StatusCode == HttpStatusCode.ServiceUnavailable && (await native.Content.ReadAsStringAsync()).Contains(Unavailable, StringComparison.Ordinal),
            "The native console and MCP expose the same authoritative project-provisioning gap");
        foreach (var (name, arguments) in UnprovisionedCalls)
            Error(await Call(replica, secret, name, arguments), Unavailable);

        Error(await Call(replica, secret, "auth_get_overview", new { organizationId = "foreign-org", projectId = "foreign-project" }), "Organization is unavailable.");
        await Control(producer, "member");
        Error(await Call(replica, secret, "auth_update_branding", new { name = "Forbidden member change" }), "Organization management is required.");
        Error(await Call(replica, secret, "auth_get_overview", new { }), Unavailable);
        await Control(producer, "admin");
        Error(await Call(replica, secret, "auth_update_branding", new { name = "Admin still needs a producer binding" }), Unavailable);
        await Control(producer, "");
        Error(await Call(replica, secret, "auth_get_overview", new { }), "Organization is unavailable.");
        using (var profile = await browser.GetAsync("/profile"))
        {
            Check(profile.StatusCode == HttpStatusCode.OK, "An authenticated account can still read its profile after the provider removes organization access");
            var html = await profile.Content.ReadAsStringAsync();
            Check(html.Contains("No active organization role", StringComparison.Ordinal) && html.Contains("No scoped permissions", StringComparison.Ordinal),
                "The native profile reports actual empty roles and capabilities rather than stale provider authorization");
            Check(!html.Contains("Write selected project data", StringComparison.Ordinal) && !html.Contains("Manage organization resources", StringComparison.Ordinal) &&
                !html.Contains("Provision organization resources", StringComparison.Ordinal), "Removed provider membership cannot leave actionable capabilities in the native profile");
            Check(!html.Contains("Demo MFA", StringComparison.Ordinal) && !html.Contains("Passkey records", StringComparison.Ordinal),
                "Missing real factor contracts never turn source demo security records into production account claims");
        }
        var metadataAfterRemoval = await Call(replica, secret, "auth_describe_sections", new { });
        Check(metadataAfterRemoval.GetProperty("result").GetProperty("structuredContent").GetProperty("sections").GetArrayLength() == 11,
            "Removed project access does not turn public authenticated field metadata into a tenant identity record");
        using var observed = await producer.GetAsync("/__test/observations");
        Check(observed.StatusCode == HttpStatusCode.OK, "The external producer reports its observed network requests");
        using var observations = JsonDocument.Parse(await observed.Content.ReadAsStringAsync());
        Check(observations.RootElement.GetProperty("managementRequests").GetInt32() == 0 &&
            observations.RootElement.GetProperty("contractFailures").GetInt32() == 0,
            "No customer-project tool borrowed the dashboard management key, changed a foreign identity, or issued an unauthorized producer request");
    }

    private static IEnumerable<string> Properties(JsonElement schema) => schema.TryGetProperty("properties", out var properties)
        ? properties.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal) : [];
    private static Task<JsonElement> Call(HttpClient client, string secret, string name, object arguments) =>
        HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
    private static void Error(JsonElement response, string expected)
    {
        var result = response.GetProperty("result");
        Check(result.GetProperty("isError").GetBoolean() && result.GetProperty("content")[0].GetProperty("text").GetString() == expected &&
            !result.TryGetProperty("structuredContent", out _), "The authenticated app reports its safe scope or unavailable error without invented records: " + expected);
    }
    private static async Task Control(HttpClient producer, string role)
    {
        using var response = await producer.PostAsJsonAsync("/__test/control", new { command = "workspace-role", role });
        Check(response.StatusCode == HttpStatusCode.OK, "The external workspace producer changes membership while persisted dashboard role stays stale");
    }
    private static async Task<IReadOnlyList<JsonElement>> Tools(HttpClient client, string secret)
    {
        var tools = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var response = await HttpJourney.McpAsync(client, secret, "tools/list", cursor is null ? new { } : (object)new { cursor });
            var result = response.GetProperty("result");
            tools.AddRange(result.GetProperty("tools").EnumerateArray().Select(tool => tool.Clone()));
            cursor = result.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            Check(tools.Count <= 500, "The native MCP tool inventory remains bounded while following actual pagination");
        } while (cursor is not null);
        return tools;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
