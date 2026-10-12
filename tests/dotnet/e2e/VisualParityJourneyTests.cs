using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
public sealed class VisualParityJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task EveryGoReferenceStateHasIdenticalDesktopAndMobileRendering()
    {
        var reference = Path.Combine(fixture.Root, "test-results", "parity", "reference.json");
        await Assert.That(File.Exists(reference)).IsTrue()
            .Because("Capture the original Go application with make dotnet-parity-reference before comparing its actual rendering.");
        await using var scenario = await GoReferenceScenario.StartAsync(fixture.Root, builder =>
        {
            // The source Go reference host has no analytics producer. Match
            // that dependency state while exercising the actual page/service.
            builder.Services.Replace(ServiceDescriptor.Singleton<IAnalyticsBackend>(new ReferenceAnalytics()));
            builder.Services.Replace(ServiceDescriptor.Singleton<IUsageMeasurements>(new ReferenceUsageMeasurements()));
            builder.Services.Replace(ServiceDescriptor.Singleton<IUsageReplication>(new ReferenceUsageReplication()));
        });
        await BrowserJourneys.RunAsync(fixture.Root, "parity.mjs", scenario.Origin);
    }

    private sealed class ReferenceAnalytics : IAnalyticsBackend
    {
        public Task<AnalyticsBatch> ReadAsync(AnalyticsSelection selection, CancellationToken cancellationToken)
            => Task.FromResult(new AnalyticsBatch(AnalyticsSourceState.Unavailable, []));
    }
}
