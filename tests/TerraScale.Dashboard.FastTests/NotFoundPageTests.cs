using TerraScale.Dashboard.Testing;

namespace TerraScale.Dashboard.FastTests;

public sealed class NotFoundPageTests
{
    [Test]
    public async Task SignedOutVisitorGetsTheIllustratedPageWith404Status()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();

        var page = await app.GetPageAsync(client, "/no-such-page");

        await Assert.That(page.StatusCode).IsEqualTo(404);
        await Assert.That(page.Contains("404-illustration.webp")).IsTrue();
        await Assert.That(page.Contains("404-number.webp")).IsTrue();
        await Assert.That(page.Contains("Back to dashboard")).IsTrue();
        await Assert.That(page.Contains("https://terrascale.tech/contact")).IsTrue();
    }

    [Test]
    public async Task MissingDatabaseAlsoUsesTheIllustratedPage()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"notfound-{Guid.NewGuid():N}@example.test");

        var page = await app.GetPageAsync(client, "/databases/db_does_not_exist");

        await Assert.That(page.StatusCode).IsEqualTo(404);
        await Assert.That(page.Contains("404-illustration.webp")).IsTrue();
    }

    [Test]
    public async Task ApiAndJsonClientsKeepBare404Responses()
    {
        using var app = new DashboardTestApp();
        using var client = await app.CreateMemberClientAsync($"notfound-api-{Guid.NewGuid():N}@example.test");

        using var api = await client.GetAsync("/api/v1/does-not-exist");
        await Assert.That((int)api.StatusCode).IsEqualTo(404);
        await Assert.That((await api.Content.ReadAsStringAsync()).Contains("404-illustration.webp")).IsFalse();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/no-such-page");
        request.Headers.Accept.ParseAdd("application/json");
        using var json = await client.SendAsync(request);
        await Assert.That((int)json.StatusCode).IsEqualTo(404);
        await Assert.That((await json.Content.ReadAsStringAsync()).Contains("404-illustration.webp")).IsFalse();
    }

    [Test]
    public async Task BrowserNavigationRendersHtmlWhileDatastarAndHeadStayBare()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();

        using var browserRequest = new HttpRequestMessage(HttpMethod.Get, "/no-such-page");
        browserRequest.Headers.Accept.ParseAdd("text/html");
        using var browser = await client.SendAsync(browserRequest);
        await Assert.That((int)browser.StatusCode).IsEqualTo(404);
        await Assert.That((await browser.Content.ReadAsStringAsync()).Contains("404-illustration.webp")).IsTrue();

        using var datastarRequest = new HttpRequestMessage(HttpMethod.Get, "/no-such-page");
        datastarRequest.Headers.Accept.ParseAdd("text/event-stream, text/html, application/json");
        datastarRequest.Headers.TryAddWithoutValidation("Datastar-Request", "true");
        using var datastar = await client.SendAsync(datastarRequest);
        await Assert.That((int)datastar.StatusCode).IsEqualTo(404);
        await Assert.That((await datastar.Content.ReadAsStringAsync()).Contains("404-illustration.webp")).IsFalse();

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/no-such-page"));
        await Assert.That((int)head.StatusCode).IsEqualTo(404);
        await Assert.That((await head.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
    }

    [Test]
    public async Task UnknownAdminRouteStillRequiresAuthentication()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/admin/no-such-page");

        await Assert.That((int)response.StatusCode).IsEqualTo(302);
        await Assert.That(response.Headers.Location?.OriginalString).IsEqualTo("/login");
    }
}
