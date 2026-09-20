namespace TerraScale.Auth.Tests;

public sealed class BrowserFixtureTests
{
    [Test]
    public async Task Disposing_a_page_releases_its_browser_context()
    {
        var page = await BrowserFixture.NewPageAsync();
        var context = page.Context;
        var browser = context.Browser!;
        await page.DisposeAsync();
        await Assert.That(browser.Contexts.Contains(context)).IsFalse();
    }
}
