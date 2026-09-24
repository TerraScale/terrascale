using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Identity;
using TerraBase.Indexing;
using TerraBase.Indexing.Runtime;
using TerraBase.Storage.Contracts;

internal static class Program
{
    private const string ActivitySourceName = "TerraBase.Indexing.Query";
    private const int DefaultBaseDocuments = 3_000;
    private const int DefaultMaintenanceDocuments = 100;
    private const int DefaultPayloadBytes = 98_304;
    private const int BatchSize = 25;
    private const int DefaultSamples = 10;
    private const string Tenant = "benchmark-tenant";
    private const string Collection = "users";
    private const string QueryValue = "s17";
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.UnixEpoch;

    public static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            WriteUsage();
            return 2;
        }

        var toolRoot = FindToolRoot();
        var sourceRoot = FindSourceRoot();
        if (toolRoot is null || sourceRoot is null)
        {
            Console.Error.WriteLine("Could not locate the benchmark folder and TerraBase source tree.");
            return 2;
        }

        var output = Path.GetFullPath(options.Output ?? Path.Combine(
            toolRoot,
            "run-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture)));
        if (!output.StartsWith(toolRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Directory.Exists(output)
            || File.Exists(output))
        {
            Console.Error.WriteLine("The output path must be a new child directory of the stage-trace benchmark folder.");
            return 2;
        }

        Directory.CreateDirectory(output);
        var dataPath = Path.Combine(output, "data", "terrabase-status");
        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        var config = options.Smoke
            ? new WorkloadConfig(1_000, 20, 4_096, options.Samples, Qualified: false)
            : new WorkloadConfig(DefaultBaseDocuments, DefaultMaintenanceDocuments, DefaultPayloadBytes, options.Samples, Qualified: true);

        try
        {
            var report = await RunAsync(sourceRoot, toolRoot, output, dataPath, config).ConfigureAwait(false);
            var jsonPath = Path.Combine(output, "stage-trace-receipt.json");
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine)
                .ConfigureAwait(false);
            var markdownPath = Path.Combine(output, "stage-trace.md");
            await File.WriteAllTextAsync(markdownPath, Render(report)).ConfigureAwait(false);
            Console.WriteLine(jsonPath);
            Console.WriteLine(markdownPath);
            return 0;
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "failure.txt"), exception + Environment.NewLine)
                .ConfigureAwait(false);
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, recursive: true);
        }
    }

    private static async Task<RunReceipt> RunAsync(
        string sourceRoot,
        string toolRoot,
        string output,
        string dataPath,
        WorkloadConfig config)
    {
        var definition = IndexDefinition.Equality("by-status", "status");
        var tenant = new TenantId(Tenant);
        var collection = new CollectionId(Collection);
        var sequence = await SeedAsync(dataPath, definition, tenant, collection, config).ConfigureAwait(false);
        var query = new IndexRuntimeQuery(
            tenant,
            collection,
            [new IndexPredicate("status", IndexPredicateOperator.Equal, QueryValue)],
            requiredCommitSequence: sequence,
            resultLimit: 100);
        var expectedIds = ExpectedIds(config);
        var pairs = new List<SamplePair>(config.Samples);
        var warmups = Math.Min(5, config.Samples);

        await using (var store = new DurableIndexStore(dataPath, definition))
        {
            if (store.Watermark.CommitSequence != sequence)
                throw new InvalidOperationException($"Reopen watermark {store.Watermark.CommitSequence} != {sequence}.");

            for (var index = 0; index < warmups; index++)
            {
                var warmup = await SampleAsync(store, query, expectedIds, config, traced: false).ConfigureAwait(false);
                Verify(warmup, expectedIds, config, "untraced warmup");
            }

            for (var index = 0; index < config.Samples; index++)
            {
                QuerySample plain;
                QuerySample traced;
                if ((index & 1) == 0)
                {
                    plain = await SampleAsync(store, query, expectedIds, config, traced: false).ConfigureAwait(false);
                    traced = await SampleAsync(store, query, expectedIds, config, traced: true).ConfigureAwait(false);
                }
                else
                {
                    traced = await SampleAsync(store, query, expectedIds, config, traced: true).ConfigureAwait(false);
                    plain = await SampleAsync(store, query, expectedIds, config, traced: false).ConfigureAwait(false);
                }

                Verify(plain, expectedIds, config, "plain sample");
                Verify(traced, expectedIds, config, "traced sample");
                pairs.Add(new SamplePair(index + 1, plain, traced, traced.WallMilliseconds - plain.WallMilliseconds));
            }
        }

        var subject = await CaptureSubjectAsync(sourceRoot, toolRoot).ConfigureAwait(false);
        var outputDirectoryBytes = DirectoryBytes(Path.Combine(output, "data"));
        return new RunReceipt(
            SchemaVersion: 1,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Status: "complete",
            ActivitySource: ActivitySourceName,
            Query: $"{QueryValue} = status",
            IndexDefinition: definition.Name,
            CommitSequence: sequence,
            Fixture: config,
            ResultCount: expectedIds.Length,
            ExpectedIdsSha256: Sha256(Encoding.UTF8.GetBytes(string.Join('\n', expectedIds) + "\n")),
            Pairs: pairs,
            Medians: Summarize(pairs),
            DataDirectoryBytesBeforeCleanup: outputDirectoryBytes,
            Subject: subject,
            Toolchain: CaptureToolchain());
    }

    private static async Task<ulong> SeedAsync(
        string dataPath,
        IndexDefinition definition,
        TenantId tenant,
        CollectionId collection,
        WorkloadConfig config)
    {
        await using var store = new DurableIndexStore(dataPath, definition);
        ulong sequence = 0;
        for (var start = 0; start < config.BaseDocuments; start += BatchSize)
        {
            var count = Math.Min(BatchSize, config.BaseDocuments - start);
            await ApplyFrameAsync(start, count, maintenance: false, version: 1).ConfigureAwait(false);
        }

        for (var start = 0; start < config.MaintenanceDocuments; start += BatchSize)
        {
            var count = Math.Min(BatchSize, config.MaintenanceDocuments - start);
            await ApplyFrameAsync(start, count, maintenance: true, version: 1).ConfigureAwait(false);
        }

        for (var start = 0; start < config.MaintenanceDocuments; start += BatchSize)
        {
            var count = Math.Min(BatchSize, config.MaintenanceDocuments - start);
            await ApplyFrameAsync(start, count, maintenance: true, version: 2).ConfigureAwait(false);
        }

        if (sequence != (ulong)(
                DivideRoundUp(config.BaseDocuments, BatchSize)
                + 2 * DivideRoundUp(config.MaintenanceDocuments, BatchSize)))
        {
            throw new InvalidOperationException("Seed transaction count is inconsistent with the fixture.");
        }

        return sequence;

        async Task ApplyFrameAsync(int start, int count, bool maintenance, ulong version)
        {
            var mutations = new IndexRuntimeMutation[count];
            for (var offset = 0; offset < count; offset++)
            {
                var index = start + offset;
                using var parsed = JsonDocument.Parse(DocumentJson(index, version == 2 ? 1 : 0, config.PayloadBytes));
                var key = new DocumentKey(tenant, collection, maintenance ? $"doc-maint-{index:D6}" : $"doc-{index:D6}");
                mutations[offset] = IndexRuntimeMutation.Upsert(
                    new Document(key, parsed.RootElement, version, Timestamp));
            }

            await store.ApplyFrameAsync(new IndexRuntimeFrame(++sequence, mutations)).ConfigureAwait(false);
        }
    }

    private static async Task<QuerySample> SampleAsync(
        DurableIndexStore store,
        IndexRuntimeQuery query,
        string[] expectedIds,
        WorkloadConfig config,
        bool traced)
    {
        QueryStageActivityListener? listener = traced ? new QueryStageActivityListener() : null;
        try
        {
            var started = Stopwatch.GetTimestamp();
            var result = await store.QueryAsync(query).ConfigureAwait(false);
            var stopped = Stopwatch.GetTimestamp();
            var wallMs = Stopwatch.GetElapsedTime(started, stopped).TotalMilliseconds;
            var stages = listener?.Capture();
            var sample = new QuerySample(
                WallMilliseconds: wallMs,
                Traced: traced,
                Access: result.Plan.Access.ToString(),
                EstimatedCandidates: result.Plan.EstimatedMatches,
                ActualMatches: result.Plan.ActualMatches,
                Rows: result.Documents.Select(document => new RowSample(
                    document.Key.DocumentId,
                    document.Version,
                    document.Body.GetProperty("status").GetString() ?? string.Empty,
                    document.Body.GetProperty("profile").GetProperty("segment").GetString() ?? string.Empty,
                    document.Body.GetProperty("value").GetInt32(),
                    document.Body.GetProperty("payload").GetString() ?? string.Empty)).ToArray(),
                StageTotals: stages?.Totals,
                Activities: stages?.Activities ?? []);
            Verify(sample, expectedIds, config, traced ? "traced query" : "plain query");
            return sample;
        }
        finally
        {
            listener?.Dispose();
        }
    }

    private static void Verify(QuerySample sample, string[] expectedIds, WorkloadConfig config, string phase)
    {
        var rows = sample.Rows;
        var actualIds = rows.Select(static row => row.Id).ToArray();
        if (!actualIds.SequenceEqual(expectedIds, StringComparer.Ordinal))
            throw new InvalidOperationException($"{phase}: exact ordered IDs differ.");
        if (sample.Access != IndexAccessMethod.EqualitySeek.ToString())
            throw new InvalidOperationException($"{phase}: expected EqualitySeek, got {sample.Access}.");
        if (sample.EstimatedCandidates != expectedIds.Length || sample.ActualMatches != expectedIds.Length)
            throw new InvalidOperationException($"{phase}: candidate/result counts differ from {expectedIds.Length}.");

        foreach (var row in rows)
        {
            if (row.Status != QueryValue
                || row.Segment != $"g{ParseIndex(row.Id) % 100:D2}"
                || row.Value != (ParseIndex(row.Id) + (row.Id.StartsWith("doc-maint-", StringComparison.Ordinal) ? 1 : 0)) % 5
                || row.Payload != Convert.ToBase64String(PayloadBytes(ParseIndex(row.Id), config.PayloadBytes))
                || row.Version != (row.Id.StartsWith("doc-maint-", StringComparison.Ordinal) ? 2UL : 1UL))
            {
                throw new InvalidOperationException($"{phase}: body or version mismatch for {row.Id}.");
            }
        }

        if (sample.Traced && sample.StageTotals is null)
            throw new InvalidOperationException($"{phase}: the query emitted no recognized stage instrumentation.");
    }

    private static string[] ExpectedIds(WorkloadConfig config) =>
        Enumerable.Range(0, config.BaseDocuments)
            .Where(static index => index % 100 == 17)
            .Select(static index => $"doc-{index:D6}")
            .Concat(Enumerable.Range(0, config.MaintenanceDocuments)
                .Where(static index => index % 100 == 17)
                .Select(static index => $"doc-maint-{index:D6}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string DocumentJson(int index, int valueOffset, int payloadBytes) =>
        $"{{\"status\":\"s{index % 100:D2}\",\"profile\":{{\"segment\":\"g{index % 100:D2}\"}},\"value\":{(index + valueOffset) % 5},\"payload\":\"{Convert.ToBase64String(PayloadBytes(index, payloadBytes))}\"}}";

    private static byte[] PayloadBytes(int index, int payloadBytes)
    {
        var bytes = new byte[payloadBytes];
        new Random(unchecked(1_337 + index * 7_919)).NextBytes(bytes);
        return bytes;
    }

    private static int ParseIndex(string id)
    {
        var prefixLength = id.StartsWith("doc-maint-", StringComparison.Ordinal) ? "doc-maint-".Length : "doc-".Length;
        return int.Parse(id.AsSpan(prefixLength), CultureInfo.InvariantCulture);
    }

    private static async Task<SubjectIdentity> CaptureSubjectAsync(string sourceRoot, string toolRoot)
    {
        var sourcePaths = new[]
        {
            "src/TerraBase/Indexing/Runtime/DurableIndexStore.cs",
            "src/TerraBase/Indexing/Runtime/IndexQueryDiagnostics.cs",
        };
        var sourceFiles = sourcePaths.Select(relative => new FileIdentity(
            relative,
            Sha256File(Path.Combine(sourceRoot, relative)))).ToArray();
        var harnessFiles = new[]
        {
            "Program.cs",
            "DurableIndexQueryStageTrace.csproj",
            "README.md",
        }.Select(relative => new FileIdentity(relative, Sha256File(Path.Combine(toolRoot, relative)))).ToArray();
        var commit = await GitAsync(sourceRoot, "rev-parse", "HEAD").ConfigureAwait(false);
        var dirty = await GitAsync(sourceRoot, "status", "--porcelain").ConfigureAwait(false);
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(static assembly => assembly.GetName().Name is "TerraBase" or "TerraBase.ZoneTree" or "ZoneTree")
            .Select(assembly =>
            {
                var location = assembly.Location;
                return new AssemblyIdentity(
                    assembly.GetName().Name ?? "unknown",
                    assembly.GetName().Version?.ToString() ?? "unknown",
                    location,
                    File.Exists(location) ? Sha256File(location) : "unavailable");
            })
            .OrderBy(static assembly => assembly.Name, StringComparer.Ordinal)
            .ToArray();
        return new SubjectIdentity(
            sourceRoot,
            commit.Trim(),
            dirty.Length > 0,
            dirty.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            sourceFiles,
            harnessFiles,
            assemblies);
    }

    private static ToolchainIdentity CaptureToolchain() => new(
        Environment.MachineName,
        Environment.OSVersion.ToString(),
        RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        Environment.ProcessorCount,
        Environment.Version.ToString(),
        AppContext.TargetFrameworkName ?? "unknown",
        Stopwatch.IsHighResolution,
        Stopwatch.Frequency);

    private static async Task<string> GitAsync(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start git.");
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }

    private static Summary Summarize(IReadOnlyList<SamplePair> pairs)
    {
        var plain = pairs.Select(static pair => pair.Plain.WallMilliseconds).ToArray();
        var traced = pairs.Select(static pair => pair.Traced.WallMilliseconds).ToArray();
        var overhead = pairs.Select(static pair => pair.ObservedTraceOverheadMilliseconds).ToArray();
        var stages = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var stage in StageTotals.RequiredStageNames)
        {
            var values = pairs.Select(pair => pair.Traced.StageTotals![stage]).ToArray();
            stages[stage] = Median(values);
        }

        return new Summary(
            PlainWallP50Milliseconds: Median(plain),
            TracedWallP50Milliseconds: Median(traced),
            PairedListenerOverheadP50Milliseconds: Median(overhead),
            PlainWallSamplesMilliseconds: plain,
            TracedWallSamplesMilliseconds: traced,
            PairedListenerOverheadSamplesMilliseconds: overhead,
            TracedStageP50Milliseconds: stages,
            StageSumP50Milliseconds: stages.Values.Sum());
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0)
            throw new InvalidOperationException("Cannot calculate a median from no samples.");
        Array.Sort(values);
        var middle = values.Length / 2;
        return (values.Length & 1) == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }

    private static string Render(RunReceipt receipt)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# DurableIndexStore full-body query stage trace");
        builder.AppendLine();
        builder.AppendLine($"Generated {receipt.GeneratedAtUtc:O}; mode={(receipt.Fixture.Qualified ? "full qualified fixture" : "smoke, unqualified")}; source commit `{receipt.Subject.Commit}`; ActivitySource `{receipt.ActivitySource}`.");
        builder.AppendLine($"Fixture: {receipt.Fixture.BaseDocuments} base + {receipt.Fixture.MaintenanceDocuments} maintenance documents, {receipt.Fixture.PayloadBytes:N0} payload bytes per document; index `{receipt.IndexDefinition}`; predicate `{receipt.Query}`; result count {receipt.ResultCount}.");
        builder.AppendLine();
        builder.AppendLine("| Measurement | p50 ms |");
        builder.AppendLine("| --- | ---: |");
        builder.AppendLine($"| Plain query wall time | {receipt.Medians.PlainWallP50Milliseconds:F3} |");
        builder.AppendLine($"| Traced query wall time | {receipt.Medians.TracedWallP50Milliseconds:F3} |");
        builder.AppendLine($"| Paired listener/instrumentation overhead estimate | {receipt.Medians.PairedListenerOverheadP50Milliseconds:F3} |");
        foreach (var (stage, elapsed) in receipt.Medians.TracedStageP50Milliseconds)
            builder.AppendLine($"| {stage} | {elapsed:F3} |");
        builder.AppendLine($"| Sum of traced stage p50 values | {receipt.Medians.StageSumP50Milliseconds:F3} |");
        builder.AppendLine();
        builder.AppendLine("Listener overhead is the median of paired traced-minus-plain wall-time samples and is reported separately; it is not subtracted from stage timings. Stage spans are aggregated per query. The raw JSON contains every pair and activity tags.");
        builder.AppendLine();
        builder.AppendLine("## Identity");
        builder.AppendLine();
        builder.AppendLine($"Working tree dirty: `{receipt.Subject.Dirty}`. Harness and product source hashes, loaded assembly identities, and toolchain details are in `stage-trace-receipt.json`.");
        return builder.ToString();
    }

    private static long DirectoryBytes(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(static file => new FileInfo(file).Length)
        : 0;

    private static string Sha256File(string path) => Sha256(File.ReadAllBytes(path));

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string? FindToolRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "DurableIndexQueryStageTrace.csproj")))
                    return current.FullName;
                current = current.Parent;
            }
        }

        return null;
    }

    private static string? FindSourceRoot()
    {
        var configured = Environment.GetEnvironmentVariable("TERRABASE_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)
            && File.Exists(Path.Combine(configured, "src/TerraBase/TerraBase.csproj")))
            return Path.GetFullPath(configured);

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, "terrabase");
                if (File.Exists(Path.Combine(candidate, "src/TerraBase/TerraBase.csproj")))
                    return candidate;
                current = current.Parent;
            }
        }

        return null;
    }

    private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;

    private static void WriteUsage() => Console.Error.WriteLine(
        "Usage: DurableIndexQueryStageTrace --trace-stages [--smoke] [--samples=N] [--output=run-name]\n"
        + "The qualified full-body fixture is the default; --smoke selects a smaller unqualified fixture.");

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private sealed record Options(bool Smoke, int Samples, string? Output)
    {
        internal static Options? Parse(string[] args)
        {
            if (!args.Contains("--trace-stages", StringComparer.Ordinal))
                return null;
            var smoke = args.Contains("--smoke", StringComparer.Ordinal);
            var sampleArg = args.FirstOrDefault(static argument => argument.StartsWith("--samples=", StringComparison.Ordinal));
            var samples = sampleArg is null ? DefaultSamples : int.Parse(sampleArg[10..], CultureInfo.InvariantCulture);
            if (samples is < 5 or > 100)
                throw new ArgumentOutOfRangeException(nameof(args), "Samples must be between 5 and 100.");
            var output = args.FirstOrDefault(static argument => argument.StartsWith("--output=", StringComparison.Ordinal))?[9..];
            var unknown = args.Where(static argument => argument != "--trace-stages" && argument != "--smoke"
                && !argument.StartsWith("--samples=", StringComparison.Ordinal)
                && !argument.StartsWith("--output=", StringComparison.Ordinal)).ToArray();
            if (unknown.Length > 0)
                throw new ArgumentException($"Unknown argument(s): {string.Join(", ", unknown)}.");
            return new Options(smoke, samples, output);
        }
    }

    private sealed record WorkloadConfig(
        int BaseDocuments,
        int MaintenanceDocuments,
        int PayloadBytes,
        int Samples,
        bool Qualified);

    private sealed record RunReceipt(
        int SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        string Status,
        string ActivitySource,
        string Query,
        string IndexDefinition,
        ulong CommitSequence,
        WorkloadConfig Fixture,
        int ResultCount,
        string ExpectedIdsSha256,
        IReadOnlyList<SamplePair> Pairs,
        Summary Medians,
        long DataDirectoryBytesBeforeCleanup,
        SubjectIdentity Subject,
        ToolchainIdentity Toolchain);

    private sealed record SamplePair(int Pair, QuerySample Plain, QuerySample Traced, double ObservedTraceOverheadMilliseconds);

    private sealed record QuerySample(
        double WallMilliseconds,
        bool Traced,
        string Access,
        int EstimatedCandidates,
        int ActualMatches,
        IReadOnlyList<RowSample> Rows,
        IReadOnlyDictionary<string, double>? StageTotals,
        IReadOnlyList<ActivitySample> Activities);

    private sealed record RowSample(string Id, ulong Version, string Status, string Segment, int Value, string Payload);

    private sealed record ActivitySample(
        string Name,
        string TraceId,
        double DurationMilliseconds,
        IReadOnlyDictionary<string, string> Tags);

    private sealed record Summary(
        double PlainWallP50Milliseconds,
        double TracedWallP50Milliseconds,
        double PairedListenerOverheadP50Milliseconds,
        IReadOnlyList<double> PlainWallSamplesMilliseconds,
        IReadOnlyList<double> TracedWallSamplesMilliseconds,
        IReadOnlyList<double> PairedListenerOverheadSamplesMilliseconds,
        IReadOnlyDictionary<string, double> TracedStageP50Milliseconds,
        double StageSumP50Milliseconds);

    private sealed record SubjectIdentity(
        string SourceRoot,
        string Commit,
        bool Dirty,
        IReadOnlyList<string> WorkingTreeChanges,
        IReadOnlyList<FileIdentity> SourceFiles,
        IReadOnlyList<FileIdentity> HarnessFiles,
        IReadOnlyList<AssemblyIdentity> LoadedAssemblies);

    private sealed record FileIdentity(string Path, string Sha256);

    private sealed record AssemblyIdentity(string Name, string Version, string Path, string Sha256);

    private sealed record ToolchainIdentity(
        string MachineName,
        string OsVersion,
        string OsDescription,
        string ProcessArchitecture,
        int ProcessorCount,
        string RuntimeVersion,
        string TargetFramework,
        bool StopwatchHighResolution,
        long StopwatchFrequency);

    private sealed record QueryStageSummary(IReadOnlyDictionary<string, double> Totals);

    private static class StageTotals
    {
        internal static IReadOnlyList<string> RequiredStageNames { get; } =
        [
            "entry_seek",
            "committed_document_read",
            "decode_clone",
            "predicate_recheck",
            "sort",
        ];
    }

    private sealed class QueryStageActivityListener : IDisposable
    {
        private readonly ConcurrentQueue<ActivitySample> activities = new();
        private readonly ActivityListener listener;
        private bool captured;

        internal QueryStageActivityListener()
        {
            listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    var tags = activity.TagObjects.ToDictionary(
                        static tag => tag.Key,
                        static tag => tag.Value?.ToString() ?? string.Empty,
                        StringComparer.Ordinal);
                    activities.Enqueue(new ActivitySample(
                        activity.DisplayName,
                        activity.TraceId.ToString(),
                        activity.Duration.TotalMilliseconds,
                        tags));
                },
            };
            ActivitySource.AddActivityListener(listener);
        }

        internal QueryStageSummary Capture()
        {
            captured = true;
            var observed = activities.ToArray();
            var totals = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var stage in StageTotals.RequiredStageNames)
            {
                if (TryReadAggregateTag(observed, stage, out var value))
                {
                    totals[stage] = value;
                    continue;
                }

                var duration = observed
                    .Where(activity => ActivityMatchesStage(activity.Name, stage))
                    .Sum(static activity => activity.DurationMilliseconds);
                if (duration <= 0)
                    throw new InvalidOperationException($"No ActivitySource duration or aggregate tag found for stage `{stage}`.");
                totals[stage] = duration;
            }

            return new QueryStageSummary(totals);
        }

        private static bool TryReadAggregateTag(IReadOnlyList<ActivitySample> observed, string stage, out double milliseconds)
        {
            var aliases = stage switch
            {
                "entry_seek" => new[] { "entry_seek_ms", "entry_seek_milliseconds", "entry_seek_ticks" },
                "committed_document_read" => new[] { "committed_document_read_ms", "committed_document_read_milliseconds", "committed_document_read_ticks" },
                "decode_clone" => new[] { "decode_clone_ms", "decode_clone_milliseconds", "decode_clone_ticks" },
                "predicate_recheck" => new[] { "predicate_recheck_ms", "predicate_recheck_milliseconds", "predicate_recheck_ticks" },
                "sort" => new[] { "sort_ms", "sort_milliseconds", "sort_ticks" },
                _ => [],
            };
            foreach (var activity in observed)
            {
                foreach (var tag in activity.Tags)
                {
                    if (!aliases.Any(alias => tag.Key.EndsWith(alias, StringComparison.Ordinal)))
                        continue;
                    if (!double.TryParse(tag.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        continue;
                    milliseconds = tag.Key.EndsWith("_ticks", StringComparison.Ordinal)
                        ? parsed * 1_000d / Stopwatch.Frequency
                        : parsed;
                    return true;
                }
            }

            milliseconds = 0;
            return false;
        }

        private static bool ActivityMatchesStage(string name, string stage) => stage switch
        {
            "entry_seek" => name.Contains("entry_seek", StringComparison.Ordinal),
            "committed_document_read" => name.Contains("committed_document_read", StringComparison.Ordinal),
            "decode_clone" => name.Contains("decode_clone", StringComparison.Ordinal),
            "predicate_recheck" => name.Contains("predicate_recheck", StringComparison.Ordinal),
            "sort" => name.EndsWith(".sort", StringComparison.Ordinal) || name.Contains("query.sort", StringComparison.Ordinal),
            _ => false,
        };

        public void Dispose()
        {
            if (captured)
            {
                listener.Dispose();
                return;
            }

            listener.Dispose();
        }
    }
}
