using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Home;

[ClassDataSource<DashboardRootFixture>]
[Category("Slow")]
public sealed class AccountProjectDatabaseJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task AccountOrgProjectDatabaseSqlAndDashboardPages()
    {
        IHttpClientFactory? platform = null;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, application =>
        {
            platform = (IHttpClientFactory)application.Services.Last(item => item.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
        });
        using var client = scenario.CreateClient();
        var account = await DatabaseJourney.NativeWorkspaceAsync(client);
        using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        var organizationId = identity.RootElement.GetProperty("organizationId").GetString()!;
        var projectId = identity.RootElement.GetProperty("projectId").GetString()!;
        await Assert.That(organizationId.Length).IsEqualTo(26);
        await Assert.That(projectId.Length).IsEqualTo(26);
        const string databaseName = "journey-db";
        var databaseId = await HttpJourney.CreateDatabaseAsync(client, databaseName);
        await Assert.That(databaseId.Length).IsEqualTo(26);
        var secret = await CreateKeyAsync(client);
        var token = await ExchangeAsync(platform!, secret, projectId);
        using var sqlHttp = new HttpClient(new H2cTestTransport()) { Timeout = TimeSpan.FromSeconds(30) };
        var database = new TerraBaseSqlClient(sqlHttp, new TerraBaseSqlClientOptions
        {
            BaseAddress = scenario.ProviderOrigin,
            AccessTokenProvider = _ => ValueTask.FromResult(token),
        }).Database(projectId, databaseName);
        await database.ExecuteAsync("CREATE TABLE people (name TEXT NOT NULL)");
        await database.ExecuteAsync("INSERT INTO people (name) VALUES ('Ada')");
        var rows = await database.QueryAsync("SELECT name FROM people");
        await Assert.That(rows[0].GetString("name")).IsEqualTo("Ada");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        var failures = new List<string>();
        page.PageError += (_, error) => failures.Add(error);
        page.Response += (_, response) =>
        {
            if (response.Status >= 500)
            {
                failures.Add(response.Status + " " + response.Url);
            }
        };
        await BrowserAudit.SignInAsync(page, scenario.Origin, "/", account.Email, account.Password);
        await OpenAsync("/");
        await OpenAsync("/databases/" + databaseId);
        await OpenAsync("/api-keys");
        await OpenAsync("/organization?tab=billing");
        await OpenAsync("/databases/" + databaseId + "/settings");
        await Assert.That(failures).IsEmpty();

        async Task OpenAsync(string path)
        {
            var response = await page.GotoAsync(new Uri(scenario.Origin, path).ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var body = await page.ContentAsync();
            if (response is null || response.Status >= 500 || body.Contains("unhandled exception", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(path + " " + (response is null ? "no-response" : response.Status.ToString(CultureInfo.InvariantCulture)));
            }
        }
    }

    private static async Task<string> CreateKeyAsync(HttpClient client)
    {
        using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/key", new()
        {
            ["_csrf"] = await HttpJourney.CsrfAsync(client, "/api-keys"),
            ["name"] = "Journey SQL",
            ["scopes"] = "sql:read,sql:write",
            ["expiresInDays"] = "0",
        });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("secret").GetString()!;
    }

    private static async Task<string> ExchangeAsync(IHttpClientFactory platform, string secret, string projectId)
    {
        using var auth = platform.CreateClient("journey");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.terrascale.tech/terrascale/oauth/token")
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("project", projectId)]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(secret[..26] + ":" + secret)));
        using var response = await auth.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("access_token").GetString()!;
    }
}
