using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Indexing.Automatic;
using TerraBase.Storage.ChangeFeed;
using DurableStorageEngine = TerraBase.StorageEngine.StorageEngine;

namespace TerraBase.Benchmarks;

/// <summary>Equal sample limits for prefix versus stratified profiling, without query history.</summary>
internal static class IndexProfileSamplingWorkload
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        var documents = int.Parse(Option(args, "--documents") ?? "4096", CultureInfo.InvariantCulture);
        var maximum = int.Parse(Option(args, "--sample") ?? "256", CultureInfo.InvariantCulture);
        var trials = int.Parse(Option(args, "--trials") ?? "9", CultureInfo.InvariantCulture);
        if (documents is < 8 or > 100_000 || maximum < 4 || maximum > Math.Min(documents / 2, 10_000)
            || trials is < 3 or > 100)
            throw new ArgumentException("Require 8..100000 documents, 4..min(documents/2,10000) sample documents, and 3..100 trials.");
        var output = Path.GetFullPath(Option(args, "--output") ?? Path.Combine("bench", "profile-sampling-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(output);
        var root = Path.Combine(Path.GetTempPath(), "terrabase-profile-bench-" + Guid.NewGuid().ToString("N"));
        var scope = new AutomaticIndexScope("sampling-tenant", "sampling-db", "items");
        var rows = new List<Trial>();
        double snapshotMilliseconds;
        try
        {
            await using var source = new DurableStorageEngine(root);
            for (var index = 0; index < documents; index++)
            {
                var field = index < documents / 2 ? "early" : "later";
                await source.UpsertAsync(new DocumentKey(new TenantId(scope.Tenant), new CollectionId(scope.Collection),
                    index.ToString("D8", CultureInfo.InvariantCulture)),
                    JsonSerializer.SerializeToElement(new Dictionary<string, int> { [field] = index }),
                    DocumentPrecondition.Missing).ConfigureAwait(false);
            }
            var started = Stopwatch.GetTimestamp();
            await using var snapshot = await source.BeginSnapshotAsync().ConfigureAwait(false);
            snapshotMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            IChangeFeedSnapshotReader[] readers = [new PrefixReader(source), source];
            for (var warmup = 0; warmup < 2; warmup++)
                foreach (var reader in readers) _ = await MeasureAsync(reader, snapshot, scope, maximum, -1).ConfigureAwait(false);
            for (var trial = 0; trial < trials; trial++)
                for (var strategy = 0; strategy < readers.Length; strategy++)
                    rows.Add(await MeasureAsync(readers[(trial + strategy) % readers.Length], snapshot, scope, maximum, trial).ConfigureAwait(false));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        var receipt = new
        {
            Format = "terrabase-index-profile-sampling-v1", CreatedAt = DateTimeOffset.UtcNow,
            Documents = documents, MaximumSampleDocuments = maximum, Trials = trials, WarmupRounds = 2,
            Scope = scope, Dataset = "Ordered eight-digit decimal IDs; first floor(N/2) rows have early=i, the remainder later=i.",
            SnapshotMilliseconds = snapshotMilliseconds,
            SubjectAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CollectionIndexProfiler).Assembly.Location))),
            Rows = rows,
        };
        await File.WriteAllTextAsync(Path.Combine(output, "profile-sampling-receipt.json"), JsonSerializer.Serialize(receipt, JsonOptions)).ConfigureAwait(false);
        var markdown = new StringBuilder();
        markdown.AppendLine("# Index-profile sampling comparison");
        markdown.AppendLine();
        markdown.AppendLine(CultureInfo.InvariantCulture, $"Documents: {documents}; sample limit: {maximum}; trials per strategy: {trials}; warmup rounds: 2; snapshot capture: {snapshotMilliseconds:F3} ms.");
        markdown.AppendLine();
        markdown.AppendLine("Each strategy receives the same immutable snapshot and sample limit. IDs encode two field populations: the first half has `early`, the second `later`. Selection receives no queries or workload observations. Trials alternate strategy order; filesystem/JIT state is warm.");
        markdown.AppendLine();
        markdown.AppendLine("| Sampling method | Median profile + candidates ms | p95 ms | Median allocated bytes | Sampled documents | Candidate paths | Later-field observations |");
        markdown.AppendLine("| --- | ---: | ---: | ---: | ---: | --- | ---: |");
        foreach (var group in rows.GroupBy(row => row.Method).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var elapsed = group.Select(row => row.Milliseconds).Order().ToArray();
            var allocated = group.Select(row => row.AllocatedBytes).Order().ToArray();
            var first = group.First();
            markdown.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {elapsed[elapsed.Length / 2]:F3} | {elapsed[(int)Math.Ceiling(elapsed.Length * 0.95) - 1]:F3} | {allocated[allocated.Length / 2]} | {first.SampledDocuments} | {string.Join(", ", first.CandidatePaths)} | {first.LaterDocuments} |");
        }
        markdown.AppendLine();
        markdown.AppendLine("Timing includes profile aggregation and deterministic candidate generation, excluding ingestion and snapshot capture (reported separately). Allocations are process-wide deltas and exclude the snapshot's retained metadata/body artifact. They are not peak-memory measurements. Raw trials and the subject assembly digest are in `profile-sampling-receipt.json`; command, repository state, runtime and host are in `run-receipt.json`.");
        markdown.AppendLine();
        markdown.AppendLine("This constructed dataset checks ordinal-prefix bias under equal sample limits. It does not establish future query benefit, population-wide type guarantees, Jev benefit, SQLite superiority, or larger-than-memory behavior. Snapshot capture still sorts and retains collection-wide key metadata.");
        await File.WriteAllTextAsync(Path.Combine(output, "profile-sampling.md"), markdown.ToString()).ConfigureAwait(false);
        Console.Write(markdown);
        return rows.All(row => row.SampledDocuments == maximum
            && (row.Method == nameof(IndexProfileSamplingMethod.OrdinalPrefixV1)
                ? row.CandidatePaths.SequenceEqual(["early"]) && row.LaterDocuments == 0
                : row.CandidatePaths.SequenceEqual(["early", "later"]) && row.LaterDocuments > 0)) ? 0 : 1;
    }

    private static async Task<Trial> MeasureAsync(IChangeFeedSnapshotReader source, StorageSnapshot snapshot,
        AutomaticIndexScope scope, int maximum, int trial)
    {
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var profile = await new CollectionIndexProfiler(new IndexProfileOptions { MaximumDocuments = maximum })
            .ProfileAsync(source, snapshot, scope).ConfigureAwait(false);
        var candidates = await new SchemaStatisticsCandidateSource().GetCandidatesAsync(profile,
            new QueryIndependentIndexPolicy { MaximumIndexes = 2 }).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        return new Trial(trial, profile.SamplingMethod.ToString(), elapsed, bytes, profile.SampledDocuments,
            candidates.Candidates.Select(candidate => candidate.Field.Path).Order(StringComparer.Ordinal).ToArray(),
            profile.Fields.FirstOrDefault(field => field.Path == "later")?.PresentDocuments ?? 0);
    }

    private static string? Option(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(name + "=", StringComparison.Ordinal)) return args[index][(name.Length + 1)..];
            if (args[index] == name && index + 1 < args.Length) return args[index + 1];
        }
        return null;
    }

    private sealed class PrefixReader(IChangeFeedSnapshotReader source) : IChangeFeedSnapshotReader
    {
        public IAsyncEnumerable<Document> ScanSnapshotAsync(StorageSnapshot snapshot, TenantId tenant, CollectionId collection,
            string? after, int limit, CancellationToken cancellationToken = default) =>
            source.ScanSnapshotAsync(snapshot, tenant, collection, after, limit, cancellationToken);
    }

    private sealed record Trial(int Iteration, string Method, double Milliseconds, long AllocatedBytes,
        int SampledDocuments, string[] CandidatePaths, int LaterDocuments);
}
