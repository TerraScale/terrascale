using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.E2E.Features.Databases;
using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.OnboardingProjects;

[ClassDataSource<DashboardE2EFixture>]
public sealed class OnboardingProjectsAuditJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task InvalidAccountNameKeepsTheSubmittedDraft()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var login = await HttpJourney.LoginAsync(client, returnTo: "/onboarding");
        var name = new string('N', 129) + " <draft>";
        using var invalid = await PostStepAsync(client, new() { ["step"] = "account-information", ["displayName"] = name });
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(invalid.Headers.Location).IsNull();
        var page = await invalid.Content.ReadAsStringAsync();
        await Assert.That(page).Contains("Use a name with 128 characters or fewer.");
        await Assert.That(page).Contains("value=\"" + WebUtility.HtmlEncode(name) + "\"");
        await Assert.That(page).Contains("name=\"_csrf\"");
        await SaveStepAsync(client, new() { ["step"] = "account-information", ["displayName"] = "Corrected draft" });
    }

    [Test]
    public async Task InvalidPaymentAndProjectInputsKeepTheirSelections()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var login = await HttpJourney.LoginAsync(client, returnTo: "/onboarding");
        await SaveStepAsync(client, new() { ["step"] = "account-information", ["displayName"] = "Draft account" });
        using (var invalid = await PostStepAsync(client, new() { ["step"] = "payment-method", ["plan"] = "starter", ["interval"] = "invalid" }))
        {
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            var page = await invalid.Content.ReadAsStringAsync();
            await Assert.That(page).Contains("Choose an available plan and billing interval.");
            await Assert.That(page).Contains("value=\"starter\" checked=\"checked\"");
        }
        await SaveStepAsync(client, new() { ["step"] = "payment-method", ["bypass"] = "1" });
        var name = new string('P', 101);
        using var rejected = await PostStepAsync(client, new() { ["step"] = "project", ["name"] = name, ["region"] = "SFO" });
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        var draft = await rejected.Content.ReadAsStringAsync();
        await Assert.That(draft).Contains("value=\"" + name + "\"");
        await Assert.That(draft).Contains("value=\"SFO\" selected=\"selected\"");
        await Assert.That(draft).Contains("Use a project name with 100 characters or fewer.");
    }

    [Test]
    public async Task SidebarCreateProjectLinkRendersTheRequestedDialog()
    {
        using var client = fixture.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var shell = await client.GetStringAsync("/organization?tab=projects");
        var anchor = Regex.Matches(shell, "<a\\b[^>]*>.*?</a>", RegexOptions.Singleline)
            .Single(match => match.Value.Contains(">Create new project</span>", StringComparison.Ordinal)).Value;
        var link = WebUtility.HtmlDecode(Regex.Match(anchor, "href=\"([^\"]+)\"").Groups[1].Value);
        await Assert.That(link.Length).IsGreaterThan(0);
        var page = await client.GetStringAsync(link);
        await Assert.That(page).Contains("<dialog id=\"create-project\"")
            .Because("The sidebar Create new project link must render its requested dialog");
        await Assert.That(page).Contains("data-init=\"el.showModal()\"");
    }

    [Test]
    public async Task InvalidProjectPlacementKeepsTheCreateFormAndDraft()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        await AdmitSecondProjectAsync(client, producer);
        var before = await JsonAsync(producer, "/__test/observations");
        using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/projects", new()
        {
            ["_csrf"] = await HttpJourney.CsrfAsync(client, "/organization?tab=projects"),
            ["name"] = "Recover this draft",
            ["region"] = "ZZZ",
            ["regions"] = "ZZZ"
        });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        var after = await JsonAsync(producer, "/__test/observations");
        await Assert.That(after.GetProperty("projectCreates").GetInt32()).IsEqualTo(before.GetProperty("projectCreates").GetInt32());
        var page = await response.Content.ReadAsStringAsync();
        await Assert.That(page).Contains("Choose serving project regions.");
        await Assert.That(page).Contains("<dialog id=\"create-project\"")
            .Because("A rejected project submission must keep its editable form visible for retry");
        await Assert.That(page).Contains("value=\"Recover this draft\"");
        await Assert.That(page).Contains("data-init=\"el.showModal()\"");
    }

    [Test]
    public async Task InterruptedOnboardingRejectsAnotherSelectionBeforeRenamingTheDefaultProject()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        await RegisterToProjectAsync(client);
        var initial = await JsonAsync(client, "/api/v1/dashboard/context");
        var defaultId = initial.GetProperty("selection").GetProperty("projectId").GetString()!;
        var originalName = initial.GetProperty("organizations").EnumerateArray().SelectMany(org => org.GetProperty("projects").EnumerateObject())
            .Single(project => project.Name == defaultId).Value.GetProperty("name").GetString();
        await CreateProjectAsync(client, producer, "Other selected project");
        var context = await JsonAsync(client, "/api/v1/dashboard/context");
        var otherId = context.GetProperty("organizations").EnumerateArray().SelectMany(org => org.GetProperty("projects").EnumerateObject())
            .Single(project => project.Value.GetProperty("name").GetString() == "Other selected project").Name;
        await SwitchProjectAsync(client, otherId, "/onboarding");
        await Assert.That((await JsonAsync(client, "/api/v1/dashboard/context")).GetProperty("selection").GetProperty("projectId").GetString()).IsEqualTo(otherId);
        using var response = await HttpJourney.PostFormAsync(client, "/onboarding/step", new()
        {
            ["_csrf"] = await HttpJourney.CsrfAsync(client, "/onboarding"),
            ["step"] = "project",
            ["name"] = "Must not rename the default",
            ["region"] = "GRU"
        });
        var observed = await JsonAsync(producer, "/__test/observations");
        var remoteName = observed.GetProperty("workspaceProjects").EnumerateArray().Single(project => project.GetProperty("id").GetString() == defaultId)
            .GetProperty("name").GetString();
        var saved = await JsonAsync(client, "/api/v1/dashboard/context");
        var localName = saved.GetProperty("organizations").EnumerateArray().SelectMany(org => org.GetProperty("projects").EnumerateObject())
            .Single(project => project.Name == defaultId).Value.GetProperty("name").GetString();
        await ProtocolAcceptedAsync(producer);
        await Assert.That(remoteName).IsEqualTo(originalName)
            .Because($"Onboarding must reject a nondefault selection before provider mutation; HTTP {(int)response.StatusCode}, remote name '{remoteName}', dashboard name '{localName}'");
        await Assert.That(localName).IsEqualTo(originalName);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("value=\"Must not rename the default\"");
        await Assert.That((await JsonAsync(client, "/api/v1/auth/me")).GetProperty("onboardingCompleted").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task SwitchingProjectFromDatabaseDetailsReturnsToTheDatabaseList()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var initialProjectId = (await JsonAsync(client, "/api/v1/dashboard/context")).GetProperty("selection").GetProperty("projectId").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = "scope-origin", ["region"] = "GRU" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var database = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var oldPath = "/databases/" + database.RootElement.GetProperty("id").GetString() + "?created=1";
        using (var detail = await client.GetAsync(oldPath)) await Assert.That(detail.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await CreateProjectAsync(client, producer, "Empty destination");
        var context = await JsonAsync(client, "/api/v1/dashboard/context");
        var newId = context.GetProperty("organizations").EnumerateArray().SelectMany(org => org.GetProperty("projects").EnumerateObject())
            .Single(project => project.Value.GetProperty("name").GetString() == "Empty destination").Name;
        using var switched = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-project", new()
        { ["_csrf"] = csrf, ["projectId"] = newId, ["returnUrl"] = oldPath });
        await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var destination = await client.GetAsync(switched.Headers.Location);
        await Assert.That(destination.StatusCode).IsEqualTo(HttpStatusCode.OK)
            .Because($"The switch must leave the prior project's database route; Location={switched.Headers.Location}");
        var switchedUri = new Uri(client.BaseAddress!, switched.Headers.Location!);
        await Assert.That(switchedUri.AbsolutePath).IsEqualTo("/databases");
        await Assert.That(QueryHelpers.ParseQuery(switchedUri.Query)["projectId"].ToString()).IsEqualTo(newId);
        await Assert.That((await JsonAsync(client, "/api/v1/dashboard/context")).GetProperty("selection").GetProperty("projectId").GetString()).IsEqualTo(newId);
        await Assert.That(await destination.Content.ReadAsStringAsync()).Contains("No databases");
        using var compatible = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-project", new()
        { ["_csrf"] = csrf, ["projectId"] = initialProjectId, ["returnUrl"] = oldPath });
        await Assert.That(compatible.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var compatibleUri = new Uri(client.BaseAddress!, compatible.Headers.Location!);
        await Assert.That(compatibleUri.AbsolutePath).IsEqualTo(oldPath.Split('?')[0]);
        await Assert.That(QueryHelpers.ParseQuery(compatibleUri.Query)["created"].ToString()).IsEqualTo("1");
        await Assert.That(QueryHelpers.ParseQuery(compatibleUri.Query)["projectId"].ToString()).IsEqualTo(initialProjectId);
        using var preservedDetail = await client.GetAsync(compatible.Headers.Location);
        await Assert.That(preservedDetail.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await ProtocolAcceptedAsync(producer);
    }

    [Test]
    public async Task ProducerAuthenticatedSetupResumesAfterLoginAndValidationFailure()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var first = scenario.CreateClient();
        using var resumed = scenario.CreateClient(replica: true);
        using var login = await HttpJourney.LoginAsync(first, returnTo: "/onboarding");
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await SaveStepAsync(first, new() { ["step"] = "account-information", ["displayName"] = "Audit account" });
        using var relogin = await HttpJourney.LoginAsync(resumed, returnTo: "/onboarding");
        await Assert.That(relogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(await resumed.GetStringAsync("/onboarding?payment=canceled")).Contains("Payment setup canceled");
        await SaveStepAsync(resumed, new() { ["step"] = "payment-method", ["bypass"] = "1" });
        using var invalid = await PostStepAsync(first, new() { ["step"] = "project", ["name"] = "Audit project", ["region"] = "ZZZ" });
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        var draft = await invalid.Content.ReadAsStringAsync();
        await Assert.That(draft).Contains("Choose a supported project region.");
        await Assert.That(draft).Contains("value=\"Audit project\"");
        await Assert.That(draft).Contains("value=\"ZZZ\" selected");
        await SaveStepAsync(resumed, new() { ["step"] = "project", ["name"] = "Audit project", ["region"] = "GRU" });
        var done = await JsonAsync(first, "/api/v1/auth/me");
        await Assert.That(done.GetProperty("onboardingCompleted").GetBoolean()).IsTrue();
        await SaveStepAsync(first, new() { ["step"] = "project", ["name"] = "Repeated submission", ["region"] = "SJC" });
        var context = await JsonAsync(first, "/api/v1/dashboard/context");
        var projectId = done.GetProperty("projectId").GetString()!;
        var project = context.GetProperty("organizations").EnumerateArray().SelectMany(org => org.GetProperty("projects").EnumerateObject()).Single(item => item.Name == projectId).Value;
        await Assert.That(project.GetProperty("name").GetString()).IsEqualTo("Audit project");
        await Assert.That(project.GetProperty("region").GetString()).IsEqualTo("GRU");
    }

    [Test]
    public async Task RepeatedScopedSelectionRejectsForeignIdsAndExternalReturns()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var owner = scenario.CreateClient();
        using var outsider = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(owner);
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(outsider);
        var before = await JsonAsync(owner, "/api/v1/dashboard/context");
        var projectId = before.GetProperty("selection").GetProperty("projectId").GetString()!;
        var foreignId = (await JsonAsync(outsider, "/api/v1/dashboard/context")).GetProperty("selection").GetProperty("projectId").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=projects");
        using (var denied = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/switch-project", new()
        { ["_csrf"] = csrf, ["projectId"] = foreignId, ["returnUrl"] = "/databases" }))
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        foreach (var returnUrl in new[] { "https://outside.invalid/path", "//outside.invalid/path", "/organization?tab=projects" })
        {
            using var response = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/switch-project", new()
            { ["_csrf"] = csrf, ["projectId"] = projectId, ["returnUrl"] = returnUrl });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var target = new Uri(owner.BaseAddress!, response.Headers.Location!);
            await Assert.That(target.AbsolutePath).IsEqualTo(returnUrl.StartsWith("/organization", StringComparison.Ordinal) ? "/organization" : "/");
            var query = QueryHelpers.ParseQuery(target.Query);
            await Assert.That(query["projectId"].ToString()).IsEqualTo(projectId);
            await Assert.That(query["organizationId"].ToString()).IsEqualTo(before.GetProperty("selection").GetProperty("organizationId").GetString());
            if (target.AbsolutePath == "/organization") await Assert.That(query["tab"].ToString()).IsEqualTo("projects");
        }
        var after = await JsonAsync(owner, "/api/v1/dashboard/context");
        await Assert.That(after.GetProperty("selection").GetRawText()).IsEqualTo(before.GetProperty("selection").GetRawText());
        await ProtocolAcceptedAsync(producer);
    }

    private static async Task RegisterToProjectAsync(HttpClient client)
    {
        const string email = "onboarding-selection-audit@example.test";
        const string password = "Audit-local-password-123";
        using var verified = await HttpJourney.LoginAsync(client, email, password, "/onboarding", signUp: true);
        await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await SaveStepAsync(client, new() { ["step"] = "account-information", ["displayName"] = "Audit setup" });
        await SaveStepAsync(client, new() { ["step"] = "payment-method", ["bypass"] = "1" });
    }

    private static async Task CreateProjectAsync(HttpClient client, HttpClient producer, string name)
    {
        await AdmitSecondProjectAsync(client, producer);
        using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/projects", new()
        { ["_csrf"] = await HttpJourney.CsrfAsync(client, "/organization?tab=projects"), ["name"] = name, ["region"] = "GRU", ["regions"] = "GRU" });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
    }

    // Test admission permits a second project; it does not simulate a paid checkout.
    private static async Task AdmitSecondProjectAsync(HttpClient client, HttpClient producer)
    {
        var organizationId = (await JsonAsync(client, "/api/v1/auth/me")).GetProperty("organizationId").GetString();
        using var admission = await producer.PostAsJsonAsync("/__test/control", new { command = "plan", organizationId, value = OrganizationRecord.StarterPlanId });
        await Assert.That(admission.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    private static async Task SwitchProjectAsync(HttpClient client, string projectId, string returnUrl)
    {
        using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-project", new()
        { ["_csrf"] = await HttpJourney.CsrfAsync(client, "/organization?tab=projects"), ["projectId"] = projectId, ["returnUrl"] = returnUrl });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
    }

    private static async Task SaveStepAsync(HttpClient client, Dictionary<string, string> form)
    {
        using var response = await PostStepAsync(client, form);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(response.Headers.Location?.OriginalString is "/onboarding" or "/").IsTrue();
    }

    private static async Task<HttpResponseMessage> PostStepAsync(HttpClient client, Dictionary<string, string> form)
    {
        form["_csrf"] = await HttpJourney.CsrfAsync(client, "/organization?tab=projects");
        return await HttpJourney.PostFormAsync(client, "/onboarding/step", form);
    }

    private static async Task ProtocolAcceptedAsync(HttpClient producer) =>
        await Assert.That((await JsonAsync(producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);

    private static async Task<JsonElement> JsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
