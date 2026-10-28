using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApiBench;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using TUnit.Core;

namespace BenchClient.Tests;

public sealed class ProtocolTests
{
    [Test]
    [Arguments(256)]
    [Arguments(4096)]
    [Arguments(65536)]
    public async Task PayloadIsExactUtf8Bytes(int bytes)
    {
        for (var group = 0; group < Payload.Groups; group++)
        {
            var json = Payload.Json(group, bytes);
            await Assert.That(Encoding.UTF8.GetByteCount(json)).IsEqualTo(bytes);
            using var document = JsonDocument.Parse(json);
            await Assert.That(document.RootElement.GetProperty("g").GetInt32()).IsEqualTo(group);
        }
    }

    [Test]
    public async Task DatabaseNameIsLowercaseAndValid()
    {
        var name = Bench.DatabaseName(new TerraScaleIdGenerator().NewId());
        await Assert.That(Regex.IsMatch(name, "^[a-z][a-z0-9_-]{0,62}$")).IsTrue();
    }

    [Test]
    [Arguments("query.small", 4096, 256)]
    [Arguments("auth.token", 4096, 0)]
    [Arguments("doc.get", 4096, 4096)]
    public async Task SeedSizeFollowsScenario(string scenario, int payload, int expected) =>
        await Assert.That(Payload.SizeFor(scenario, payload)).IsEqualTo(expected);

    [Test]
    public async Task StepAndResultUseExactSnakeCaseNames()
    {
        var step = JsonSerializer.Deserialize("""
            {"type":"step","scenario":"doc.get","transport":"sdk","payload_bytes":4096,"mode":"open","concurrency":8,
             "rate":100,"warmup_s":2,"step_s":10,"abort_error_rate":0.01,"abort_p99_us":5000}
            """, Wire.Default.Step)!;
        await Assert.That(step.AbortP99Us).IsEqualTo(5000);
        await Assert.That(step.Rate).IsEqualTo(100);

        var result = new StepResult("result", "doc.get", "sdk", 4096, "closed", 8, 0, "2026-10-11T12:00:00Z", 10.01, 10, 9, 1, 0.1, 0.9,
            new() { ["ok"] = 9, ["429"] = 1 }, new(1, 2, 3, 4, 5, 6), 7, 8, 0.5, 61.5, "");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, Wire.Default.StepResult));
        var root = document.RootElement;
        string[] names = ["type", "scenario", "transport", "payload_bytes", "mode", "concurrency", "rate", "start", "duration_s", "ops", "ok", "errors",
            "error_rate", "rps", "status", "latency_us", "bytes_out", "bytes_in", "cpu_s", "rss_mb", "stopped"];
        await Assert.That(root.EnumerateObject().Count()).IsEqualTo(names.Length);
        foreach (var name in names)
        {
            await Assert.That(root.TryGetProperty(name, out _)).IsTrue();
        }

        var latency = root.GetProperty("latency_us");
        string[] latencyNames = ["p50", "p90", "p99", "p999", "max", "mean"];
        foreach (var name in latencyNames)
        {
            await Assert.That(latency.TryGetProperty(name, out _)).IsTrue();
        }

        var rejected = false;
        try
        {
            JsonSerializer.Deserialize("""{"type":"step","bogus":1}""", Wire.Default.Step);
        }
        catch (JsonException)
        {
            rejected = true;
        }

        await Assert.That(rejected).IsTrue();
    }

    [Test]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FAV_0123456789abcdefghjkmnpqrstvwxyzaaaaaaaaaaaaaaaaaaaa", "01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    public async Task ClientIdIsTheKeysUlid(string key, string expected) =>
        await Assert.That(Tokens.ClientId(key)).IsEqualTo(expected);

    [Test]
    [Arguments("tsk_01ARZ3NDEKTSV4RRFFQ69G5FAV_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FAV_")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FAV_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [Arguments("01ARZ3NDEKTSV4RRFFQ69G5FAV_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaau")]
    [Arguments("not-a-ulid-at-all-here-xx_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task MalformedKeyIsRejectedWithoutEchoingIt(string key)
    {
        var rejected = false;
        try
        {
            Tokens.ClientId(key);
        }
        catch (FormatException error)
        {
            rejected = !error.Message.Contains(key, StringComparison.Ordinal);
        }

        await Assert.That(rejected).IsTrue();
    }

    [Test]
    public async Task RawReadUsesTheByKeyRoute()
    {
        var uri = Http.ReadUri(new Uri("https://gru.api.terrascale.tech"), "01ARZ3NDEKTSV4RRFFQ69G5FAV", "bench-x", "seed_256", "01ARZ3NDEKTSV4RRFFQ69G5FAA");
        await Assert.That(uri.ToString()).IsEqualTo(
            "https://gru.api.terrascale.tech/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV/databases/bench-x/collections/seed_256/documents/by-key?id=01ARZ3NDEKTSV4RRFFQ69G5FAA");
    }

    [Test]
    public async Task SdkErrorsMapToStatusKeys()
    {
        var missing = Documents(HttpStatusCode.NotFound, """{"error":{"code":"not_found","message":"missing"}}""");
        var document = await missing.Database("01ARZ3NDEKTSV4RRFFQ69G5FAV", "bench-x").GetDocumentAsync("seed_256", "01ARZ3NDEKTSV4RRFFQ69G5FAA");
        await Assert.That(Http.ReadStatus(document)).IsEqualTo("404");

        var limited = Documents(HttpStatusCode.TooManyRequests, """{"error":{"code":"rate_limited","message":"slow down"}}""");
        Exception? error = null;
        try
        {
            await limited.Database("01ARZ3NDEKTSV4RRFFQ69G5FAV", "bench-x").GetDocumentAsync("seed_256", "01ARZ3NDEKTSV4RRFFQ69G5FAA");
        }
        catch (Exception caught)
        {
            error = caught;
        }

        await Assert.That(error).IsNotNull();
        await Assert.That(Http.StatusOf(error!)).IsEqualTo("429");
    }

    [Test]
    public async Task DatabaseCreateUsesTheProjectRoute()
    {
        var handler = new FakeHandler(HttpStatusCode.BadRequest, """{"error":{"code":"invalid_request","message":"stop"}}""");
        var documents = new TerraBaseDocumentsClient(new HttpClient(handler), new TerraBaseDocumentsClientOptions
        {
            BaseAddress = new Uri("https://gru.api.terrascale.tech"),
            AccessTokenProvider = _ => ValueTask.FromResult("token"),
        });
        var refused = false;
        try
        {
            await Bench.CreateDatabaseAsync(documents, "01ARZ3NDEKTSV4RRFFQ69G5FAV", "bench-x", CancellationToken.None);
        }
        catch (TerraBaseException)
        {
            refused = true;
        }

        await Assert.That(refused).IsTrue();
        await Assert.That(handler.RequestMethod).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.RequestUri!.AbsolutePath).IsEqualTo("/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV/databases");
        await Assert.That(handler.RequestBody).IsEqualTo("""{"name":"bench-x","allRegions":true}""");
    }

    [Test]
    public async Task CountingHandlerCountsRequestAndResponseBytes()
    {
        const string body = "{\"g\":1}";
        var counter = new CountingHandler(new FakeHandler(HttpStatusCode.OK, body));
        using var client = new HttpClient(counter);
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://gru.api.terrascale.tech/") { Content = new StringContent("abc") },
            HttpCompletionOption.ResponseHeadersRead);
        await response.Content.CopyToAsync(Stream.Null);
        await Assert.That(counter.BytesOut).IsEqualTo(3);
        await Assert.That(counter.BytesIn).IsEqualTo(body.Length);
    }

    private static TerraBaseDocumentsClient Documents(HttpStatusCode status, string body) => new(
        new HttpClient(new FakeHandler(status, body)),
        new TerraBaseDocumentsClientOptions { BaseAddress = new Uri("https://gru.api.terrascale.tech"), AccessTokenProvider = _ => ValueTask.FromResult("token") });

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        internal HttpMethod? RequestMethod { get; private set; }
        internal Uri? RequestUri { get; private set; }
        internal string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestMethod = request.Method;
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}

public sealed class LoopTests
{
    private static readonly SocketsHttpHandler Idle = new();

    [Test]
    public async Task ClosedLoopCountsEveryStatusInTheWindow()
    {
        var calls = 0L;
        Op op = async (ct, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            await Task.Delay(1, ct);
            return (call % 4) switch
            {
                0 => throw new TimeoutException(),
                1 => "429",
                _ => "ok",
            };
        };
        var result = await new Driver(StepOf(mode: "closed", concurrency: 4, seconds: 1), op, new CountingHandler(Idle)).RunAsync(CancellationToken.None);
        await Assert.That(result.Ops).IsGreaterThan(0);
        await Assert.That(result.Status.Values.Sum()).IsEqualTo(result.Ops);
        await Assert.That(result.Status.GetValueOrDefault("timeout")).IsGreaterThan(0);
        await Assert.That(result.Status.GetValueOrDefault("429")).IsGreaterThan(0);
        await Assert.That(result.Ok).IsEqualTo(result.Status.GetValueOrDefault("ok"));
        await Assert.That(result.Errors).IsEqualTo(result.Ops - result.Ok);
        await Assert.That(result.Stopped).IsEqualTo("");
    }

    [Test]
    public async Task HalfFailingStepAbortsOnErrorRate()
    {
        var calls = 0L;
        Op op = async (ct, _) =>
        {
            await Task.Delay(1, ct);
            return Interlocked.Increment(ref calls) % 2 == 0 ? "ok" : "429";
        };
        var result = await new Driver(StepOf(mode: "closed", concurrency: 4, seconds: 5, abortErrorRate: 0.01), op, new CountingHandler(Idle))
            .RunAsync(CancellationToken.None);
        await Assert.That(result.Stopped).IsEqualTo("error rate");
        await Assert.That(result.DurationS).IsLessThan(5.0);
    }

    [Test]
    public async Task OpenLoopDropsArrivalsWhenTheCapIsFull()
    {
        Op op = async (ct, _) =>
        {
            await Task.Delay(200, ct);
            return "ok";
        };
        var result = await new Driver(StepOf(mode: "open", concurrency: 1, seconds: 1, rate: 50), op, new CountingHandler(Idle)).RunAsync(CancellationToken.None);
        await Assert.That(result.Status.GetValueOrDefault("dropped")).IsGreaterThan(0);
        await Assert.That(result.Ops).IsEqualTo(result.Status.Values.Sum());
    }

    private static Step StepOf(string mode, int concurrency, int seconds, double rate = 0, double abortErrorRate = 0) =>
        new("step", "doc.get", "sdk", 256, mode, concurrency, rate, 0, seconds, abortErrorRate, 0);
}

public sealed class CountingHandlerTests
{
    [Test]
    public async Task WrappedContentKeepsHeadersAndCountsBytes()
    {
        const string body = "{\"ok\":true}";
        var counting = new CountingHandler(new JsonHandler(body));
        using var client = new HttpClient(counting);
        using var response = await client.GetAsync(new Uri("https://gru.api.terrascale.tech/probe"));
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(body);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
        await Assert.That(response.Content.Headers.ContentLength).IsEqualTo(Encoding.UTF8.GetByteCount(body));
        await Assert.That(counting.BytesIn).IsEqualTo(Encoding.UTF8.GetByteCount(body));
    }

    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) { Headers = { ContentType = new("application/json") } },
            });
    }
}
