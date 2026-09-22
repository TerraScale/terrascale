using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Indexing;
using TerraBase.Indexing.Automatic;
using TerraBase.Indexing.Runtime;
using TerraBase.Storage.ChangeFeed;
using DurableStorageEngine = TerraBase.StorageEngine.StorageEngine;

namespace TerraBase.Benchmarks;

/// <summary>Same-snapshot comparison of atomic materialization and durable batched construction.</summary>
internal static class IndexSnapshotBuildWorkload
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] EvidenceArtifacts = ["snapshot-build-raw.json", "snapshot-build.md"];

    public static async Task<int> RunAsync(string[] args)
    {
        var count = int.Parse(Option(args, "--documents") ?? "2048", CultureInfo.InvariantCulture);
        var trials = int.Parse(Option(args, "--trials") ?? "5", CultureInfo.InvariantCulture);
        if (count is < 16 or > 100_000 || trials is < 3 or > 30) throw new ArgumentException("Require 16..100000 documents and 3..30 trials.");
        var output = Path.GetFullPath(Option(args, "--output") ?? "bench/snapshot-build");
        Directory.CreateDirectory(output);
        var root = Path.Combine(Path.GetTempPath(), "terrabase-snapshot-bench-" + Guid.NewGuid().ToString("N"));
        var scope = new AutomaticIndexScope("snapshot-tenant", "snapshot-db", "items");
        var candidate = new AutomaticIndexCandidate(scope, ["value"], requiresDescriptor: false);
        var limits = new IndexSnapshotBuildLimits { MaximumDocuments = 128, MaximumEncodedBytes = 1024 * 1024 };
        var rows = new List<Trial>();
        var assemblies = new[]
        {
            typeof(DurableIndexStore).Assembly.Location,
            typeof(ZoneTree.ITransactionalWriteBatch<,>).Assembly.Location,
            typeof(IndexSnapshotBuildWorkload).Assembly.Location,
        }.Select(path => new
        {
            File = Path.GetFileName(path),
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
        }).ToArray();
        double snapshotMilliseconds;
        try
        {
            await using var source = new DurableStorageEngine(Path.Combine(root, "source"));
            for (var i = 0; i < count; i++)
                await source.UpsertAsync(new DocumentKey(new TenantId(scope.Tenant), new CollectionId(scope.Collection), i.ToString("D8", CultureInfo.InvariantCulture)),
                    JsonSerializer.SerializeToElement(new { value = i % 16, padding = new string('x', 1024) }), DocumentPrecondition.Missing).ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            await using var snapshot = await source.BeginChangeFeedSnapshotAsync().ConfigureAwait(false);
            snapshotMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            for (var trial = -1; trial < trials; trial++)
                for (var lane = 0; lane < 2; lane++)
                {
                    var batched = (Math.Abs(trial) + lane) % 2 == 0;
                    var result = await MeasureAsync(root, source, snapshot, candidate, limits, count, trial, batched).ConfigureAwait(false);
                    if (trial >= 0) rows.Add(result);
                }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "snapshot-build-raw.json"), JsonSerializer.Serialize(new
        {
            Format = "terrabase-snapshot-build-v3", IndexWalMode = "Sync", Documents = count, Trials = trials, WarmupRounds = 1, Limits = limits,
            SnapshotMilliseconds = snapshotMilliseconds, Rows = rows,
        }, JsonOptions)).ConfigureAwait(false);
        var report = new StringBuilder("# Snapshot construction comparison\n\n");
        report.AppendLine(CultureInfo.InvariantCulture, $"Same immutable snapshot, {count} documents (1 KiB padding each), {trials} alternating trials per lane after one warmup. Source snapshot capture: {snapshotMilliseconds:F3} ms, excluded from both lanes.");
        report.AppendLine();
        report.AppendLine("| Construction | Median ms | p95 ms | Median allocated bytes | Median sampled peak RSS bytes | Median sampled peak managed bytes | Median index bytes | Spool bytes | Rows verified |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var group in rows.GroupBy(row => row.Lane))
        {
            var ordered = group.OrderBy(row => row.Milliseconds).ToArray();
            report.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {ordered[trials / 2].Milliseconds:F3} | {ordered[(int)Math.Ceiling(trials * 0.95) - 1].Milliseconds:F3} | {group.Select(row => row.AllocatedBytes).Order().ElementAt(trials / 2)} | {group.Select(row => row.PeakRssBytes).Order().ElementAt(trials / 2)} | {group.Select(row => row.PeakManagedBytes).Order().ElementAt(trials / 2)} | {group.Select(row => row.IndexBytes).Order().ElementAt(trials / 2)} | {group.First().SpoolBytes} | {count} |");
        }
        report.AppendLine();
        report.AppendLine("Both lanes use synchronous index WALs. Atomic materialization synchronizes each append, including transaction-control records. Durable batches synchronize bounded history, write-stamp, data and commit phases; it does not flush every row individually. The earlier AsyncCompressed run lost acknowledged checkpoints under SIGKILL and is a historical baseline, not equivalent durability evidence.");
        report.AppendLine();
        report.AppendLine("Both lanes create the same physical definition, source watermark and query results. Every built source document and entry is verified against the snapshot outside the timed section. Ingestion and initial empty-generation creation are excluded. Atomic materialization includes scan, collection buffering and one transaction. Durable batches include ordered capture, SHA-256 verification, file/directory sync, bounded transactions and durable cursors. These different durability/recovery costs are intentional and disclosed.");
        report.AppendLine();
        report.AppendLine("The batched lane limits pages to 128 documents and 1 MiB of charged encoded rows; it may halve pages for entry expansion. The source snapshot still retains collection-wide metadata. RSS/managed peaks are absolute observations sampled every 2 ms in a shared warm process, not isolated process peaks; they include source storage and prior runtime/cache state. Allocation deltas include the sampler and process background work. Disk sizes are immediate logical file lengths, not compacted physical allocation. This small local run does not establish larger-than-memory behavior or a general performance advantage. Raw trials and the build/source/host provenance are retained beside this report.");
        await File.WriteAllTextAsync(Path.Combine(output, "snapshot-build.md"), report.ToString()).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "snapshot-build-receipt.json"), JsonSerializer.Serialize(new
        {
            Format = "terrabase-snapshot-build-evidence-v1", Assemblies = assemblies,
            Artifacts = EvidenceArtifacts.Select(name => new
            {
                File = name,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, name)))),
            }),
        }, JsonOptions)).ConfigureAwait(false);
        Console.Write(report);
        return 0;
    }

    private static async Task<Trial> MeasureAsync(string root, DurableStorageEngine source, ChangeFeedSnapshot snapshot,
        AutomaticIndexCandidate candidate, IndexSnapshotBuildLimits limits, int count, int trial, bool batched)
    {
        var path = Path.Combine(root, $"{trial}-{batched}");
        await using var store = new DurableIndexStore(path, IndexDefinition.Covering("by-value", ["value"], []));
        await store.BuildInvisibleAsync(candidate, snapshot.Cursor.CommitSequence).ConfigureAwait(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        using var stop = new CancellationTokenSource();
        long peakRss = 0, peakManaged = 0;
        Sample();
        var sampler = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(2, stop.Token).ConfigureAwait(false);
                    Sample();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        double milliseconds;
        long allocation;
        try
        {
            if (batched)
            {
                await using var spool = await AutomaticIndexSnapshotSpool.CaptureAsync(store, source, snapshot, source.OriginUlid.ToString(),
                    candidate, limits, CancellationToken.None).ConfigureAwait(false);
                await spool.BuildAsync().ConfigureAwait(false);
            }
            else
            {
                await using var pump = new AutomaticIndexFeedPump(store, source, new TenantId(candidate.Scope.Tenant), new CollectionId(candidate.Scope.Collection));
                await pump.RebuildFromSnapshotAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
                await store.BackfillAsync(candidate.CandidateId, 1, snapshot.Cursor.CommitSequence).ConfigureAwait(false);
            }
            milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocation = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await sampler.ConfigureAwait(false);
            Sample();
        }
        var proof = await store.VerifyContentAsync(source, snapshot, new TenantId(candidate.Scope.Tenant), new CollectionId(candidate.Scope.Collection)).ConfigureAwait(false);
        if (proof.Documents != count) throw new InvalidDataException("Snapshot build lost documents.");
        var query = await store.QueryAsync(new IndexRuntimeQuery(new TenantId(candidate.Scope.Tenant), new CollectionId(candidate.Scope.Collection), [IndexPredicate.Equal("value", 0)])).ConfigureAwait(false);
        var expected = Enumerable.Range(0, count).Where(i => i % 16 == 0).Select(i => i.ToString("D8", CultureInfo.InvariantCulture));
        if (!query.Documents.Select(document => document.Key.DocumentId).Order(StringComparer.Ordinal).SequenceEqual(expected))
            throw new InvalidDataException("Snapshot query differs from the source.");
        var spoolBytes = Directory.EnumerateFiles(path, "*.spool").Sum(file => new FileInfo(file).Length);
        var indexBytes = Directory.EnumerateFiles(store.BackendPath, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
        return new Trial(trial, batched ? "durable-batches" : "atomic-materialization", milliseconds, allocation, peakRss, peakManaged, indexBytes, spoolBytes);

        void Sample()
        {
            process.Refresh();
            peakRss = Math.Max(peakRss, process.WorkingSet64);
            peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(forceFullCollection: false));
        }
    }

    private static string? Option(string[] args, string name) => args.FirstOrDefault(argument => argument.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
    private sealed record Trial(int Number, string Lane, double Milliseconds, long AllocatedBytes, long PeakRssBytes, long PeakManagedBytes, long IndexBytes, long SpoolBytes);
}
