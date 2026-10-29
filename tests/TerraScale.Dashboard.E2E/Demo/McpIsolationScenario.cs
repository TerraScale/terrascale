using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Home;

namespace TerraScale.Dashboard.E2E
{
    // Each host owns its state, identity, and measurement dependency. Only production
    // MCP tool metadata is shared by the process.
    internal sealed class McpIsolationScenario : IAsyncDisposable
    {
        private WebApplication? app;
        public HttpClient Client { get; private set; } = null!;

        public static async Task<McpIsolationScenario> StartAsync(string root, string measurement)
        {
            var scenario = new McpIsolationScenario();
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            scenario.app = environment.CreateTestHost(root, 0, "http://127.0.0.1", builder =>
                builder.Services.Replace(ServiceDescriptor.Singleton<IHomeMeasurements>(new Measurements(measurement))));
            try
            {
                await scenario.app.StartAsync();
                scenario.Client = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false })
                {
                    BaseAddress = new(scenario.app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)),
                    Timeout = TimeSpan.FromSeconds(30)
                };
                return scenario;
            }
            catch { await scenario.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); app = null; }
        }

        private sealed class Measurements(string marker) : IHomeMeasurements
        {
            public Task<HomeMeasurement> ReadAsync(AccountContext context, string range, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new HomeMeasurement { ActivityMeasured = true, ActivityNote = marker });
            }
        }
    }
}
