namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
[Explicit]
[Category("Performance")]
public sealed class PerformanceJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task NativeBrowserMeasuresColdAndWarmDesktopAndThrottledMobileJourneys()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        await BrowserJourneys.RunAsync(fixture.Root, "performance-browser-flow.mjs", scenario.Origin);
    }
}
