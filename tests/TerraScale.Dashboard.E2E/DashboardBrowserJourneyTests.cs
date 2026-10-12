namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
[Category("Slow")]
public sealed class DashboardBrowserJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public Task DatabaseProfileBillingAndDatastarDesktopAndMobile() => BrowserJourneys.RunAsync(fixture, "dashboard-flow.mjs");

    [Test]
    public Task AccessibleAuthPagesAndNativePasswordControls() => BrowserJourneys.RunAsync(fixture, "browser.mjs");
}
