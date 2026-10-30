using System.Net;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Rpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraScale.Dashboard.E2E
{
    // Real MagicOnion services execute the existing REST protocol fixtures in process.
    // Each host owns an ephemeral HTTP/2 listener and disposes its channels with the fixture.
    internal sealed class TestRestRpcHost : IAsyncDisposable
    {
        private readonly WebApplication application;
        public TerraBaseRpcClient Client { get; }
        public Uri Origin { get; }

        private TestRestRpcHost(WebApplication application, Uri origin)
        {
            this.application = application;
            Origin = origin;
            Client = new(new TestIdentity(), origin, "gru", null, application.Services.GetRequiredService<IMeterFactory>());
        }

        // Points the dashboard's TerraBase client at the registered host's MagicOnion services.
        public static void UseRegisteredHost(IServiceCollection services)
        {
            services.Replace(ServiceDescriptor.Singleton<TerraBaseRpcClient>(provider => provider.GetRequiredService<TestRestRpcHost>().Client));
        }

        public static async Task<TestRestRpcHost> StartAsync(IHttpClientFactory factory, string origin = "https://api.terrascale.tech/", string clientName = "platform")
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.Protocols = HttpProtocols.Http2));
            builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions>(options => options.IOQueueCount = 0);
            builder.Logging.ClearProviders();
            builder.Services.AddTerraBaseRestRpc(static (_, _) => { });
            var app = builder.Build();
            app.UseTerraBaseRestPipeline();
            app.UseRouting();
            app.MapTerraBaseRestRpc();
            app.MapMethods("/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"], async (HttpContext http) =>
            {
                using var client = factory.CreateClient(clientName);
                using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), new Uri(new Uri(origin), http.Request.Path + http.Request.QueryString));
                if (http.Request.ContentLength is > 0)
                {
                    request.Content = new StreamContent(http.Request.Body);
                }

                foreach (var header in http.Request.Headers)
                {
                    if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) && !request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    {
                        request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                    }
                }

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
                http.Response.StatusCode = (int)response.StatusCode;
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    {
                        http.Response.Headers[header.Key] = header.Value.ToArray();
                    }
                }

                await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
            });
            try
            {
                await app.StartAsync();
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                return new(app, new(address));
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }

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
