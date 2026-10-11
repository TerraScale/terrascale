using BenchmarkDotNet.Attributes;

namespace TerraScale.Endpoints.Benchmarks;

/// <summary>Prices the individual steps TerraScale adds per request, to explain HandlerBenchmarks.</summary>
[MemoryDiagnoser]
public class OverheadBenchmarks
{
    private WebApplication app = null!;
    private IServiceScopeFactory scopes = null!;
    private ObjectFactory<HealthEndpoint> factory = null!;

    [GlobalSetup]
    public void Setup()
    {
        this.app = BenchmarkApp.Build([], kestrel: false);
        this.scopes = this.app.Services.GetRequiredService<IServiceScopeFactory>();
        this.factory = ActivatorUtilities.CreateFactory<HealthEndpoint>([]);
    }

    [GlobalCleanup]
    public void Cleanup() => this.app.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark(Baseline = true, Description = "scope + context only")]
    public async Task<object> ScopeAndContext()
    {
        await using var scope = this.scopes.CreateAsyncScope();
        return new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    }

    [Benchmark(Description = "+ GetRequiredService(Type)")]
    public async Task<object> ResolveFromScope()
    {
        await using var scope = this.scopes.CreateAsyncScope();
        _ = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        return scope.ServiceProvider.GetRequiredService(typeof(HealthEndpoint));
    }

    [Benchmark(Description = "+ ObjectFactory<T>")]
    public async Task<object> ObjectFactory()
    {
        await using var scope = this.scopes.CreateAsyncScope();
        _ = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        return this.factory(scope.ServiceProvider, null);
    }

    [Benchmark(Description = "+ RequestAborted")]
    public async Task<object> RequestAborted()
    {
        await using var scope = this.scopes.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        return context.RequestAborted;
    }

    [Benchmark(Description = "+ Task.FromResult")]
    public async Task<object> TaskFromResult()
    {
        await using var scope = this.scopes.CreateAsyncScope();
        _ = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        return Task.FromResult(new HealthResponse("ok"));
    }
}
