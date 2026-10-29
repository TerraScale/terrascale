using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Home;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.E2E
{
    // A separate application pair prevents populated reference data, source
    // identities and illustrative workload from changing other E2E journeys.
    public sealed class ReferenceScenario : IAsyncDisposable
    {
        private WebApplication? first;
        private WebApplication? second;
        private ReferenceStripeTransport? transport;
        public Uri Origin { get; private set; } = null!;
        public Uri ReplicaOrigin { get; private set; } = null!;
        public ReferencePersonas Personas { get; private set; } = null!;

        public static async Task<ReferenceScenario> StartAsync(string root, Action<WebApplicationBuilder>? configure = null)
        {
            var scenario = new ReferenceScenario();
            try { await scenario.InitializeAsync(root, configure); return scenario; }
            catch { await scenario.DisposeAsync(); throw; }
        }

        private async Task InitializeAsync(string root, Action<WebApplicationBuilder>? configure)
        {
            var environment = new DemoEnvironment();
            Personas = await environment.InitializeReferenceAsync(root);
            var databaseFacts = await ReferenceDatabaseFacts.ReadAsync(root, Personas);
            void Configure(WebApplicationBuilder builder)
            {
                var original = (IHttpClientFactory)builder.Services.Last(descriptor => descriptor.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                transport ??= new(original);
                builder.Services.Replace(ServiceDescriptor.Singleton<IHomeMeasurements>(new ReferenceMeasurements()));
                builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new ReferenceHttpFactory(transport)));
                var originalDatabases = (IDatabaseBackend)builder.Services.Last(descriptor => descriptor.ServiceType == typeof(IDatabaseBackend)).ImplementationInstance!;
                builder.Services.Replace(ServiceDescriptor.Singleton<IDatabaseBackend>(new ReferenceDatabaseBackend(originalDatabases, databaseFacts)));
                builder.Services.Replace(ServiceDescriptor.Singleton<IBackupBackend>(databaseFacts));
                builder.Services.Replace(ServiceDescriptor.Singleton<IUsageReplication>(new ReferenceUsageReplication()));
                configure?.Invoke(builder);
            }
            first = environment.CreateTestHost(root, 0, "http://127.0.0.1", Configure);
            second = environment.CreateTestHost(root, 0, "http://127.0.0.1", Configure);
            await first.StartAsync();
            await second.StartAsync();
            Origin = Address(first);
            ReplicaOrigin = Address(second);
            using var client = CreateClient();
            using var ready = await client.GetAsync("/readyz");
            ready.EnsureSuccessStatusCode();
        }

        public HttpClient CreateClient(bool replica = false)
        {
            return new(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = TestHttps.Validate,
                AllowAutoRedirect = false,
                CookieContainer = new CookieContainer()
            })
            { BaseAddress = replica ? ReplicaOrigin : Origin, Timeout = TimeSpan.FromSeconds(30) };
        }

        private static Uri Address(WebApplication application)
        {
            return new(application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        }

        public async ValueTask DisposeAsync()
        {
            if (second is not null) { await second.StopAsync(); await second.DisposeAsync(); second = null; }
            if (first is not null) { await first.StopAsync(); await first.DisposeAsync(); first = null; }
            transport?.Dispose();
            transport = null;
        }
    }
}
