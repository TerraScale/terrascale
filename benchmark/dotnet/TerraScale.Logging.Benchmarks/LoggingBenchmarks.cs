using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace TerraScale.Logging.Benchmarks;

/// <summary>Cost of one record through the TerraScale provider; output goes to a null sink.</summary>
[MemoryDiagnoser]
public class LoggingBenchmarks
{
    private ILoggerFactory _factory = null!;
    private ILogger _logger = null!;

    [GlobalSetup]
    public void Setup()
    {
        _factory = LoggerFactory.Create(builder => builder.AddTerraScaleLogging("bench", TextWriter.Null, Environment.GetEnvironmentVariable, TimeProvider.System));
        _logger = _factory.CreateLogger("Bench.Requests");
    }

    [GlobalCleanup]
    public void Cleanup() => _factory.Dispose();

    [Benchmark]
    public void PlainMessage() => _logger.LogInformation("Request finished");

    [Benchmark]
    public void PlainMessageZLog() => _logger.ZLogInformation($"Request finished");

    [Benchmark]
    public void ThreeFields() => _logger.LogInformation("Request {Method} {Path} finished in {ElapsedMs} ms", "GET", "/v1/documents", 12.5);

    [Benchmark]
    public void ThreeFieldsZLog()
    {
        var method = "GET";
        var path = "/v1/documents";
        var elapsedMs = 12.5;
        _logger.ZLogInformation($"Request {method:@Method} {path:@Path} finished in {elapsedMs:@ElapsedMs} ms");
    }

    [Benchmark]
    public void HotRequest()
    {
        using var activity = new Activity("request").Start();
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = "r-1", ["RequestPath"] = "/v1/documents" });
        _logger.LogWarning("Request {Method} {Path} finished in {ElapsedMs} ms", "GET", "/v1/documents", 12.5);
    }

    [Benchmark]
    public void HotRequestZLog()
    {
        using var activity = new Activity("request").Start();
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = "r-1", ["RequestPath"] = "/v1/documents" });
        var method = "GET";
        var path = "/v1/documents";
        var elapsedMs = 12.5;
        _logger.ZLogWarning($"Request {method:@Method} {path:@Path} finished in {elapsedMs:@ElapsedMs} ms");
    }
}
