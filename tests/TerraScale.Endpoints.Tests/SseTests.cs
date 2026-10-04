using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace TerraScale.Endpoints.Tests;

public sealed class SseTests
{
    [Test]
    public async Task StreamSetsKeepAliveHeadersOnHttp11()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints([EndpointRegistration.Create<RawSseEndpoint>()]);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.GetAsync(RawSseEndpoint.Path);
            await Assert.That(response.Content.Headers.ContentType?.ToString()).IsEqualTo("text/event-stream");
            var cacheControl = response.Headers.TryGetValues("Cache-Control", out var values) ? string.Join(',', values) : string.Empty;
            await Assert.That(cacheControl).IsEqualTo("no-cache");
            await Assert.That(string.Join(' ', response.Headers.Connection)).IsEqualTo("keep-alive");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Test]
    public async Task RawSseEventsAndCommentsUseTheSseWireFormat()
    {
        var (contentType, body) = await GetSseAsync(EndpointRegistration.Create<RawSseEndpoint>(), RawSseEndpoint.Path);
        await Assert.That(contentType).IsEqualTo("text/event-stream");
        await Assert.That(body).IsEqualTo(": ping\n\nevent: greeting\nid: 1\ndata: hello world\n\n");
    }

    [Test]
    public async Task EndpointsExposeTheRequestContext()
    {
        var body = await GetStringAsync(EndpointRegistration.Create<ContextProbeEndpoint>(), ContextProbeEndpoint.Path);
        await Assert.That(body).Contains("/context-probe");
    }

    [Test]
    public async Task ProducesSseDocumentsTextEventStreamResponses()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints([EndpointRegistration.Create<RawSseEndpoint>()]);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        await app.StartAsync();
        var endpoint = app.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).Single();
        var produced = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();
        await Assert.That(produced.SelectMany(item => item.ContentTypes)).Contains("text/event-stream");
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private static async Task<(string ContentType, string Body)> GetSseAsync(EndpointRegistration endpoint, string path)
    {
        var (contentType, bytes) = await RequestAsync(endpoint, path);
        return (contentType, Encoding.UTF8.GetString(bytes));
    }

    private static async Task<string> GetStringAsync(EndpointRegistration endpoint, string path)
    {
        var (_, bytes) = await RequestAsync(endpoint, path);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<(string ContentType, byte[] Body)> RequestAsync(EndpointRegistration endpoint, string path)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints([endpoint]);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.GetAsync(path);
            var body = await response.Content.ReadAsByteArrayAsync();
            return (response.Content.Headers.ContentType?.ToString() ?? string.Empty, body);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

public sealed class RawSseEndpoint : EndpointWithoutRequest<IResult>
{
    public const string Path = "/raw-sse";

    public override void Configure()
    {
        Get(Path);
        ProducesSse();
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Sse.Stream(async (sse, ct) =>
        {
            await sse.CommentAsync("ping", ct);
            await sse.SendEventAsync("greeting", ["hello world"], eventId: "1", cancellationToken: ct);
        }));
}

public sealed class ContextProbeEndpoint : EndpointWithoutRequest<string>
{
    public const string Path = "/context-probe";

    public override void Configure() => Get(Path);

    public override ValueTask<string> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(HttpContext.Request.Path.ToString());
}
