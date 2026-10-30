using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Rpc;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.E2E
{
    // The private plan endpoint sees a project only after its first database enrolls it.
    internal sealed class TestTerraBasePlans(IHttpClientFactory fallback, TestDatabases databases, Uri origin) : HttpMessageHandler, IHttpClientFactory
    {
        public ConcurrentQueue<(string Project, string Plan, HttpStatusCode Status)> Requests { get; } = new();

        public HttpClient CreateClient(string name)
        {
            return name == "terrabase-state"
            ? new(this, disposeHandler: false)
            : fallback.CreateClient(name);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            // A TerraBase without the organization plan route: the publisher falls back to each enrolled project.
            if (uri.AbsolutePath.Split('/') is ["", "admin", "organizations", var organization, "plan"] && TerraScaleIds.IsValid(organization) &&
                request.Method == HttpMethod.Put && uri.Query.Length == 0)
            {
                return new(HttpStatusCode.NotFound);
            }

            if (uri.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority) || uri.Query.Length != 0 ||
                uri.AbsolutePath.Split('/') is not ["", "admin", "projects", var project, "plan"] || !TerraScaleIds.IsValid(project) ||
                request.Method != HttpMethod.Put || request.Headers.Authorization is not null ||
                request.Content?.Headers.ContentType?.MediaType != "application/json")
            {
                throw new InvalidOperationException("Unexpected private TerraBase plan request.");
            }

            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            if (body.RootElement.ValueKind != JsonValueKind.Object || body.RootElement.EnumerateObject().Count() != 1 ||
                !body.RootElement.TryGetProperty("plan", out var value) || value.GetString() is not ("hobby" or "starter" or "pro" or "team" or "enterprise"))
            {
                throw new InvalidOperationException("Unexpected TerraBase plan body.");
            }

            var scope = new DatabaseScope("", "", "", "", project, "");
            var home = (await databases.RegionsAsync(cancellationToken))[0];
            var enrolled = (await databases.ListAsync(scope, home, cancellationToken)).Count > 0;
            var status = enrolled ? HttpStatusCode.OK : HttpStatusCode.NotFound;
            Requests.Enqueue((project, value.GetString()!, status));
            return new(status);
        }
    }
    internal sealed class H2cTestTransport : DelegatingHandler
    {
        public H2cTestTransport() : base(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false })
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            return base.SendAsync(request, cancellationToken);
        }
    }

    // Contract fixtures retain their REST producers while exercising the dashboard's real MagicOnion transport.
    internal abstract class RpcRestFake : HttpMessageHandler, IAsyncDisposable
    {
        private WebApplication? host;
        private HttpClient? rest;
        public TerraBaseRpcClient Rpc { get; private set; } = null!;

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.Protocols = HttpProtocols.Http2));
            builder.Logging.ClearProviders();
            builder.Services.AddTerraBaseRestRpc(static (_, _) => { });
            host = builder.Build();
            rest = new(this, disposeHandler: false);
            host.UseTerraBaseRestPipeline();
            host.UseRouting();
            host.MapTerraBaseRestRpc();
            host.MapMethods("/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"], ForwardAsync);
            await host.StartAsync();
            Rpc = new(new TestIdentity(), new Uri(host.Urls.Single()), "gru", null, host.Services.GetRequiredService<IMeterFactory>());
        }

        private async Task ForwardAsync(HttpContext context)
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), "http://127.0.0.1" + context.Request.Path + context.Request.QueryString);
            if (context.Request.ContentLength is > 0)
            {
                using var buffer = new MemoryStream();
                await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
                request.Content = new ByteArrayContent(buffer.ToArray());
            }
            foreach (var header in context.Request.Headers)
            {
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                {
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }
            using var response = await rest!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }

        public async ValueTask DisposeAsync()
        {
            Rpc?.Dispose();
            if (host is not null)
            {
                await host.StopAsync();
                await host.DisposeAsync();
            }
            rest?.Dispose();
            Dispose();
        }
    }

}
