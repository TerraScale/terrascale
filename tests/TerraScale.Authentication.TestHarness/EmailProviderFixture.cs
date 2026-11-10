using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TerraScale.Authentication.Tests;

internal static class EmailProviderFixture
{
    private const string Recipient = "private-person@example.test";
    private const string AcceptedResponse = "{\"success\":true,\"result\":{\"delivered\":[],\"queued\":[\"" + Recipient + "\"],\"permanent_bounces\":[],\"suppressed_recipients\":[]}}";

    internal sealed class FakeCloudflare(WebApplication app) : IAsyncDisposable
    {
        private readonly WebApplication app = app;
        private readonly Lock gate = new();
        private readonly Dictionary<string, TaskCompletionSource<string>> arrivals = new(StringComparer.OrdinalIgnoreCase);

        public Uri Address { get; private set; } = null!;

        public string? RequestUrl { get; set; }

        public string? Authorization { get; private set; }

        public string? Body { get; private set; }

        public int Calls { get; private set; }

        public HttpStatusCode Status { get; set; }

        public string ResponseBody { get; set; } = AcceptedResponse;

        public Task<string> WaitForAsync(string recipient)
        {
            lock (this.gate)
            {
                if (this.arrivals.TryGetValue(recipient, out var source))
                {
                    return source.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }

                var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                this.arrivals[recipient] = pending;
                return pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private void Arrived(string recipient, string body)
        {
            lock (this.gate)
            {
                if (this.arrivals.TryGetValue(recipient, out var source))
                {
                    source.TrySetResult(body);
                }
                else
                {
                    var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    done.SetResult(body);
                    this.arrivals[recipient] = done;
                }
            }
        }

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
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            provider.Body = body;
            provider.Arrived(RecipientOf(body), body);
            if (abort)
            {
                context.Abort();
                return;
            }

            context.Response.StatusCode = (int)provider.Status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(provider.ResponseBody == AcceptedResponse ? EchoRecipient(body) : provider.ResponseBody, context.RequestAborted);
        });

        private static string EchoRecipient(string body) =>
            "{\"success\":true,\"result\":{\"delivered\":[],\"queued\":[\"" + RecipientOf(body) + "\"],\"permanent_bounces\":[],\"suppressed_recipients\":[]}}";

        private static string RecipientOf(string body) =>
            body.Length == 0 ? "unknown" : (JsonNode.Parse(body)?["to"]?.GetValue<string>() ?? "unknown");

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
