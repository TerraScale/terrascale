using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using TerraScale.Endpoints;

namespace TerraScale.Endpoints.Tests;

public sealed class DatastarTests
{
    [Test]
    public async Task PatchElementsEmitsTheSpecWireFormat()
    {
        var (contentType, body) = await GetSseAsync(typeof(PatchElementsEndpoint), PatchElementsEndpoint.Path);
        await Assert.That(contentType).IsEqualTo("text/event-stream");
        await Assert.That(body).IsEqualTo("event: datastar-patch-elements\ndata: elements <div id=\"a\">Hi</div>\n\n");
    }

    [Test]
    public async Task PatchElementsEmitsOnlyNonDefaultOptions()
    {
        var (_, body) = await GetSseAsync(typeof(FullPatchElementsEndpoint), FullPatchElementsEndpoint.Path);
        await Assert.That(body).IsEqualTo(
            "event: datastar-patch-elements\n" +
            "id: 123\n" +
            "retry: 2000\n" +
            "data: selector #feed\n" +
            "data: mode inner\n" +
            "data: useViewTransition true\n" +
            "data: viewTransitionSelector #main\n" +
            "data: namespace svg\n" +
            "data: elements <div>\n" +
            "data: elements         Hello\n" +
            "data: elements </div>\n" +
            "\n");
    }

    [Test]
    public async Task RemoveFragmentsEmitsSelectorAndRemoveMode()
    {
        var (_, body) = await GetSseAsync(typeof(RemoveFragmentsEndpoint), RemoveFragmentsEndpoint.Path);
        await Assert.That(body).IsEqualTo("event: datastar-patch-elements\ndata: selector #feed\ndata: mode remove\n\n");
    }

    [Test]
    public async Task PatchSignalsSerializeWebJsonAndOnlyIfMissing()
    {
        var (_, body) = await GetSseAsync(typeof(PatchSignalsEndpoint), PatchSignalsEndpoint.Path);
        await Assert.That(body).IsEqualTo(
            "event: datastar-patch-signals\n" +
            "data: onlyIfMissing true\n" +
            "data: signals {\"output\":\"Patched\",\"show\":true}\n" +
            "\n");
    }

    [Test]
    public async Task RemoveSignalsBuildsNestedNullPatches()
    {
        var (_, body) = await GetSseAsync(typeof(RemoveSignalsEndpoint), RemoveSignalsEndpoint.Path);
        await Assert.That(body).IsEqualTo(
            "event: datastar-patch-signals\n" +
            "data: signals {\"user\":{\"name\":null},\"flag\":null}\n" +
            "\n");
    }

    [Test]
    public async Task ExecuteScriptAppendsASelfRemovingScriptTag()
    {
        var (_, body) = await GetSseAsync(typeof(ExecuteScriptEndpoint), ExecuteScriptEndpoint.Path);
        await Assert.That(body).IsEqualTo(
            "event: datastar-patch-elements\n" +
            "data: selector body\n" +
            "data: mode append\n" +
            "data: elements <script type=\"module\" data-effect=\"el.remove()\">console.log(1)</script>\n" +
            "\n");
    }

    [Test]
    public async Task StreamSetsKeepAliveHeadersOnHttp11()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new EndpointCatalog([typeof(PatchElementsEndpoint)]));
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.GetAsync(PatchElementsEndpoint.Path);
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
        var (contentType, body) = await GetSseAsync(typeof(RawSseEndpoint), RawSseEndpoint.Path);
        await Assert.That(contentType).IsEqualTo("text/event-stream");
        await Assert.That(body).IsEqualTo(": ping\n\nevent: greeting\nid: 1\ndata: hello world\n\n");
    }

    [Test]
    public async Task SenderStyleEndpointsCanStreamDatastarEvents()
    {
        var (_, body) = await GetSseAsync(typeof(SenderDatastarEndpoint), SenderDatastarEndpoint.Path);
        await Assert.That(body).IsEqualTo("event: datastar-patch-signals\ndata: signals {\"ok\":true}\n\n");
    }

    [Test]
    public async Task ReadSignalsReadsTheQueryParameterOnGet()
    {
        var body = await GetStringAsync(
            typeof(SignalsEchoEndpoint),
            SignalsEchoEndpoint.Path + "?datastar=" + Uri.EscapeDataString("{\"input\":\"hi\"}"));
        await Assert.That(body).IsEqualTo("input=hi");
    }

    [Test]
    public async Task ReadSignalsReadsTheJsonBodyOnPost()
    {
        var body = await PostStringAsync(typeof(SignalsEchoEndpoint), SignalsEchoEndpoint.Path, "{\"input\":\"yo\"}");
        await Assert.That(body).IsEqualTo("input=yo");
    }

    [Test]
    public async Task EndpointsExposeTheRequestContext()
    {
        var body = await GetStringAsync(typeof(ContextProbeEndpoint), ContextProbeEndpoint.Path);
        await Assert.That(body).Contains("/context-probe");
    }

    [Test]
    public async Task ProducesSseDocumentsTextEventStreamResponses()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new EndpointCatalog([typeof(PatchElementsEndpoint)]));
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        await app.StartAsync();
        var endpoint = app.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).Single();
        var produced = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();
        await Assert.That(produced.SelectMany(item => item.ContentTypes)).Contains("text/event-stream");
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private static async Task<(string ContentType, string Body)> GetSseAsync(Type endpointType, string path)
    {
        var (contentType, bytes) = await RequestAsync(endpointType, path, null);
        return (contentType, Encoding.UTF8.GetString(bytes));
    }

    private static async Task<string> GetStringAsync(Type endpointType, string path)
    {
        var (_, bytes) = await RequestAsync(endpointType, path, null);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<string> PostStringAsync(Type endpointType, string path, string json)
    {
        var (_, bytes) = await RequestAsync(endpointType, path, json);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<(string ContentType, byte[] Body)> RequestAsync(Type endpointType, string path, string? json)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new EndpointCatalog([endpointType]));
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = json is null
                ? await client.GetAsync(path)
                : await client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
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

public sealed class PatchElementsEndpoint : EndpointWithoutRequest<IResult>
{
    public const string Path ="/ds-elements";
    public override void Configure() { Get(Path); ProducesSse(); }
    public override Task<IResult> HandleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Datastar.Stream((datastar, ct) => datastar.PatchElementsAsync("<div id=\"a\">Hi</div>", cancellationToken: ct)));
}

public sealed class FullPatchElementsEndpoint : EndpointWithoutRequest<IResult>
{
    public const string Path ="/ds-elements-full";
    public override void Configure() { Get(Path); ProducesSse(); }
    public override Task<IResult> HandleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Datastar.Stream((datastar, ct) => datastar.PatchElementsAsync(
            "<div>\n        Hello\n</div>",
            selector: "#feed",
            mode: ElementPatchMode.Inner,
            useViewTransition: true,
            viewTransitionSelector: "#main",
            @namespace: ElementNamespace.Svg,
            eventId: "123",
            retry: TimeSpan.FromMilliseconds(2000),
            cancellationToken: ct)));
}

public sealed class RemoveFragmentsEndpoint : EndpointWithoutRequest<IResult>
{
    public const string Path ="/ds-remove";
    public override void Configure() { Get(Path); ProducesSse(); }
    public override Task<IResult> HandleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Datastar.Stream((datastar, ct) => datastar.RemoveFragmentsAsync("#feed", cancellationToken: ct)));
}

public sealed class PatchSignalsEndpoint : EndpointWithoutRequest<IResult>
{
    public const string Path ="/ds-signals";
    public override void Configure() { Get(Path); ProducesSse(); }
    public override Task<IResult> HandleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Datastar.Stream((datastar, ct) =>
            datastar.PatchSignalsAsync(new { Output = "Patched", Show = true }, onlyIfMissing: true, cancellationToken: ct)));
}

public sealed class RemoveSignalsEndpoint : EndpointWithoutRequest<IResult>
{
    public const string Path ="/ds-remove-signals";
    public override void Configure() { Get(Path); ProducesSse(); }
    public override Task<IResult> HandleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Datastar.Stream((datastar, ct) =>
            datastar.RemoveSignalsAsync(["user.name", "flag"], cancellationToken: ct)));
}

public sealed class ExecuteScriptEndpoint : EndpointWithoutRequest<IResult>
{
    public const string Path ="/ds-script";
    public override void Configure() { Get(Path); ProducesSse(); }
    public override Task<IResult> HandleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Datastar.Stream((datastar, ct) =>
            datastar.ExecuteScriptAsync("console.log(1)", attributes: ["type=\"module\""], cancellationToken: ct)));
}

public sealed class RawSseEndpoint : EndpointWithoutRequest<IResult>
{
    public const string Path ="/raw-sse";
    public override void Configure() { Get(Path); ProducesSse(); }
    public override Task<IResult> HandleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Sse.Stream(async (sse, ct) =>
        {
            await sse.CommentAsync("ping", ct);
            await sse.SendEventAsync("greeting", ["hello world"], eventId: "1", cancellationToken: ct);
        }));
}

public sealed class SenderDatastarEndpoint : EndpointWithoutRequest
{
    public const string Path ="/sender-ds";
    public override void Configure() { Get(Path); ProducesSse(); }
    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        await Send.ResponseAsync(Datastar.Stream((datastar, ct) => datastar.PatchSignalsAsync(new { ok = true }, cancellationToken: ct)), cancellationToken);
    }
}

public sealed record EchoSignals(string Input);

public sealed class SignalsEchoEndpoint : EndpointWithoutRequest<string>
{
    public const string Path ="/signals-echo";
    public override void Configure() { Verbs("GET", "POST"); Route(Path); }
    public override async Task<string> HandleAsync(CancellationToken cancellationToken)
    {
        var signals = await Datastar.ReadSignalsAsync<EchoSignals>(HttpContext.Request, cancellationToken);
        return $"input={signals.Input}";
    }
}

public sealed class ContextProbeEndpoint : EndpointWithoutRequest<string>
{
    public const string Path ="/context-probe";
    public override void Configure() { Get(Path); }
    public override Task<string> HandleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(HttpContext.Request.Path.ToString());
}
