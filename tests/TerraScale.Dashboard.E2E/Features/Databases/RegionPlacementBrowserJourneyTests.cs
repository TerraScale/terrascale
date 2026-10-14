using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class RegionPlacementBrowserJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task ProjectMapControlsPersistNativePlacementAcrossReplicas(int width, int height)
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await browserContext.NewPageAsync();
        page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);

        var persona = scenario.Personas.Seeded;
        using var replica = scenario.CreateClient(replica: true);
        using var replicaLogin = await HttpJourney.LoginAsync(replica, persona.Email, persona.Password);
        await Assert.That(replicaLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var secret = await HttpJourney.CreatePatAsync(replica, "TUnit project map " + width, persona.Password);

        var initialContext = await ContextAsync();
        var organizationId = initialContext.GetProperty("selection").GetProperty("organizationId").GetString()!;
        var projectName = "Map placement " + width + " " + Guid.NewGuid().ToString("N")[..8];

        var login = await page.GotoAsync(new Uri(scenario.Origin, "/login").ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assert.That(login!.Status).IsEqualTo(200);
        await page.Locator("#email").FillAsync(persona.Email);
        await page.Locator("#password").FillAsync(persona.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(scenario.Origin.ToString());
        await NavigateAsync("/organization?tab=projects");

        await page.GetByRole(AriaRole.Button, new() { Name = "Create project", Exact = true }).ClickAsync();
        var dialog = page.Locator("#create-project[open]");
        await dialog.WaitForAsync();
        await page.Locator("#project-name").FillAsync(projectName);

        var allowedMap = dialog.Locator("[data-select-id='project-regions']");
        var allowedSelect = dialog.Locator("#project-regions");
        await allowedMap.GetByRole(AriaRole.Button, new() { Name = "San Francisco (SFO)", Exact = true }).WaitForAsync();
        await allowedMap.GetByRole(AriaRole.Button, new() { Name = "São Paulo (GRU)", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => [...document.querySelector('#project-regions').selectedOptions].map(option => option.value).join(',') === 'SFO'");
        await Assert.That((await SelectedAsync(allowedSelect)).SequenceEqual(new[] { "SFO" })).IsTrue();
        await Assert.That(await allowedMap.Locator("button[data-region='GRU']").GetAttributeAsync("aria-pressed")).IsEqualTo("false");
        await Assert.That(await allowedMap.Locator("button[data-region='SFO']").GetAttributeAsync("aria-pressed")).IsEqualTo("true");

        var primaryMap = dialog.Locator("[data-select-id='project-region']");
        var primarySelect = dialog.Locator("#project-region");
        await primaryMap.GetByRole(AriaRole.Button, new() { Name = "San Francisco (SFO)", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('#project-region').value === 'SFO'");
        await Assert.That(await primarySelect.InputValueAsync()).IsEqualTo("SFO");
        await Assert.That(await primaryMap.Locator("button[data-region='SFO']").GetAttributeAsync("aria-pressed")).IsEqualTo("true");

        var backupMap = dialog.Locator("[data-select-id='project-backup']");
        var backupSelect = dialog.Locator("#project-backup");
        await backupMap.GetByRole(AriaRole.Button, new() { Name = "San Francisco (SFO)", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('#project-backup').value === 'SFO'");
        await Assert.That(await backupSelect.InputValueAsync()).IsEqualTo("SFO");
        await Assert.That(await backupMap.Locator("button[data-region='SFO']").GetAttributeAsync("aria-pressed")).IsEqualTo("true");

        var submit = dialog.GetByRole(AriaRole.Button, new() { Name = "Create project", Exact = true });
        var created = await page.RunAndWaitForResponseAsync(
            () => submit.ClickAsync(),
            response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == "/api/v1/dashboard/organization/projects");
        await Assert.That(created.Status).IsEqualTo(303);
        await page.WaitForURLAsync(url => new Uri(url).PathAndQuery == "/organization?tab=projects");
        await NavigateAsync("/organization?tab=projects");

        var row = page.Locator("#projects tbody tr").Filter(new() { HasText = projectName });
        await row.WaitForAsync();
        await Assert.That(await row.CountAsync()).IsEqualTo(1);
        await Assert.That(await row.Locator("td").Nth(3).InnerTextAsync()).Contains("sfo");
        var projectId = (await row.Locator(".ts-org-project-name .ts-mono").InnerTextAsync()).Trim();
        await row.GetByRole(AriaRole.Button, new() { Name = "Rename", Exact = true }).ClickAsync();

        var edit = page.Locator($"#rename-project-{projectId}[open]");
        await edit.WaitForAsync();
        await Assert.That((await SelectedAsync(edit.Locator($"#project-regions-{projectId}"))).SequenceEqual(new[] { "SFO" })).IsTrue();
        await Assert.That(await edit.Locator($"#project-region-{projectId}").InputValueAsync()).IsEqualTo("SFO");
        await Assert.That(await edit.Locator($"#project-backup-{projectId}").InputValueAsync()).IsEqualTo("SFO");
        foreach (var field in new[] { "project-regions", "project-region", "project-backup" })
        {
            var map = edit.Locator($"[data-select-id='{field}-{projectId}']");
            await Assert.That(await map.Locator("button[data-region='SFO']").GetAttributeAsync("aria-pressed")).IsEqualTo("true");
            if (field == "project-regions")
                await Assert.That(await map.Locator("button[data-region='GRU']").GetAttributeAsync("aria-pressed")).IsEqualTo("false");
        }

        using var httpContextResponse = await replica.GetAsync("/api/v1/dashboard/context");
        await Assert.That(httpContextResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var httpContextDocument = JsonDocument.Parse(await httpContextResponse.Content.ReadAsStringAsync());
        var httpOrganization = httpContextDocument.RootElement.GetProperty("organizations").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == organizationId);
        var httpProject = httpOrganization.GetProperty("projects").GetProperty(projectId);
        await Assert.That(httpProject.GetProperty("region").GetString()).IsEqualTo("SFO");
        await Assert.That(httpProject.GetProperty("backupRegion").GetString()).IsEqualTo("SFO");
        await Assert.That(string.Join(',', httpProject.GetProperty("regions").EnumerateArray().Select(item => item.GetString())))
            .IsEqualTo("SFO");

        var persisted = await ContextAsync();
        var organization = persisted.GetProperty("organizations").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == organizationId);
        var project = organization.GetProperty("projects").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == projectId);
        await Assert.That(project.GetProperty("region").GetString()).IsEqualTo("SFO");
        await Assert.That(project.GetProperty("backupRegion").GetString()).IsEqualTo("SFO");
        await Assert.That(string.Join(',', project.GetProperty("regions").EnumerateArray().Select(item => item.GetString())))
            .IsEqualTo("SFO");
        await Assert.That(errors.ToArray()).IsEmpty();

        async Task NavigateAsync(string path)
        {
            var response = await page.GotoAsync(new Uri(scenario.Origin, path).ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assert.That(response!.Status).IsEqualTo(200);
        }

        async Task<JsonElement> ContextAsync()
        {
            var rpc = await HttpJourney.McpAsync(replica, secret, "tools/call", new { name = "get_context", arguments = new { } });
            var result = rpc.GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse();
            return result.GetProperty("structuredContent");
        }

        static Task<string[]> SelectedAsync(ILocator select) => select.EvaluateAsync<string[]>("element => [...element.selectedOptions].map(option => option.value)");
    }

    [Test]
    [Arguments(1440, 900)]
    [Arguments(390, 844)]
    public async Task ProjectPlacementNativeSelectsWorkFromKeyboardAcrossReplicas(int width, int height)
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await browserContext.NewPageAsync();
        page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);

        var persona = scenario.Personas.Seeded;
        using var replica = scenario.CreateClient(replica: true);
        using var replicaLogin = await HttpJourney.LoginAsync(replica, persona.Email, persona.Password);
        await Assert.That(replicaLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var secret = await HttpJourney.CreatePatAsync(replica, "TUnit keyboard project map " + width, persona.Password);

        var initialContext = await ContextAsync();
        var organizationId = initialContext.GetProperty("selection").GetProperty("organizationId").GetString()!;
        var projectName = "Keyboard map placement " + width + " " + Guid.NewGuid().ToString("N")[..8];

        var login = await page.GotoAsync(new Uri(scenario.Origin, "/login").ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assert.That(login!.Status).IsEqualTo(200);
        await page.Locator("#email").FillAsync(persona.Email);
        await page.Locator("#password").FillAsync(persona.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(scenario.Origin.ToString());
        await NavigateAsync("/organization?tab=projects");

        await page.GetByRole(AriaRole.Button, new() { Name = "Create project", Exact = true }).ClickAsync();
        var dialog = page.Locator("#create-project[open]");
        await dialog.WaitForAsync();
        await page.Locator("#project-name").FillAsync(projectName);

        var primarySelect = dialog.Locator("#project-region");
        var backupSelect = dialog.Locator("#project-backup");
        var primaryRegion = await SelectNextRegionWithKeyboardAsync(page, primarySelect);
        var backupRegion = await SelectNextRegionWithKeyboardAsync(page, backupSelect);
        await AssertMapSelectionAsync(dialog.Locator("[data-select-id='project-region']"), primaryRegion);
        await AssertMapSelectionAsync(dialog.Locator("[data-select-id='project-backup']"), backupRegion);

        var submit = dialog.GetByRole(AriaRole.Button, new() { Name = "Create project", Exact = true });
        var created = await page.RunAndWaitForResponseAsync(
            () => submit.ClickAsync(),
            response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == "/api/v1/dashboard/organization/projects");
        await Assert.That(created.Status).IsEqualTo(303);
        await page.WaitForURLAsync(url => new Uri(url).PathAndQuery == "/organization?tab=projects");
        await NavigateAsync("/organization?tab=projects");

        var row = page.Locator("#projects tbody tr").Filter(new() { HasText = projectName });
        await row.WaitForAsync();
        var projectId = (await row.Locator(".ts-org-project-name .ts-mono").InnerTextAsync()).Trim();
        using var httpContextResponse = await replica.GetAsync("/api/v1/dashboard/context");
        await Assert.That(httpContextResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var httpContextDocument = JsonDocument.Parse(await httpContextResponse.Content.ReadAsStringAsync());
        var httpOrganization = httpContextDocument.RootElement.GetProperty("organizations").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == organizationId);
        var httpProject = httpOrganization.GetProperty("projects").GetProperty(projectId);
        await Assert.That(httpProject.GetProperty("region").GetString()).IsEqualTo(primaryRegion);
        await Assert.That(httpProject.GetProperty("backupRegion").GetString()).IsEqualTo(backupRegion);

        var persistedContext = await ContextAsync();
        var mcpOrganization = persistedContext.GetProperty("organizations").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == organizationId);
        var mcpProject = mcpOrganization.GetProperty("projects").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == projectId);
        await Assert.That(mcpProject.GetProperty("region").GetString()).IsEqualTo(primaryRegion);
        await Assert.That(mcpProject.GetProperty("backupRegion").GetString()).IsEqualTo(backupRegion);
        await Assert.That(errors.ToArray()).IsEmpty();

        async Task NavigateAsync(string path)
        {
            var response = await page.GotoAsync(new Uri(scenario.Origin, path).ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assert.That(response!.Status).IsEqualTo(200);
        }

        async Task<JsonElement> ContextAsync()
        {
            var rpc = await HttpJourney.McpAsync(replica, secret, "tools/call", new { name = "get_context", arguments = new { } });
            var result = rpc.GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse();
            return result.GetProperty("structuredContent");
        }
    }

    private static async Task<string> SelectNextRegionWithKeyboardAsync(IPage page, ILocator select)
    {
        var options = await select.Locator("option").EvaluateAllAsync<string[]>("elements => elements.map(option => option.value)");
        await Assert.That(options.Length).IsGreaterThan(1);
        var current = await select.InputValueAsync();
        var currentIndex = Array.IndexOf(options, current);
        await Assert.That(currentIndex).IsGreaterThanOrEqualTo(0);
        var target = options[(currentIndex + 1) % options.Length];

        await select.FocusAsync();
        await page.Keyboard.PressAsync("Home");
        for (var index = 0; index < options.Length; index++)
        {
            if (await select.InputValueAsync() == target) break;
            await page.Keyboard.PressAsync("ArrowDown");
        }
        await Assert.That(await select.InputValueAsync()).IsEqualTo(target);
        await page.Keyboard.PressAsync("Tab");
        return target;
    }

    private static async Task AssertMapSelectionAsync(ILocator map, string selected)
    {
        await Assert.That(await map.Locator($"button[data-region='{selected}']").GetAttributeAsync("aria-pressed")).IsEqualTo("true");
        var unselected = map.Locator($"button[data-region]:not([data-region='{selected}'])");
        for (var index = 0; index < await unselected.CountAsync(); index++)
            await Assert.That(await unselected.Nth(index).GetAttributeAsync("aria-pressed")).IsEqualTo("false");
    }
}
