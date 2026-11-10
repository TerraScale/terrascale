using TUnit.Core.Interfaces;

namespace TerraScale.Dashboard.E2E
{
    public sealed class SharedDatabaseSdk : IAsyncInitializer, IAsyncDisposable
    {
        public string Root { get; } = DashboardE2EFixture.FindRoot();

        public DatabaseSdkScenario Scenario { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Scenario = await DatabaseSdkScenario.StartAsync(Root);
        }

        public async ValueTask DisposeAsync()
        {
            if (Scenario is not null)
            {
                await Scenario.DisposeAsync();
            }
        }
    }
}
