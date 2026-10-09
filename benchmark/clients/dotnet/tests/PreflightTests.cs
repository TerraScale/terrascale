using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using TUnit.Core;

namespace BenchClient.Tests;

public sealed class PreflightTests
{
    private const string Valid = """
        {"name":"point-reads","duration_s":60,"warmup_s":5,"concurrency":8,
         "dataset":{"documents":128,"doc_size_bytes":1024},
         "mix":[{"op":"doc.get","weight":100}],"targets":{"p99_ms":200}}
        """;

    [Test]
    [Arguments("doc.create")]
    [Arguments("doc.get")]
    [Arguments("doc.update")]
    [Arguments("doc.delete")]
    [Arguments("query.simple")]
    [Arguments("query.indexed")]
    [Arguments("txn.multi")]
    public async Task AllContractOperationsValidate(string op)
    {
        var value = JsonNode.Parse(Valid)!;
        value["mix"]![0]!["op"] = op;
        var workload = Parse(value.ToJsonString());
        await Assert.That(workload.Mix[0].Op).IsEqualTo(op);
        await Assert.That(workload.Mix[0].DocSizeBytes).IsEqualTo(1024);
    }

    [Test]
    public async Task ExactPercentageMixAndPayloadOverrideValidate()
    {
        var value = JsonNode.Parse(Valid)!;
        value["name"] = "mixed-70-30";
        value["mix"] = JsonNode.Parse("""
            [{"op":"doc.get","weight":70},{"op":"doc.update","weight":30,"doc_size_bytes":32}]
            """);
        var workload = Parse(value.ToJsonString());
        await Assert.That(workload.Mix.Count).IsEqualTo(2);
        await Assert.That(workload.Mix[1].DocSizeBytes).IsEqualTo(32);
    }

    [Test]
    [Arguments("duration_s", "0")]
    [Arguments("duration_s", "601")]
    [Arguments("duration_s", "1.5")]
    [Arguments("warmup_s", "-1")]
    [Arguments("warmup_s", "61")]
    [Arguments("concurrency", "0")]
    [Arguments("concurrency", "257")]
    [Arguments("dataset.documents", "0")]
    [Arguments("dataset.documents", "10001")]
    [Arguments("dataset.doc_size_bytes", "31")]
    [Arguments("dataset.doc_size_bytes", "1048577")]
    [Arguments("targets.p99_ms", "0")]
    [Arguments("targets.p99_ms", "30001")]
    [Arguments("targets.p99_ms", "1e999")]
    [Arguments("name", "\"\"")]
    [Arguments("name", "\"../secret\"")]
    [Arguments("dataset", "null")]
    [Arguments("mix", "[]")]
    [Arguments("targets.p99_ms", "null")]
    public async Task InvalidBoundsAndTypesAreRejected(string path, string replacement)
    {
        var value = JsonNode.Parse(Valid)!;
        var pieces = path.Split('.');
        if (pieces.Length == 1) value[path] = JsonNode.Parse(replacement);
        else value[pieces[0]]![pieces[1]] = JsonNode.Parse(replacement);
        await Assert.That(() => Parse(value.ToJsonString())).Throws<PreflightException>();
    }

    [Test]
    [Arguments("[{\"op\":\"doc.get\",\"weight\":99}]")]
    [Arguments("[{\"op\":\"doc.get\",\"weight\":101}]")]
    [Arguments("[{\"op\":\"doc.get\",\"weight\":0}]")]
    [Arguments("[{\"op\":\"doc.get\",\"weight\":50},{\"op\":\"doc.get\",\"weight\":50}]")]
    [Arguments("[{\"op\":\"doc.unknown\",\"weight\":100}]")]
    [Arguments("[{\"op\":\"doc.get\",\"weight\":100,\"doc_size_bytes\":null}]")]
    public async Task InvalidMixCannotReachSdk(string mix)
    {
        var value = JsonNode.Parse(Valid)!;
        value["mix"] = JsonNode.Parse(mix);
        await Assert.That(() => Parse(value.ToJsonString())).Throws<PreflightException>();
    }

    [Test]
    public async Task EightMixEntriesFailTheCountLimitBeforeOperationValidation()
    {
        var value = JsonNode.Parse(Valid)!;
        var mix = new JsonArray();
        for (var i = 0; i < 8; i++)
            mix.Add(new JsonObject { ["op"] = "doc.get", ["weight"] = i == 0 ? 16 : 12 });
        value["mix"] = mix;
        var failure = await Assert.That(() => Parse(value.ToJsonString())).Throws<PreflightException>();
        await Assert.That(failure!.Message).IsEqualTo("mix must contain 1–7 distinct operations.");
    }

    [Test]
    [Arguments("{\"unknown\":1,")]
    [Arguments("{\"name\":\"duplicate\",")]
    public async Task UnknownAndDuplicateFieldsAreRejected(string prefix) =>
        await Assert.That(() => Parse(prefix + Valid[1..])).Throws<PreflightException>();

    [Test]
    public async Task NestedUnknownAndDuplicateFieldsAreRejected()
    {
        await Assert.That(() => Parse(Valid.Replace("\"p99_ms\":200", "\"p99_ms\":200,\"unexpected\":1"))).Throws<PreflightException>();
        await Assert.That(() => Parse(Valid.Replace("\"weight\":100", "\"weight\":100,\"weight\":100"))).Throws<PreflightException>();
    }

    [Test]
    public async Task MissingFieldsAndNullRampAreRejected()
    {
        var value = JsonNode.Parse(Valid)!.AsObject();
        value.Remove("warmup_s");
        await Assert.That(() => Parse(value.ToJsonString())).Throws<PreflightException>();
        value = JsonNode.Parse(Valid)!.AsObject();
        value["name"] = "ramp";
        value["ramp"] = null;
        await Assert.That(() => Parse(value.ToJsonString())).Throws<PreflightException>();
    }

    [Test]
    [Arguments("{} {}")]
    [Arguments("{")]
    [Arguments("{\"name\":/* comment */\"point-reads\"}")]
    [Arguments("{\"name\":\"point-reads\",}")]
    public async Task MalformedJsonIsRejected(string json) =>
        await Assert.That(() => Parse(json)).Throws<JsonException>();

    [Test]
    public async Task ProtocolLimitsValidateAtTheirInclusiveBounds()
    {
        var value = JsonNode.Parse(Valid)!;
        value["duration_s"] = 600;
        value["warmup_s"] = 60;
        value["concurrency"] = 256;
        value["dataset"]!["documents"] = 10_000;
        value["dataset"]!["doc_size_bytes"] = 1_048_576;
        value["targets"]!["p99_ms"] = 30_000;
        await Assert.That(Parse(value.ToJsonString()).Documents).IsEqualTo(10_000);
    }

    [Test]
    public async Task SevenStepRampMatchesSharedWorkload()
    {
        var workload = Parse(RampJson(105, 15, "[1,2,4,8,16,32,64]"));
        await Assert.That(workload.Ramp!.Concurrency.Count).IsEqualTo(7);
        await Assert.That(workload.Ramp.StepS).IsEqualTo(15);
    }

    [Test]
    [Arguments(10, 5, "[2,4]")]
    [Arguments(10, 5, "[1,1]")]
    [Arguments(15, 5, "[1,4,2]")]
    [Arguments(10, 5, "[1,257]")]
    [Arguments(9, 5, "[1,2]")]
    [Arguments(8, 4, "[1,2]")]
    [Arguments(40, 5, "[1,2,3,4,5,6,7,8]")]
    [Arguments(10, 5, "[]")]
    [Arguments(5, 5, "[1]")]
    public async Task UnsafeRampConfigurationsAreRejected(int duration, int step, string concurrency) =>
        await Assert.That(() => Parse(RampJson(duration, step, concurrency))).Throws<PreflightException>();

    [Test]
    public async Task RampRequiresExplicitStepsAndSupportsCustomNames()
    {
        await Assert.That(() => Parse(Valid.Replace("point-reads", "ramp"))).Throws<PreflightException>();
        var custom = Parse(RampJson(10, 5, "[1,2]").Replace("\"name\":\"ramp\"", "\"name\":\"custom-ramp\"", StringComparison.Ordinal));
        await Assert.That(custom.Ramp!.Concurrency.Count).IsEqualTo(2);
    }

    [Test]
    public async Task FractionalP99TargetMatchesRunner()
    {
        var workload = Parse(Valid.Replace("\"p99_ms\":200", "\"p99_ms\":200.5"));
        await Assert.That(workload.P99Ms).IsEqualTo(200.5);
    }

    [Test]
    public async Task InvalidUtf8IsRejected()
    {
        var bytes = Encoding.UTF8.GetBytes(Valid);
        bytes[Array.IndexOf(bytes, (byte)'p')] = 0xff;
        await Assert.That(() => Workload.Parse(bytes)).Throws<DecoderFallbackException>();
    }

    [Test]
    public async Task CliAcceptsAnyOptionOrderAndOnlyContractRegions()
    {
        var options = Cli.Parse(["run", "--out", "results.jsonl", "--region", "sfo", "--workload", "w.json"]);
        await Assert.That(options.Region).IsEqualTo("sfo");
        await Assert.That(() => Cli.Parse(["run", "--out", "r", "--region", "ams", "--workload", "w"])).Throws<PreflightException>();
        await Assert.That(() => Cli.Parse(["run", "--out", "r", "--out", "r", "--workload", "w"])).Throws<PreflightException>();
        await Assert.That(() => Cli.Parse(["run", "--out", "r", "--region", "gru"])).Throws<PreflightException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BlockedRunPreservesOutputAndPrintsNoSecrets(bool existingOutput)
    {
        var directory = Directory.CreateTempSubdirectory("dotnet-preflight-");
        try
        {
            var workload = Path.Combine(directory.FullName, "workload.json");
            var results = Path.Combine(directory.FullName, "results.jsonl");
            await File.WriteAllTextAsync(workload, Valid);
            if (existingOutput) await File.WriteAllTextAsync(results, "existing-evidence");
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await Cli.RunAsync(["run", "--workload", workload, "--region", "gru", "--out", results], output, error, () => "ci-host");
            await Assert.That(code).IsEqualTo(1);
            await Assert.That(error.ToString().StartsWith("SDK_BLOCKED:", StringComparison.Ordinal)).IsTrue();
            await Assert.That(error.ToString().Contains(SdkPrerequisites.Revision, StringComparison.Ordinal)).IsTrue();
            await Assert.That(output.ToString()).IsEqualTo("");
            await Assert.That(File.Exists(results)).IsEqualTo(existingOutput);
            if (existingOutput) await Assert.That(await File.ReadAllTextAsync(results)).IsEqualTo("existing-evidence");
        }
        finally { directory.Delete(recursive: true); }
    }

    [Test]
    [Arguments("{\"secret\":\"do-not-print-me\"}")]
    [Arguments("{\"secret\":do-not-print-me}")]
    [Arguments("{\"\\uD800\":\"do-not-print-me\"}")]
    public async Task InvalidInputDiagnosticsDoNotEchoInput(string json)
    {
        var directory = Directory.CreateTempSubdirectory("dotnet-preflight-");
        try
        {
            var path = Path.Combine(directory.FullName, "do-not-print-me");
            await File.WriteAllTextAsync(path, json);
            using var output = new StringWriter();
            using var error = new StringWriter();
            await Assert.That(await Cli.RunAsync(["run", "--workload", path, "--region", "gru", "--out", path + ".jsonl"], output, error, () => "ci-host")).IsEqualTo(2);
            await Assert.That(error.ToString().Contains("do-not-print-me", StringComparison.Ordinal)).IsFalse();
        }
        finally { directory.Delete(recursive: true); }
    }

    [Test]
    public async Task OversizedFileIsRejectedBeforeParsing()
    {
        var directory = Directory.CreateTempSubdirectory("dotnet-preflight-");
        try
        {
            var path = Path.Combine(directory.FullName, "large.json");
            await File.WriteAllTextAsync(path, new string(' ', Workload.MaxFileBytes + 1));
            await Assert.That(async () => await Workload.ReadAsync(path)).Throws<PreflightException>();
        }
        finally { directory.Delete(recursive: true); }
    }

    [Test]
    public async Task OfficialSdkGeneratesPlainUlidsAndResolvesRegionalHttps()
    {
        var generator = new TerraScaleIdGenerator();
        var first = generator.NewId();
        var second = generator.NewId();
        await Assert.That(TerraScaleIds.IsValid(first)).IsTrue();
        await Assert.That(TerraScaleIds.IsValid(second)).IsTrue();
        await Assert.That(string.CompareOrdinal(first, second) < 0).IsTrue();
        var resolver = new TerraBaseStaticEndpointResolver(new Uri("https://gru.api.terrascale.tech"));
        await Assert.That((await resolver.ResolveAsync()).Host).IsEqualTo("gru.api.terrascale.tech");
        await Assert.That(SdkPrerequisites.Version).IsEqualTo(typeof(TerraBaseDocumentsClient).Assembly.GetName().Version!.ToString());
    }

    private static Workload Parse(string json) => Workload.Parse(Encoding.UTF8.GetBytes(json));

    private static string RampJson(int duration, int step, string concurrency)
    {
        var value = JsonNode.Parse(Valid)!;
        value["name"] = "ramp";
        value["concurrency"] = 1;
        value["duration_s"] = duration;
        value["ramp"] = JsonNode.Parse($"{{\"step_s\":{step},\"concurrency\":{concurrency}}}");
        return value.ToJsonString();
    }
}
