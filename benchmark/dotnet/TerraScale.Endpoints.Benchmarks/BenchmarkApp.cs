using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Endpoints.Benchmarks;

/// <summary>
/// One app that maps every scenario three ways: minimal API with the Request Delegate Generator
/// (/rdg), minimal API through the reflection-based RequestDelegateFactory (/rdf), and
/// TerraScale endpoints (/ts). Handlers do the same work so only the plumbing differs.
/// </summary>
public static class BenchmarkApp
{
    public static readonly string[] Scenarios = ["health", "echo", "send", "scoped", "text"];

    public static WebApplication Build(string[] args, bool kestrel)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        if (!kestrel)
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        }

        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, BenchJsonContext.Default));
        builder.Services.AddScoped<ScopedDependency>();
        builder.Services.AddTerraScaleEndpoints();

        var app = builder.Build();

        // RDG intercepts these call sites at compile time because the handlers are lambdas.
        app.MapGet("/rdg/health", () => new HealthResponse("ok"));
        app.MapPost("/rdg/echo", (EchoRequest request) => new EchoResponse(request.Name));
        app.MapPost("/rdg/send", (EchoRequest request) => TypedResults.Ok(new EchoResponse(request.Name)));
        app.MapGet("/rdg/scoped", ([FromServices] ScopedDependency dependency) => dependency.Value);
        app.MapGet("/rdg/text", () => "ok");

        // Handlers typed as Delegate cannot be analyzed by RDG, so they fall back to RequestDelegateFactory.
        MapRdf(app, "GET", "/rdf/health", () => new HealthResponse("ok"));
        MapRdf(app, "POST", "/rdf/echo", (EchoRequest request) => new EchoResponse(request.Name));
        MapRdf(app, "POST", "/rdf/send", (EchoRequest request) => TypedResults.Ok(new EchoResponse(request.Name)));
        MapRdf(app, "GET", "/rdf/scoped", ([FromServices] ScopedDependency dependency) => dependency.Value);
        MapRdf(app, "GET", "/rdf/text", () => "ok");

        app.MapTerraScaleEndpoints();
        return app;
    }

    public static string Method(string scenario) => scenario is "echo" or "send" ? "POST" : "GET";

    public static readonly byte[] EchoBody = JsonSerializer.SerializeToUtf8Bytes(new EchoRequest("widget"), BenchJsonContext.Default.EchoRequest);

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Benchmark runs on the JIT; this is the reflection baseline on purpose.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Benchmark runs on the JIT; this is the reflection baseline on purpose.")]
    private static void MapRdf(WebApplication app, string method, string pattern, Delegate handler) =>
        app.MapMethods(pattern, [method], handler);
}

public sealed record HealthResponse(string Status);
public sealed record EchoRequest(string Name);
public sealed record EchoResponse(string Name);

public sealed class ScopedDependency
{
    public string Value { get; } = "scoped";
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(EchoRequest))]
[JsonSerializable(typeof(EchoResponse))]
internal sealed partial class BenchJsonContext : JsonSerializerContext;

public sealed class HealthEndpoint : EndpointWithoutRequest<HealthResponse>
{
    public override void Configure() => Get("/ts/health");

    public override ValueTask<HealthResponse> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new HealthResponse("ok"));
}

public sealed class EchoEndpoint : Endpoint<EchoRequest, EchoResponse>
{
    public override void Configure() => Post("/ts/echo");

    public override ValueTask<EchoResponse> HandleAsync(EchoRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EchoResponse(request.Name));
}

public sealed class SendEndpoint : Endpoint<EchoRequest>
{
    public override void Configure() => Post("/ts/send");

    public override ValueTask HandleAsync(EchoRequest request, CancellationToken cancellationToken) =>
        Send.OkAsync(new EchoResponse(request.Name), cancellationToken);
}

public sealed class ScopedEndpoint(ScopedDependency dependency) : EndpointWithoutRequest<string>
{
    public override void Configure() => Get("/ts/scoped");

    public override ValueTask<string> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult(dependency.Value);
}

public sealed class TextEndpoint : EndpointWithoutRequest<string>
{
    public override void Configure() => Get("/ts/text");

    public override ValueTask<string> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult("ok");
}
