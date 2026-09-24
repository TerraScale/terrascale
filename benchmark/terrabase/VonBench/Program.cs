using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using TerraBase.Indexing.Automatic;
using TerraBase.Indexing.Automatic.Jev;

if (args.Length is < 2 or > 3 || !int.TryParse(args[0], out var serverPid))
{
    Console.Error.WriteLine("Usage: VonBench <von-server-pid> <report.md> [--warm]");
    return 2;
}
var warmModel = args.Length == 3 && args[2] == "--warm";

var output = Path.GetFullPath(args[1]);
using var server = Process.GetProcessById(serverPid);
using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var options = new JevAdvisorOptions
{
    Provider = "von", ModelVersion = "von-1.2.0",
    Endpoint = new Uri("http://127.0.0.1:8000/v1/systemone"),
    InputPricePerMillionTokensUsd = 0,
    Deadline = TimeSpan.FromSeconds(30),
};
using var advisor = new JevAutoIndexAdvisor(new JevSystemOneClient(http, options),
    new JevTenantApprovalRegistry(), new JevApiCredentials(() => null), options);

server.Refresh();
var idleRss = server.WorkingSet64;
var initialCpu = server.TotalProcessorTime;
long peakRss = idleRss;
using var sampling = new CancellationTokenSource();
var sampler = Task.Run(async () =>
{
    while (!sampling.IsCancellationRequested)
    {
        server.Refresh();
        peakRss = Math.Max(peakRss, server.WorkingSet64);
        try { await Task.Delay(20, sampling.Token); }
        catch (OperationCanceledException) { break; }
    }
});

var rows = new List<(string Phase, double WallMs, double CpuMs, long RssBytes, string Provider, string Reason)>();
for (var index = 0; index < 11; index++)
{
    if (index > 0) await Task.Delay(1100);
    var candidates = Candidates(index);
    server.Refresh();
    var beforeCpu = server.TotalProcessorTime;
    var watch = Stopwatch.StartNew();
    var advice = await advisor.AdviseAsync(candidates);
    watch.Stop();
    server.Refresh();
    rows.Add((index == 0 ? (warmModel ? "first warm call" : "cold model load") : "steady", watch.Elapsed.TotalMilliseconds,
        (server.TotalProcessorTime - beforeCpu).TotalMilliseconds, server.WorkingSet64,
        advice.Provider, advice.ReasonCode));
}

sampling.Cancel();
await sampler;
server.Refresh();
var finalRss = server.WorkingSet64;
var totalCpu = (server.TotalProcessorTime - initialCpu).TotalMilliseconds;
var steady = rows.Skip(1).ToArray();
var sorted = steady.Select(row => row.WallMs).Order().ToArray();
var report = new StringBuilder();
report.AppendLine("# Von local index advisor resource benchmark");
report.AppendLine();
report.AppendLine(CultureInfo.InvariantCulture, $"Date: {DateTimeOffset.UtcNow:O}; Von source: wfzyx/von 946d4eb95071982ec0b67f8c762759c04c4080ff; model: von-1.2.0; server PID: {serverPid}; requests: 1 {(warmModel ? "warm" : "cold")} + 10 steady; cadence: 1.1 s; OMP_NUM_THREADS=4; device=CPU. Each request has two candidate fields and five questions. Each request uses a different schema version to avoid advisor cache hits.");
report.AppendLine();
report.AppendLine("| Measure | Value |");
report.AppendLine("| --- | ---: |");
report.AppendLine(CultureInfo.InvariantCulture, $"| Idle server RSS | {idleRss / 1048576d:F1} MiB |");
report.AppendLine(CultureInfo.InvariantCulture, $"| Final server RSS | {finalRss / 1048576d:F1} MiB |");
report.AppendLine(CultureInfo.InvariantCulture, $"| Sampled peak server RSS | {peakRss / 1048576d:F1} MiB |");
report.AppendLine(CultureInfo.InvariantCulture, $"| Server CPU time during run | {totalCpu:F1} ms |");
report.AppendLine(CultureInfo.InvariantCulture, $"| First call wall / server CPU | {rows[0].WallMs:F1} / {rows[0].CpuMs:F1} ms |");
report.AppendLine(CultureInfo.InvariantCulture, $"| Steady wall p50 / p95 | {sorted[4]:F1} / {sorted[9]:F1} ms |");
report.AppendLine(CultureInfo.InvariantCulture, $"| Steady server CPU mean | {steady.Average(row => row.CpuMs):F1} ms/call |");
report.AppendLine(CultureInfo.InvariantCulture, $"| Von advice / fallback calls | {rows.Count(row => row.Provider == "von")} / {rows.Count(row => row.Provider != "von")} |");
report.AppendLine();
report.AppendLine("| Call | Phase | Wall ms | Server CPU ms | Server RSS MiB | Advice | Reason |");
report.AppendLine("| ---: | --- | ---: | ---: | ---: | --- | --- |");
for (var index = 0; index < rows.Count; index++)
{
    var row = rows[index];
    report.AppendLine(CultureInfo.InvariantCulture,
        $"| {index + 1} | {row.Phase} | {row.WallMs:F1} | {row.CpuMs:F1} | {row.RssBytes / 1048576d:F1} | {row.Provider} | {row.Reason} |");
}
report.AppendLine();
report.AppendLine("CPU is cumulative process CPU time, so a 4-thread inference can consume more CPU milliseconds than wall milliseconds. RSS is the server process working set; the peak is sampled every 20 ms and may miss a shorter spike. This measures the current CPU host and this two-candidate advisor request, not throughput of the full database.");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, report.ToString());
Console.WriteLine(report);
return 0;

static IndexCandidateSet Candidates(int version)
{
    var scope = new AutomaticIndexScope("bench-tenant", "bench-database", "bench-collection", (ulong)(version + 1));
    var fields = ImmutableArray.Create(
        new FieldIndexProfile("customer_lookup", ProfileValueKinds.Text, 16, 0, 8, false, 2, 96, 0, false),
        new FieldIndexProfile("opaque_payload", ProfileValueKinds.Text, 16, 0, 16, false, 1, 128, 0, false));
    var profile = new CollectionIndexProfile(scope, 16, 16, true, false, fields);
    return new IndexCandidateSet(profile, new QueryIndependentIndexPolicy { MaximumIndexes = 2 },
        fields.Select((field, index) => new IndexCandidateEvidence(new AutomaticIndexCandidate(scope, [field.Path]),
            field, index == 0 ? 0.9 : 0.5, "sample-scalar-coverage")).ToImmutableArray());
}
