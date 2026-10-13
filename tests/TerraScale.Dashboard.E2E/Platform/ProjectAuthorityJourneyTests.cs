using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E.Platform;

public sealed class ProjectAuthorityJourneyTests
{
    [Test]
    public async Task ScopedFixtureObservesExactScopesAndRefusesRemovedMembersViewerWritesAndForeignProjects()
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        TestAuth auth = null!;
        await using var app = environment.CreateHost(new DashboardE2EFixture().Root, 0, "http://127.0.0.1", builder =>
            auth = (TestAuth)builder.Services.Last(item => item.ServiceType == typeof(IAuthGateway)).ImplementationInstance!);
        app.MapGet("/__test/scopes", async (HttpContext http, DashboardSessions sessions, IProjectAccessTokens tokens) =>
        {
            var session = await sessions.ReadAsync(http);
            if (session is null) return Results.Unauthorized();
            try
            {
                var token = await tokens.GetAsync(session, http.Request.Query["organization"].ToString(),
                    http.Request.Query["project"].ToString(), http.Request.Query["scope"].ToString().Split(' '), http.RequestAborted);
                return Results.Json(new { scopes = auth.ResolveProjectToken(token)!.Value.Scopes });
            }
            catch (HttpRequestException error) when (error.StatusCode is not null)
            { return Results.StatusCode((int)error.StatusCode); }
        });
        await app.StartAsync();
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new() })
        { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
        using var login = await HttpJourney.LoginAsync(browser);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var organization = environment.Workspace.Organizations.Values.Single();
        var project = organization.Projects.Keys.Single();
        var target = "/__test/scopes?organization=" + organization.Id + "&project=" + project + "&scope=";
        foreach (var role in new[] { "owner", "admin", "member", "viewer" })
        {
            environment.Workspace.Role = role;
            using var read = await browser.GetAsync(target + "documents%3Aread");
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            await Assert.That(body.RootElement.GetProperty("scopes").EnumerateArray().Select(item => item.GetString()).ToArray())
                .IsEquivalentTo(new[] { "documents:read" });
            using var write = await browser.GetAsync(target + "databases%3Awrite%20documents%3Awrite");
            await Assert.That(write.StatusCode).IsEqualTo(role == "viewer" ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
            var observed = auth.ProjectExchanges.Last();
            await Assert.That(observed.RequestedScopes).IsEquivalentTo(new[] { "databases:write", "documents:write" });
            await Assert.That(observed.GrantedScopes).IsEquivalentTo(role == "viewer" ? [] : new[] { "databases:write", "documents:write" });
        }
        environment.Workspace.Role = "";
        using var removed = await browser.GetAsync(target + "documents%3Aread");
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        environment.Workspace.Role = "owner";
        using var foreign = await browser.GetAsync(target.Replace(project, new TerraScaleIdGenerator().NewId(), StringComparison.Ordinal) + "documents%3Aread");
        await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(auth.ProjectExchanges.Last().GrantedScopes.Count).IsEqualTo(0);
        await app.StopAsync();
    }
}
