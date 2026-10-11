using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http.Features;

namespace TerraScale.Endpoints.Benchmarks;

/// <summary>
/// Invokes each endpoint's final RequestDelegate with a fresh HttpContext and request scope.
/// Routing, Kestrel and sockets are identical for every variant, so this isolates the handler
/// plumbing: activation, binding, invocation and response writing.
/// </summary>
[MemoryDiagnoser]
public class HandlerBenchmarks
{
    private WebApplication app = null!;
    private IServiceScopeFactory scopes = null!;
    private RequestDelegate rdg = null!;
    private RequestDelegate rdf = null!;
    private RequestDelegate terraScale = null!;

    [ParamsSource(nameof(AllScenarios))]
    public string Scenario { get; set; } = "health";

    public static IEnumerable<string> AllScenarios => BenchmarkApp.Scenarios;

    [GlobalSetup]
    public void Setup()
    {
        this.app = BenchmarkApp.Build([], kestrel: false);
        this.scopes = this.app.Services.GetRequiredService<IServiceScopeFactory>();
        var endpoints = ((IEndpointRouteBuilder)this.app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToDictionary(endpoint => endpoint.RoutePattern.RawText!, endpoint => endpoint.RequestDelegate!);
        this.rdg = endpoints[$"/rdg/{this.Scenario}"];
        this.rdf = endpoints[$"/rdf/{this.Scenario}"];
        this.terraScale = endpoints[$"/ts/{this.Scenario}"];

        // Same status, content type and bytes from every variant, or the comparison is meaningless.
        var expected = Capture(this.rdg);
        foreach (var (name, handler) in new[] { ("rdf", this.rdf), ("ts", this.terraScale) })
        {
            var actual = Capture(handler);
            if (actual != expected)
            {
                throw new InvalidOperationException($"{name}/{this.Scenario} returned {actual}, expected {expected}.");
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup() => this.app.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark(Baseline = true, Description = "Minimal API (RDG)")]
    public Task MinimalRdg() => this.InvokeAsync(this.rdg, Stream.Null);

    [Benchmark(Description = "Minimal API (RDF)")]
    public Task MinimalRdf() => this.InvokeAsync(this.rdf, Stream.Null);

    [Benchmark(Description = "TerraScale")]
    public Task TerraScale() => this.InvokeAsync(this.terraScale, Stream.Null);

    private string Capture(RequestDelegate handler)
    {
        using var body = new MemoryStream();
        var context = this.InvokeAsync(handler, body).GetAwaiter().GetResult();
        return $"{context.Response.StatusCode} {context.Response.ContentType} {System.Text.Encoding.UTF8.GetString(body.ToArray())}";
    }

    private async Task<HttpContext> InvokeAsync(RequestDelegate handler, Stream responseBody)
    {
        await using var scope = this.scopes.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = BenchmarkApp.Method(this.Scenario);
        if (context.Request.Method == "POST")
        {
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = BenchmarkApp.EchoBody.Length;
            context.Request.Body = new MemoryStream(BenchmarkApp.EchoBody, writable: false);
            context.Features.Set<IHttpRequestBodyDetectionFeature>(HasBody.Instance);
        }

        context.Response.Body = responseBody;
        await handler(context);
        await context.Response.CompleteAsync();
        return context;
    }

    // Kestrel supplies this; minimal APIs treat a request without it as having no body.
    private sealed class HasBody : IHttpRequestBodyDetectionFeature
    {
        public static readonly HasBody Instance = new();

        public bool CanHaveBody => true;
    }
}
