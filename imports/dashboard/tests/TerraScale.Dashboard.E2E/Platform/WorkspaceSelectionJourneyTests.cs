using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardRootFixture>]
public sealed class WorkspaceSelectionJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task ExplicitRedirectScopesCannotInheritAnotherWorkspace()
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new("?organizationId=01JA0000000000000000000001&projectId=01JA0000000000000000000002");
        const string projectOnly = "/databases?projectId=01JA0000000000000000000003#list";
        const string organizationOnly = "/organization?organizationId=01JA0000000000000000000001";
        await Assert.That(WorkspaceUrl.PreserveRequest(http, projectOnly)).IsEqualTo(projectOnly);
        await Assert.That(WorkspaceUrl.PreserveRequest(http, organizationOnly)).IsEqualTo(organizationOnly);
        await Assert.That(WorkspaceUrl.PreserveRequest(http, "/databases?tab=overview#list"))
            .IsEqualTo("/databases?tab=overview&organizationId=01JA0000000000000000000001&projectId=01JA0000000000000000000002#list");
    }

    [Test]
    public async Task ExplicitProjectSurvivesAnotherTabsSwitchAndRefusesUnknownOrAmbiguousScopes()
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        var organization = environment.Workspace.Organizations.Values.Single();
        var first = organization.Projects.Keys.Single();
        var second = new TerraScaleIdGenerator().NewId();
        organization.Projects.Add(second, new() { Id = second, OrganizationId = organization.Id, Name = "Second" });
        await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1");
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
        { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var csrf = await HttpJourney.CsrfAsync(client);
        using var switched = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-project", new()
        { ["_csrf"] = csrf, ["projectId"] = second, ["returnUrl"] = "/api-keys?kind=key#credentials" });
        await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(switched.Headers.Location!.OriginalString).Contains("projectId=" + second);
        await Assert.That(switched.Headers.Location.OriginalString).Contains("#credentials");
        var scope = "?organizationId=" + organization.Id + "&projectId=" + first;
        using var created = await HttpJourney.PostConfirmedFormAsync(client, "/api/v1/dashboard/credentials/key" + scope, new()
        { ["_csrf"] = csrf, ["name"] = "First tab key", ["scopes"] = "documents:read" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var firstKeys = await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/credentials" + scope);
        await Assert.That(firstKeys.GetArrayLength()).IsEqualTo(1);
        await Assert.That(firstKeys[0].GetProperty("projectId").GetString()).IsEqualTo(first);
        var secondKeys = await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/credentials?projectId=" + second);
        await Assert.That(secondKeys.GetArrayLength()).IsEqualTo(0);
        using var unknown = await client.GetAsync("/api/v1/auth/me?projectId=" + new TerraScaleIdGenerator().NewId());
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var ambiguous = await client.GetAsync("/api/v1/auth/me?projectId=" + first + "&projectId=" + second);
        await Assert.That(ambiguous.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var deleted = await HttpJourney.PostFormAsync(client,
            "/api/v1/dashboard/organization/projects/" + second + "/delete?organizationId=" + organization.Id + "&projectId=" + second,
            new() { ["_csrf"] = csrf, ["confirmation"] = "Second" });
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var afterDelete = new Uri(client.BaseAddress!, deleted.Headers.Location!);
        await Assert.That(QueryHelpers.ParseQuery(afterDelete.Query)["projectId"].ToString()).IsEqualTo(first);
        using var remaining = await client.GetAsync(afterDelete);
        await Assert.That(remaining.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var business = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/create" + scope,
            new() { ["_csrf"] = csrf, ["name"] = "Scoped business" });
        await Assert.That(business.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var afterCreate = new Uri(client.BaseAddress!, business.Headers.Location!);
        var newOrganization = environment.Workspace.Organizations.Values.Single(item => item.Name == "Scoped business");
        await Assert.That(QueryHelpers.ParseQuery(afterCreate.Query)["organizationId"].ToString()).IsEqualTo(newOrganization.Id);
        await Assert.That(QueryHelpers.ParseQuery(afterCreate.Query).ContainsKey("projectId")).IsFalse();
        using var wizard = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/new-organization" + scope,
            new() { ["_csrf"] = csrf, ["step"] = "organization", ["name"] = "Wizard scoped business" });
        await Assert.That(wizard.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var wizardOrganization = environment.Workspace.Organizations.Values.Single(item => item.Name == "Wizard scoped business");
        var wizardTarget = new Uri(client.BaseAddress!, wizard.Headers.Location!);
        await Assert.That(QueryHelpers.ParseQuery(wizardTarget.Query)["organizationId"].ToString()).IsEqualTo(wizardOrganization.Id);
        await Assert.That(QueryHelpers.ParseQuery(wizardTarget.Query).ContainsKey("projectId")).IsFalse();
        var wizardDocument = (await environment.State.GetAsync(AccountService.OrganizationsCollection, wizardOrganization.Id, default))!;
        var paidWizard = AccountService.Read<OrganizationRecord>(wizardDocument);
        paidWizard.PlanId = OrganizationRecord.StarterPlanId;
        paidWizard.PaymentMethodValidated = true;
        paidWizard.BillingStatus = "active";
        await environment.State.PutAsync(AccountService.OrganizationsCollection, paidWizard.Id, JsonSerializer.SerializeToElement(paidWizard),
            TerraScale.DotNet.TerraBase.TerraBasePrecondition.Matches(wizardDocument.Version), default);
        using var wizardProject = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/new-organization" + scope,
            new()
            {
                ["_csrf"] = csrf,
                ["step"] = "project",
                ["organizationID"] = wizardOrganization.Id,
                ["name"] = "Wizard project",
                ["region"] = "GRU",
                ["backupRegion"] = "GRU",
                ["regions"] = "GRU"
            });
        await Assert.That(wizardProject.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(wizardOrganization.Projects.Values.Any(project => project.Name == "Wizard project")).IsTrue();
        await Assert.That(organization.Projects.Values.Any(project => project.Name == "Wizard project")).IsFalse();
        var wizardProjectTarget = new Uri(client.BaseAddress!, wizardProject.Headers.Location!);
        await Assert.That(QueryHelpers.ParseQuery(wizardProjectTarget.Query)["organizationId"].ToString()).IsEqualTo(wizardOrganization.Id);
        await app.StopAsync();
    }

    [Test]
    public async Task LeavingExplicitOrganizationRedirectsToAccessibleWorkspace()
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        var personal = environment.Workspace.Organizations.Values.Single();
        var user = personal.Members.Values.Single();
        var otherOwner = new TerraScaleIdGenerator().NewId();
        var business = new OrganizationRecord
        {
            Id = new TerraScaleIdGenerator().NewId(),
            Name = "Other business",
            Kind = "business",
            OwnerUserId = otherOwner,
            Members = new()
            {
                [user.UserId] = new() { UserId = user.UserId, Email = user.Email, Role = "member" },
                [otherOwner] = new() { UserId = otherOwner, Email = "owner@example.test", Role = "owner" }
            }
        };
        environment.Workspace.Organizations.Add(business.Id, business);
        await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1");
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
        { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var csrf = await HttpJourney.CsrfAsync(client);
        using var left = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/leave?organizationId=" + business.Id,
            new() { ["_csrf"] = csrf });
        await Assert.That(left.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var target = new Uri(client.BaseAddress!, left.Headers.Location!);
        await Assert.That(QueryHelpers.ParseQuery(target.Query)["organizationId"].ToString()).IsEqualTo(personal.Id);
        using var landed = await client.GetAsync(target);
        await Assert.That(landed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await app.StopAsync();
    }

    [Test, Category("Slow")]
    public async Task TwoBrowserTabsKeepTheirProjectInNativeFormsNavigationAndFetch()
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        var organization = environment.Workspace.Organizations.Values.Single();
        var first = organization.Projects.Keys.Single();
        var second = new TerraScaleIdGenerator().NewId();
        organization.Projects.Add(second, new() { Id = second, OrganizationId = organization.Id, Name = "Second" });
        await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1");
        await app.StartAsync();
        var origin = new Uri(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        var cookies = new CookieContainer();
        using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = cookies }) { BaseAddress = origin };
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        await context.AddCookiesAsync(cookies.GetCookies(origin).Cast<System.Net.Cookie>().Select(cookie => new Microsoft.Playwright.Cookie
        { Name = cookie.Name, Value = cookie.Value, Url = origin.ToString() }));
        var tabA = await context.NewPageAsync();
        var tabB = await context.NewPageAsync();
        await tabA.GotoAsync(new Uri(origin, "/api-keys?projectId=" + first + "&create=key").ToString());
        await tabB.GotoAsync(new Uri(origin, "/api-keys?projectId=" + second).ToString());
        await tabA.Locator("#key-name").FillAsync("First tab key");
        await BrowserAudit.SubmitConfirmedAsync(tabA, origin, () => tabA.Locator("#create-key form").GetByRole(AriaRole.Button, new() { Name = "Create key", Exact = true }).ClickAsync());
        await tabA.Locator(".ts-apikeys-secret-value").WaitForAsync();
        await Assert.That(await tabA.EvaluateAsync<string>("async () => (await (await fetch('/api/v1/auth/me')).json()).projectId")).IsEqualTo(first);
        await Assert.That(await tabB.EvaluateAsync<string>("async () => (await (await fetch('/api/v1/auth/me')).json()).projectId")).IsEqualTo(second);
        await tabB.ReloadAsync();
        await Assert.That(await tabB.Locator("#credentials").InnerTextAsync()).DoesNotContain("First tab key");
        await tabA.GotoAsync(new Uri(origin, "/api-keys?projectId=" + first).ToString());
        await tabA.Locator("form[role=search] button[type=submit]").ClickAsync();
        await Assert.That(tabA.Url).Contains("projectId=" + first);
        await Assert.That(await tabA.Locator("#credentials").InnerTextAsync()).Contains("First tab key");
        await Assert.That(await tabA.Locator(".ts-nav-link").Filter(new() { HasText = "Databases" }).First.GetAttributeAsync("href")).Contains("projectId=" + first);
        await app.StopAsync();
    }
}
