using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class ProducerReactiveConformanceTests
{
    [Test]
    public async Task ProducerSnapshotAndDeltaUpdateThePublicSubscription()
    {
        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "conformance", "reactive.json"));
        using var corpus = await JsonDocument.ParseAsync(stream);
        using var handler = new CorpusHandler(corpus.RootElement);
        using var http = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(http, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("demo", "v-conformance-view", "SELECT id FROM orders ORDER BY id");

        await Assert.That(subscription.CurrentSnapshot.Items.Count).IsEqualTo(0);
        var delta = await subscription.ReadNextAsync();
        await Assert.That(delta!.Frame).IsTypeOf<TerraScaleReactiveDelta>();
        await Assert.That(subscription.Offset).IsEqualTo(1ul);
        await Assert.That(subscription.CurrentSnapshot.Items.Count).IsEqualTo(1);
        await Assert.That(subscription.CurrentSnapshot.Items[0].Identity).IsEqualTo("order-1");
        var field = subscription.CurrentSnapshot.Items[0].Fields.Single();
        await Assert.That(field.Path).IsEqualTo("status");
        await Assert.That(Encoding.UTF8.GetString(field.Value.Span)).IsEqualTo("\"paid\"");

        var reset = await subscription.ReadNextAsync();
        await Assert.That(reset!.Frame).IsTypeOf<TerraScaleReactiveReset>();
        await Assert.That(subscription.CurrentSnapshot.Items.Count).IsEqualTo(0);
        await Assert.That(await subscription.ReadNextAsync()).IsNull();
        await Assert.That(subscription.Offset).IsEqualTo(1ul);
    }

    [Test]
    public async Task ProducerHostileVectorsAreRejectedByThePublicSubscription()
    {
        await using var reactiveStream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "conformance", "reactive.json"));
        using var corpus = await JsonDocument.ParseAsync(reactiveStream);
        await using var hostileStream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "conformance", "hostile-vectors.json"));
        using var hostile = await JsonDocument.ParseAsync(hostileStream);
        var vectors = hostile.RootElement.GetProperty("vectors").EnumerateArray().ToArray();
        await Assert.That(vectors.Length).IsGreaterThan(0);
        foreach (var vector in vectors)
        {
            using var handler = new CorpusHandler(corpus.RootElement, Convert.FromHexString(vector.GetProperty("hex").GetString()!));
            using var http = new HttpClient(handler);
            var client = new TerraScaleReactiveHttpClient(http, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
            await using var subscription = await client.SubscribeAsync("demo", "v-conformance-view", "SELECT id FROM orders ORDER BY id");
            await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
            await Assert.That(subscription.Offset).IsEqualTo(0ul);
            await Assert.That(subscription.CurrentSnapshot.Items.Count).IsEqualTo(0);
        }
    }

    private sealed class CorpusHandler(JsonElement corpus, byte[]? logOverride = null) : HttpMessageHandler
    {
        private int logRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                var fixture = corpus.GetProperty("attach").GetProperty("response");
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)fixture.GetProperty("status").GetInt32())
                {
                    Content = new StringContent(fixture.GetProperty("body").GetRawText(), Encoding.UTF8, fixture.GetProperty("mediaType").GetString()!),
                });
            }

            var name = request.RequestUri!.AbsolutePath.Contains("/snapshot/", StringComparison.Ordinal)
                ? "snapshot"
                : logRequests++ switch { 0 => "log", 1 => "reset", _ => "timeout" };
            var frame = corpus.GetProperty(name);
            var response = new HttpResponseMessage((HttpStatusCode)frame.GetProperty("status").GetInt32())
            {
                Content = new ByteArrayContent(name == "log" && logOverride is not null ? logOverride : Convert.FromHexString(frame.GetProperty("hex").GetString()!)),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(frame.GetProperty("mediaType").GetString()!);
            if (frame.TryGetProperty("headers", out var headers))
            {
                foreach (var header in headers.EnumerateObject())
                {
                    if (!response.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString()))
                        response.Content.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
                }
            }
            if (frame.TryGetProperty("cacheControl", out var cacheControl))
                response.Headers.CacheControl = CacheControlHeaderValue.Parse(cacheControl.GetString()!);
            return Task.FromResult(response);
        }
    }
}
