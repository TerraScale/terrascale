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

    public sealed class SharedDemoEnvironment : IAsyncInitializer, IDisposable
    {
        public string Root { get; } = DashboardE2EFixture.FindRoot();

        public DemoEnvironment Environment { get; } = new();

        public Task InitializeAsync()
        {
            return Environment.InitializeAsync();
        }

        public void Dispose()
        {
            Environment.Dispose();
        }
    }
}
