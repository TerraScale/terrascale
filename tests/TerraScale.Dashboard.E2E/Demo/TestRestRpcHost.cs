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
}
