using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.E2E;

internal sealed class StepUpScenario : IAsyncDisposable
{
    public required DemoEnvironment Environment { get; init; }
    public required WebApplication App { get; init; }
    public required HttpClient Browser { get; init; }
    public required CookieContainer Cookies { get; init; }
    public string Email { get; set; } = "user@example.com";
    private (int Stripe, int Create, int Rotate, int Client, int Delete) effects;
    public TestStepUpGateway Gateway => App.Services.GetRequiredService<TestStepUpGateway>();

    public static async Task<StepUpScenario> StartAsync(string root, bool signIn = true)
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        environment.Auth.RegisterFixturePrincipal(new(new TerraScale.DotNet.TerraScaleIdGenerator().NewId(), "Existing other account", "other@example.com", "test-password"));
        var app = environment.CreateTestHost(root, 0, "http://127.0.0.1", builder => builder.Services.AddSingleton<TimeProvider>(new TestClock()));
        await app.StartAsync();
        var cookies = new CookieContainer();
        var browser = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = cookies }) { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
        if (signIn)
        {
            using var login = await HttpJourney.LoginAsync(browser);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }
        return new() { Environment = environment, App = app, Browser = browser, Cookies = cookies };
    }

    public void Advance(TimeSpan duration) => ((TestClock)App.Services.GetRequiredService<TimeProvider>()).Advance(duration);

    public async Task<Dictionary<string, string>> FormAsync() => new()
    {
        ["_csrf"] = await HttpJourney.CsrfAsync(Browser),
        ["email"] = Email,
        ["name"] = "Proof protected",
        ["expiresInDays"] = "30",
        ["scope"] = "documents:read",
        ["type"] = "confidential",
        ["redirect_uris"] = "https://example.test/callback"
    };

    public async Task<HttpResponseMessage> BeginAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(await FormAsync()) };
        request.Headers.Accept.ParseAdd("text/html");
        return await Browser.SendAsync(request);
    }

    public Task<HttpResponseMessage> ApproveAsync(HttpResponseMessage started) => Browser.GetAsync("/__test/step-up/" + started.Headers.Location!.Segments.Last() + "?approve=yes");

    private (int, int, int, int, int) Effects() => (Environment.BillingPlans.CancellationCalls, Environment.ApiKeys.CreatedKeys,
        Environment.ApiKeys.RotatedKeys, Environment.Authentication.CreatedClients, Environment.Workspace.DeletionCalls);
    public void CaptureEffects() => effects = Effects();

    public async Task AssertNoEffectsAsync()
    {
        await Assert.That(Effects()).IsEqualTo(effects);
        foreach (var collection in new[] { "account_deletions", "dashboard_access_tokens", "auth_credentials", McpOAuth.CodeCollection })
            await Assert.That((await Environment.State.ListAsync(collection, default)).Count).IsEqualTo(0);
        var accounts = await Environment.State.ListAsync(AccountService.AccountsCollection, default);
        await Assert.That(accounts.Count).IsGreaterThan(0);
        await Assert.That(accounts.All(document => !AccountService.IsDeletionFence(document))).IsTrue();
    }

    public async Task SwitchAccountCookieAsync()
    {
        var cookies = new CookieContainer();
        using var other = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = cookies }) { BaseAddress = Browser.BaseAddress };
        using var signedIn = await HttpJourney.LoginAsync(other, "other@example.com", "test-password");
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var session = cookies.GetCookies(Browser.BaseAddress!)["ts-dashboard-session"]!;
        Cookies.Add(Browser.BaseAddress!, new Cookie(session.Name, session.Value, "/"));
    }

    public async Task<string> SelectSharedWorkspaceAsync()
    {
        var org = Environment.Workspace.SeedPersonal(new("outside-owner", "Outside owner", "outside@example.test", "synthetic-fixture-only", null, DateTimeOffset.UtcNow.AddHours(1)));
        lock (Environment.Workspace.Gate)
            Environment.Workspace.Organizations[org.Id].Members["test-user"] = new() { UserId = "test-user", Email = Email, Role = "member", CreatedAt = org.CreatedAt };
        using var selected = await HttpJourney.PostFormAsync(Browser, "/api/v1/dashboard/switch-organization", new()
        { ["_csrf"] = await HttpJourney.CsrfAsync(Browser), ["organizationId"] = org.Id, ["returnUrl"] = "/profile" });
        await Assert.That(selected.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        return org.Id;
    }

    public async Task<Uri> PatOidcCallbackAsync()
    {
        using var started = await BeginAsync("/api/v1/dashboard/profile/access-tokens");
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using var approved = await ApproveAsync(started);
        await Assert.That(approved.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using var oidc = await Browser.GetAsync("/auth/step-up/callback");
        await Assert.That(oidc.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using var answered = await Browser.GetAsync(oidc.Headers.Location!.OriginalString + "&email=user%40example.com&password=test-password");
        await Assert.That(answered.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        return answered.Headers.Location!;
    }

    // Stop between the hosted confirmation callback and the independent OIDC callback.
    public async Task<Uri> McpOidcCallbackAsync(string failure)
    {
        const string redirect = "http://127.0.0.1:33418/callback";
        using var registered = await Browser.PostAsJsonAsync("/oauth/register", new { redirect_uris = new[] { redirect } });
        await Assert.That(registered.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var client = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var form = new Dictionary<string, string>
        {
            ["_csrf"] = await HttpJourney.CsrfAsync(Browser),
            ["decision"] = "allow",
            ["response_type"] = "code",
            ["client_id"] = client,
            ["redirect_uri"] = redirect,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["state"] = "step-up-mcp",
            ["resource"] = Browser.BaseAddress!.GetLeftPart(UriPartial.Authority) + "/mcp"
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/authorize") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        using var started = await Browser.SendAsync(request);
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        if (failure != "unapproved")
        {
            using var approved = await ApproveAsync(started);
            await Assert.That(approved.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        }
        using var oidc = await Browser.GetAsync("/auth/step-up/callback");
        await Assert.That(oidc.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        if (failure == "switch") await SwitchAccountCookieAsync();
        using var answered = await Browser.GetAsync(oidc.Headers.Location!.OriginalString + "&email=user%40example.com&password=test-password");
        await Assert.That(answered.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        return answered.Headers.Location!;
    }

    public async ValueTask DisposeAsync()
    {
        Browser.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
