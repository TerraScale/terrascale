using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Testing;

namespace TerraScale.Dashboard.FastTests;

/// <summary>The user-blocking critical paths: if any of these fail, a user
/// cannot reach or use the dashboard. Keep this suite seconds-fast.</summary>
public sealed class CriticalPathsTests
{
    [Test]
    public async Task AnonymousRootRedirectsToLogin()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location?.ToString()).IsEqualTo("/login");
    }

    [Test]
    public async Task AnonymousMemberPageRedirectsToLogin()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/databases");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location?.ToString()).IsEqualTo("/login");
    }

    [Test]
    public async Task PublicPagesRenderWithoutASession()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        await AssertPage(client, app, "/login", "Sign in");
        await AssertPage(client, app, "/reset-password", "Reset password");
        await AssertPage(client, app, "/onboarding", "Activate your TerraScale account");
        var checkout = await DashboardTestApp.PostFormAsync(client, "/onboarding/step", new Dictionary<string, string> { ["step"] = "payment-method" });
        await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(checkout.Headers.Location!.ToString()).StartsWith("/checkout/cs_local_");
        await AssertPage(client, app, checkout.Headers.Location!.ToString(), "TerraScale local payment setup");
    }

    [Test]
    public async Task RegistrationOnboardingAndDashboardWork()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"fast-{Guid.NewGuid():N}@example.test");
        var page = await app.GetPageAsync(client, "/");
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo("Overview");
    }

    [Test]
    public async Task WrongPasswordIsRejected()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();
        using var response = await DashboardTestApp.PostFormAsync(client, "/api/v1/auth/password/login", new Dictionary<string, string>
        {
            ["email"] = "mario@terrascale.tech",
            ["password"] = "not-the-password",
        });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location?.ToString()).StartsWith("/login?error=");
    }

    [Test]
    public async Task LoginAgainAfterLogoutWithSameAccount()
    {
        using var app = new DashboardTestApp();
        var email = $"fast-{Guid.NewGuid():N}@example.test";
        const string password = "Member-tests-123!";
        using var client = await app.CreateMemberClientAsync(email, password);
        await client.PostAsync("/api/v1/auth/logout", new FormUrlEncodedContent([]));

        var anonymous = await app.GetPageAsync(client, "/");
        await Assert.That(anonymous.StatusCode).IsEqualTo(302);

        using var fresh = app.CreateClient();
        using var login = await DashboardTestApp.PostFormAsync(fresh, "/api/v1/auth/password/login", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
        });
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(login.Headers.Location?.ToString()).IsEqualTo("/");

        var page = await app.GetPageAsync(fresh, "/");
        await Assert.That(page.Heading).IsEqualTo("Overview");
    }

    [Test]
    public async Task SeededTestAccountSurvivesDemoRestart()
    {
        var settings = new Dictionary<string, string> { ["TS_DASHBOARD_SEED_TEST_PASSWORD"] = "seed-password-123" };
        for (var restart = 0; restart < 2; restart++)
        {
            using var app = new DashboardTestApp(settings);
            using var client = app.CreateClient();
            using var login = await DashboardTestApp.PostFormAsync(client, "/api/v1/auth/password/login", new Dictionary<string, string>
            {
                ["email"] = "filippo@teste.com",
                ["password"] = "seed-password-123",
            });
            await Assert.That(login.Headers.Location?.ToString()).IsEqualTo("/");
            var page = await app.GetPageAsync(client, "/usage");
            await Assert.That(page.StatusCode).IsEqualTo(200);
            await Assert.That(page.Html).Contains("Starter");
            var account = app.Services.GetRequiredService<DashboardStore>().FindAccount("filippo@teste.com")!;
            await Assert.That(account.ActiveOrganization.PlanKey).IsEqualTo("starter");
            await Assert.That(account.ActiveOrganization.DemoCardLast4).IsEqualTo("4242");
            await Assert.That(account.ActiveOrganization.DemoCardExpiry).IsEqualTo("06/2030");
        }
    }

    [Test]
    public async Task ProductionDemoSeedsTestAccountWithoutActionSecret()
    {
        var settings = new Dictionary<string, string>
        {
            ["TS_DASHBOARD_PRODUCTION_DEMO"] = "1",
            ["TS_DASHBOARD_SEED_TEST_PASSWORD"] = "other-password-123",
        };
        for (var restart = 0; restart < 2; restart++)
        {
            using var app = new DashboardTestApp(settings);
            using var client = app.CreateClient();
            using var denied = await DashboardTestApp.PostFormAsync(client, "/api/v1/auth/password/login", new Dictionary<string, string>
            {
                ["email"] = "filippo@teste.com",
                ["password"] = "wrong-password",
            });
            await Assert.That(denied.Headers.Location?.ToString()).StartsWith("/login?error=");
            using var login = await DashboardTestApp.PostFormAsync(client, "/api/v1/auth/password/login", new Dictionary<string, string>
            {
                ["email"] = "filippo@teste.com",
                ["password"] = new string('a', 12),
            });
            await Assert.That(login.Headers.Location?.ToString()).IsEqualTo("/");
            var page = await app.GetPageAsync(client, "/usage");
            await Assert.That(page.StatusCode).IsEqualTo(200);
            await Assert.That(page.Html).Contains("Starter");
        }
    }

    private static async Task AssertPage(HttpClient client, DashboardTestApp app, string path, string heading)
    {
        var page = await app.GetPageAsync(client, path);
        await Assert.That(page.StatusCode).IsEqualTo(200);
        await Assert.That(page.Heading).IsEqualTo(heading);
    }
}
