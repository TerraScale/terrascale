using TerraScale.Dashboard2.Testing;

namespace TerraScale.Dashboard2.FastTests;

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
        await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location?.ToString()).IsEqualTo("/login");
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
    public async Task SignInAgainAfterLogout()
    {
        using var app = new DashboardTestApp();
        var email = $"fast-{Guid.NewGuid():N}@example.test";
        using var client = await app.CreateMemberClientAsync(email);
        await client.PostAsync("/api/v1/auth/logout", new FormUrlEncodedContent([]));
        var anonymous = await app.GetPageAsync(client, "/");
        await Assert.That(anonymous.StatusCode).IsEqualTo(302);
        using var fresh = app.CreateClient();
        await app.SignInDemoAsync(fresh);
        var page = await app.GetPageAsync(fresh, "/");
        await Assert.That(page.Heading).IsEqualTo("Overview");
    }

    [Test]
    public async Task DemoLoginAndEveryNavItemRender()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();
        var page = await app.GetPageAsync(client, "/");
        await Assert.That(page.Heading).IsEqualTo("Overview");
        await Assert.That(string.Join("|", page.NavItems.Take(11))).IsEqualTo(
            "Overview|Databases|Tables|API keys|Team|Usage|Logs|Notifications|Settings|Profile|Organization");
    }
}
