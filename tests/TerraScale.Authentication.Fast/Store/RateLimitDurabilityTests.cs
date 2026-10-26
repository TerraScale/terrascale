using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Hosting;

namespace TerraScale.Authentication.Fast.Store;

/// <summary>
/// Rate-limit buckets are written local_durable; every other write still waits for another region's durable acknowledgement.
/// </summary>
public sealed class RateLimitDurabilityTests
{
    private const string Database = "/v1/projects/00000000000000000000000002/databases/ts-auth/collections";

    [Test]
    public async Task Rate_limit_bucket_writes_skip_the_remote_ack_while_other_writes_keep_it()
    {
        await using var recorder = await DurabilityRecorder.StartAsync();
        using var client = new HttpClient(TerraBaseTransport.CreateHandler([], NullLogger.Instance))
        {
            BaseAddress = recorder.Address,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        var bucket = $"{Database}/{AttemptLimiter.Collection}/documents/by-key?id=01M5X2000000000000000000AA";
        var user = $"{Database}/users/documents/by-key?id=01M5X2000000000000000000AB";
        using (await client.PutAsync(bucket, new StringContent("{}")))
        using (await client.PutAsync(user, new StringContent("{}")))
        using (await client.DeleteAsync(bucket))
        {
        }

        await Assert.That(recorder.Durability($"PUT {AttemptLimiter.Collection}")).IsEqualTo("");
        await Assert.That(recorder.Durability($"DELETE {AttemptLimiter.Collection}")).IsEqualTo("");
        await Assert.That(recorder.Durability("PUT users")).IsEqualTo("remote_durable");
    }

    private sealed class DurabilityRecorder : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly ConcurrentDictionary<string, string> seen = new(StringComparer.Ordinal);

        private DurabilityRecorder(WebApplication app) => this.app = app;

        public Uri Address { get; private set; } = null!;

        public string? Durability(string request) => this.seen.TryGetValue(request, out var value) ? value : null;

        public static async Task<DurabilityRecorder> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.Protocols = HttpProtocols.Http2));
            var recorder = new DurabilityRecorder(builder.Build());
            recorder.app.Run(context =>
            {
                var path = context.Request.Path.Value ?? "";
                var collection = path.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipWhile(part => part != "collections").Skip(1).FirstOrDefault() ?? "";
                recorder.seen[$"{context.Request.Method} {collection}"] = context.Request.Headers[TerraBaseTransport.DurabilityHeader].ToString();
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });
            try
            {
                await recorder.app.StartAsync();
                recorder.Address = new Uri(recorder.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                return recorder;
            }
            catch
            {
                await recorder.DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync() => this.app.DisposeAsync();
    }
}
