using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using TerraScale.DotNet.TerraBase;

namespace ApiBench;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(Spec))]
[JsonSerializable(typeof(Credentials))]
[JsonSerializable(typeof(Step))]
[JsonSerializable(typeof(StepResult))]
[JsonSerializable(typeof(Startup))]
[JsonSerializable(typeof(Ready))]
[JsonSerializable(typeof(Done))]
internal partial class Wire : JsonSerializerContext
{
}

internal sealed record Spec(
    string Name,
    string Endpoint,
    string AuthIssuer,
    int StepS,
    int WarmupS,
    int SeedDocuments,
    int[] PayloadBytes,
    int[] Concurrency,
    Safety Safety,
    Scenario[] Scenarios,
    OpenLoop OpenLoop,
    RawBaseline RawBaseline);

internal sealed record Safety(double MaxErrorRate, double MaxP99Ratio);

internal sealed record Scenario(string Name, int[]? PayloadBytes, int[]? Concurrency);

internal sealed record OpenLoop(string[] Scenarios, double[] Fractions);

internal sealed record RawBaseline(string[] Scenarios, int[] PayloadBytes, string[] Transports);

internal sealed record Credentials(string ApiKey, string Project);

internal sealed record Step(
    string Type,
    string Scenario,
    string Transport,
    int PayloadBytes,
    string Mode,
    int Concurrency,
    double Rate,
    int WarmupS,
    int StepS,
    double AbortErrorRate,
    long AbortP99Us);

internal sealed record Latency(long P50, long P90, long P99, [property: JsonPropertyName("p999")] long P999, long Max, long Mean);

internal sealed record StepResult(
    string Type,
    string Scenario,
    string Transport,
    int PayloadBytes,
    string Mode,
    int Concurrency,
    double Rate,
    string Start,
    double DurationS,
    long Ops,
    long Ok,
    long Errors,
    double ErrorRate,
    double Rps,
    Dictionary<string, long> Status,
    Latency LatencyUs,
    long BytesOut,
    long BytesIn,
    double CpuS,
    double RssMb,
    string Stopped)
{
    /// <summary>A step skipped because its untimed seeding failed.</summary>
    internal static StepResult Failed(Step step, DateTimeOffset at) => new(
        "result", step.Scenario, step.Transport, step.PayloadBytes, step.Mode, step.Concurrency, step.Rate, Protocol.Stamp(at),
        0, 0, 0, 0, 0, 0, new() { ["setup"] = 1 }, new(0, 0, 0, 0, 0, 0), 0, 0, 0, Protocol.RssMb(), "setup");
}

internal sealed record Startup(string Type, double InitMs);

internal sealed record Ready(string Type, string Client, string Sdk, string SdkVersion, string Runtime, string Database, double SetupMs, double AuthMs);

internal sealed record Done(string Type, bool Cleanup, string CleanupError);

internal static class Protocol
{
    internal const string SdkName = "ts-dotnet";

    internal static string Client => RuntimeFeature.IsDynamicCodeSupported ? "dotnet-jit" : "dotnet-aot";

    internal static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal static double RssMb() => Environment.WorkingSet / 1048576.0;

    /// <summary>The SDK assembly version and the revision pinned in sdk.rev.</summary>
    internal static string SdkVersion()
    {
        using var stream = typeof(Protocol).Assembly.GetManifestResourceStream("ApiBench.sdk.rev")
            ?? throw new InvalidOperationException("sdk.rev is not embedded");
        using var reader = new StreamReader(stream);
        return $"{typeof(TerraBaseDocumentsClient).Assembly.GetName().Version} ({reader.ReadToEnd().Trim()})";
    }
}
