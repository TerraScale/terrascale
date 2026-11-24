using System.Text.Json.Serialization;

namespace TerraScale.Benchmark;

internal sealed record ScenarioResult(
    string Name,
    int Iterations,
    double OpsPerSec,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MeanMs,
    double MinMs,
    double MaxMs);

internal sealed record SdkReport(string Sdk, IReadOnlyList<ScenarioResult> Scenarios);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SdkReport))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
