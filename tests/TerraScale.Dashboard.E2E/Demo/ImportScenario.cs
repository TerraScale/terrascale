using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace TerraScale.Dashboard.E2E
{
    // A fresh application pair per import journey: the Free plan allows one
    // destination database, and the replicas prove the upload proxy is stateless.
    public sealed class ImportScenario : IAsyncDisposable
    {
        private WebApplication? first;
        private WebApplication? second;
        public Uri Origin { get; private set; } = null!;
        public Uri ReplicaOrigin { get; private set; } = null!;
        // The fake ts-auth registry: Role overrides the signed-in user's organization role, as a role change at ts-auth would.
        internal TestWorkspace Workspace { get; private set; } = null!;

        public static async Task<ImportScenario> StartAsync(string root)
        {
            var scenario = new ImportScenario();
            try
            {
                var environment = new DemoEnvironment();
                await environment.InitializeAsync();
                scenario.Workspace = environment.Workspace;
                scenario.first = environment.CreateTestHost(root, 0, "http://127.0.0.1");
                scenario.second = environment.CreateTestHost(root, 0, "http://127.0.0.1");
                await scenario.first.StartAsync();
                await scenario.second.StartAsync();
                scenario.Origin = Address(scenario.first);
                scenario.ReplicaOrigin = Address(scenario.second);
                return scenario;
            }
            catch { await scenario.DisposeAsync(); throw; }
        }

        // A request with Expect: 100-continue sends no body byte until the dashboard reads the body or answers first.
        public HttpClient CreateClient(bool replica = false)
        {
            return new(new SocketsHttpHandler { SslOptions = new() { RemoteCertificateValidationCallback = (_, certificate, _, errors) => TestHttps.Valid(certificate, errors) }, AllowAutoRedirect = false, CookieContainer = new CookieContainer(), Expect100ContinueTimeout = TimeSpan.FromSeconds(30) })
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
        }
    }
}
