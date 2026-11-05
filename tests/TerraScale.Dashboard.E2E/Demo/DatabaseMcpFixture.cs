using TUnit.Core.Interfaces;

namespace TerraScale.Dashboard.E2E
{
    // These journeys use the same adapters and host pair. Each signs in a fresh
    // account and owns its organization, project, credentials and databases.
    public sealed class DatabaseMcpFixture : IAsyncInitializer, IAsyncDisposable
    {
        public DatabaseSdkScenario Scenario { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Scenario = await DatabaseSdkScenario.StartAsync(DashboardE2EFixture.FindRoot());
        }

        public ValueTask DisposeAsync() => Scenario is null ? ValueTask.CompletedTask : Scenario.DisposeAsync();
    }
}
