using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TerraScale.Authentication.Tests;

/// <summary>Controlled HTTP mail provider shared by the Fast and Slow integration suites.</summary>
internal static class EmailProviderFixture
{
    private const string Recipient = "private-person@example.test";
    private const string AcceptedResponse = "{\"success\":true,\"result\":{\"delivered\":[],\"queued\":[\"" + Recipient + "\"],\"permanent_bounces\":[],\"suppressed_recipients\":[]}}";

    /// <summary>A real HTTP provider on a test-owned ephemeral loopback port.</summary>
    internal sealed class FakeCloudflare(WebApplication app) : IAsyncDisposable
    {
        private readonly WebApplication app = app;

        public Uri Address { get; private set; } = null!;

        public string? RequestUrl { get; set; }

        public string? Authorization { get; private set; }

        public string? Body { get; private set; }

        public int Calls { get; private set; }

        public HttpStatusCode Status { get; set; }

        public string ResponseBody { get; set; } = AcceptedResponse;

        public static async Task<FakeCloudflare> StartAsync(HttpStatusCode status = HttpStatusCode.OK, bool abort = false, string body = AcceptedResponse)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var provider = new FakeCloudflare(builder.Build()) { Status = status, ResponseBody = body };
            MapProvider(provider, abort);
            try
            {
                await provider.app.StartAsync();
                provider.Address = new Uri(provider.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                return provider;
            }
            catch
            {
                await provider.DisposeAsync();
                throw;
            }
        }

        private static void MapProvider(FakeCloudflare provider, bool abort) => provider.app.Run(async context =>
        {
            provider.Authorization = context.Request.Headers.Authorization.ToString();
            provider.Calls++;
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            provider.Body = await reader.ReadToEndAsync(context.RequestAborted);
            if (abort)
            {
                context.Abort();
                return;
            }

            context.Response.StatusCode = (int)provider.Status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(provider.ResponseBody, context.RequestAborted);
        });

        public ValueTask DisposeAsync() => this.app.DisposeAsync();
    }

    internal sealed class ProviderRoute(Uri address, FakeCloudflare provider) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            provider.RequestUrl = request.RequestUri!.ToString();
            request.RequestUri = new Uri(address, request.RequestUri.PathAndQuery);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
