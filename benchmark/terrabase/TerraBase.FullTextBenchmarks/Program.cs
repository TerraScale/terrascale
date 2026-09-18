using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TerraBase.Indexing.FullText;
using TerraBase;
using TerraBase.StorageEngine;
using DurableStorageEngine = TerraBase.StorageEngine.StorageEngine;

var options = BenchmarkOptions.Parse(args);
var benchmarkAssembly = Assembly.GetExecutingAssembly();
var engineAssembly = typeof(DurableStorageEngine).Assembly;
var zoneTreeAssembly = typeof(ZoneTree.FullTextSearch.SearchEngines.HashedSearchEngine<>).Assembly;
var environment = new
{
    Runtime = Environment.Version.ToString(),
    Framework = RuntimeInformation.FrameworkDescription,
    OS = RuntimeInformation.OSDescription,
    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    Processors = Environment.ProcessorCount,
    AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
    ServerGc = GCSettings.IsServerGC,
    GcLatencyMode = GCSettings.LatencyMode.ToString(),
    BenchmarkAssembly = benchmarkAssembly.Location,
    BenchmarkAssemblySha256 = Sha256(benchmarkAssembly.Location),
    EngineAssembly = engineAssembly.Location,
    EngineAssemblySha256 = Sha256(engineAssembly.Location),
    ZoneTreeAssembly = zoneTreeAssembly.Location,
    ZoneTreeAssemblySha256 = Sha256(zoneTreeAssembly.Location),
    MicrosoftDataSqliteAssemblySha256 = Sha256(typeof(SqliteConnection).Assembly.Location),
};

Console.WriteLine(JsonSerializer.Serialize(environment));
Console.WriteLine(
    $"configuration records={options.Records} tokens={options.Tokens} samples={options.Samples} " +
    $"warmup={options.Warmup} operations={options.Operations} write_batch={options.WriteBatch} " +
    $"write_operations={options.WriteOperations} rebuild_operations={options.RebuildOperations} seed={options.Seed}");

// Letter-only vocabulary: the default WordTokenizer treats digits as
// separators, so numeric suffixes would collapse into one token while
// SQLite FTS5 keeps them distinct. The query pair comes from one document
// so the AND lane has a non-empty intersection.
var corpus = CreateCorpus(options);
var firstDocumentTokens = corpus[0].Split(' ');
var common = firstDocumentTokens[0];
var partner = firstDocumentTokens[^1];
var root = Path.Combine(Path.GetTempPath(), $"terrabase-fulltext-bench-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    await using var storage = new DurableStorageEngine(Path.Combine(root, "storage"));
    var scope = new FullTextIndexScope("fts-bench-tenant", "fts-bench-database", "docs");
    var source = new StorageEngineFullTextSource(storage, scope.Database);
    var keys = await SeedSourceAsync(storage, scope, corpus);

    await RunRebuildLaneAsync(root, scope, source, corpus, options);

    using (var index = await OpenBackfilledIndexAsync(Path.Combine(root, "index"), scope, source))
    using (var sqlite = await OpenSeededSqliteAsync(Path.Combine(root, "sqlite.db"), corpus))
    {
        foreach (var (name, query) in SearchLanes(common, partner))
        {
            var terrabaseHits = index.Search(query).Documents.Count;
            var sqliteHits = CountSqliteMatches(sqlite, query);
            Console.WriteLine(
                $"lane={name} query='{query}' terrabase_hits={terrabaseHits} sqlite_hits={sqliteHits}");
            if (terrabaseHits != sqliteHits)
            {
                throw new InvalidOperationException(
                    $"Lane '{name}' result mismatch: terrabase {terrabaseHits} vs sqlite {sqliteHits}.");
            }

            var terrabaseMeasurement = Measure(options, () => index.Search(query));
            var sqliteMeasurement = Measure(options, () => CountSqliteMatches(sqlite, query));
            Report(name, terrabaseMeasurement, sqliteMeasurement);
        }
    }

    await RunWriteLaneAsync(root, storage, scope, source, keys, corpus, options);
}
finally
{
    TryDelete(root);
}

static string Sha256(string path) =>
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

static string VocabularyToken(int index)
{
    // Deterministic letter-only token: wordaaa, wordaab, ... wordzzz.
    var suffix = new char[3];
    var value = index;
    for (var i = 2; i >= 0; i--)
    {
        suffix[i] = (char)('a' + value % 26);
        value /= 26;
    }

    return $"word{new string(suffix)}";
}

static string[] CreateCorpus(BenchmarkOptions options)
{
    var random = new Random(options.Seed);
    var vocabulary = options.Records / 4 + 1;
    var texts = new string[options.Records];
    for (var i = 0; i < texts.Length; i++)
    {
        var tokens = new string[options.Tokens];
        for (var token = 0; token < tokens.Length; token++)
        {
            tokens[token] = VocabularyToken(random.Next(0, vocabulary));
        }

        texts[i] = string.Join(' ', tokens);
    }

    return texts;
}

static async Task<List<DocumentKey>> SeedSourceAsync(
    DurableStorageEngine storage,
    FullTextIndexScope scope,
    string[] corpus)
{
    var keys = new List<DocumentKey>(corpus.Length);
    var mutations = new StorageMutation[corpus.Length];
    for (var i = 0; i < corpus.Length; i++)
    {
        var key = new DocumentKey(
            new TenantId(scope.Tenant.Value),
            new CollectionId(scope.Collection.Value),
            $"doc-{i:D8}");
        keys.Add(key);
        mutations[i] = StorageMutation.Upsert(key, Body($"{{\"text\":\"{corpus[i]}\"}}"), DocumentPrecondition.Any);
    }

    var batch = await storage.ApplyBatchAsync(mutations);
    if (batch.Outcome != StorageBatchOutcome.Committed)
    {
        throw new InvalidOperationException($"The benchmark source seed failed: {batch.Outcome}.");
    }

    return keys;
}

static async ValueTask<DurableFullTextIndex> OpenBackfilledIndexAsync(
    string path,
    FullTextIndexScope scope,
    IFullTextChangeFeedSource source)
{
    TryDelete(path);
    var index = new DurableFullTextIndex(path, scope, ["text"]);
    await index.BackfillAsync(source);
    return index;
}

static async ValueTask<SqliteConnection> OpenSeededSqliteAsync(string path, IReadOnlyList<string> corpus)
{
    TryDelete(path);
    TryDelete(path + "-wal");
    TryDelete(path + "-shm");
    var connection = new SqliteConnection($"Data Source={path};Pooling=False");
    await connection.OpenAsync();
    using (var setup = connection.CreateCommand())
    {
        setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        await setup.ExecuteNonQueryAsync();
    }

    using (var create = connection.CreateCommand())
    {
        create.CommandText = "CREATE VIRTUAL TABLE fts USING fts5(text)";
        await create.ExecuteNonQueryAsync();
    }

    await InsertSqliteRowsAsync(connection, corpus, 0, corpus.Count);
    return connection;
}

static async Task InsertSqliteRowsAsync(
    SqliteConnection connection,
    IReadOnlyList<string> texts,
    int offset,
    int count)
{
    using var transaction = connection.BeginTransaction();
    using var insert = connection.CreateCommand();
    insert.CommandText = "INSERT INTO fts(text) VALUES ($text)";
    var textParameter = insert.CreateParameter();
    textParameter.ParameterName = "$text";
    insert.Parameters.Add(textParameter);
    for (var i = 0; i < count; i++)
    {
        textParameter.Value = texts[(offset + i) % texts.Count];
        await insert.ExecuteNonQueryAsync();
    }

    await transaction.CommitAsync();
}

static int CountSqliteMatches(SqliteConnection connection, string query)
{
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT text FROM fts WHERE fts MATCH $query";
    var queryParameter = command.CreateParameter();
    queryParameter.ParameterName = "$query";
    queryParameter.Value = query;
    command.Parameters.Add(queryParameter);
    using var reader = command.ExecuteReader();
    var rows = 0;
    var characters = 0L;
    while (reader.Read())
    {
        rows++;
        characters += reader.GetString(0).Length;
    }

    if (characters < 0)
    {
        throw new InvalidOperationException("The sqlite lane must consume its result rows.");
    }

    return rows;
}

static IEnumerable<(string Name, string Query)> SearchLanes(string common, string partner)
{
    yield return ("single", common);
    yield return ("and", $"{common} {partner}");
    yield return ("or", $"{common} OR {partner}");
    yield return ("not", $"{common} NOT {partner}");
}

static async Task RunWriteLaneAsync(
    string root,
    DurableStorageEngine storage,
    FullTextIndexScope scope,
    IFullTextChangeFeedSource source,
    IReadOnlyList<DocumentKey> keys,
    string[] corpus,
    BenchmarkOptions options)
{
    using var index = await OpenBackfilledIndexAsync(Path.Combine(root, "index-write"), scope, source);
    using var sqlite = await OpenSeededSqliteAsync(Path.Combine(root, "sqlite-write.db"), corpus);
    var random = new Random(options.Seed ^ 0x5eed);
    var terrabaseDocuments = 0L;
    var sqliteDocuments = 0L;

    // One durable operation consumes exactly WriteBatch freshly committed
    // publications through one catch-up, mirroring the feed pump boundary;
    // the source commits happen outside the measured window.
    async Task TerrabaseWriteAsync()
    {
        for (var i = 0; i < options.WriteBatch; i++)
        {
            var document = terrabaseDocuments++;
            var key = keys[(int)(document % keys.Count)];
            await storage.UpsertAsync(
                key,
                Body($"{{\"text\":\"{corpus[random.Next(corpus.Length)]}\"}}"),
                DocumentPrecondition.Any);
        }

        await index.CatchUpAsync(source);
        if (index.DocumentCount != corpus.Length)
        {
            throw new InvalidOperationException("The write lane lost or gained documents.");
        }
    }

    async Task SqliteWriteAsync()
    {
        var document = sqliteDocuments;
        sqliteDocuments += options.WriteBatch;
        await InsertSqliteRowsAsync(sqlite, corpus, (int)(document % corpus.Length), options.WriteBatch);
    }

    var terrabaseWrite = await MeasureAsync(warmup: 1, options.WriteOperations, options.Samples, TerrabaseWriteAsync);
    var sqliteWrite = await MeasureAsync(warmup: 1, options.WriteOperations, options.Samples, SqliteWriteAsync);
    Report("write", terrabaseWrite, sqliteWrite);
}

static async Task RunRebuildLaneAsync(
    string root,
    FullTextIndexScope scope,
    IFullTextChangeFeedSource source,
    string[] corpus,
    BenchmarkOptions options)
{
    var indexPath = Path.Combine(root, "index-rebuild");

    async Task TerrabaseRebuildAsync()
    {
        using var index = await OpenBackfilledIndexAsync(indexPath, scope, source);
        if (index.DocumentCount != corpus.Length)
        {
            throw new InvalidOperationException("The rebuild lane lost documents.");
        }
    }

    async Task SqliteRebuildAsync()
    {
        using var sqlite = await OpenSeededSqliteAsync(Path.Combine(root, "sqlite-rebuild.db"), corpus);
    }

    var terrabaseRebuild = await MeasureAsync(warmup: 1, options.RebuildOperations, options.Samples, TerrabaseRebuildAsync);
    var sqliteRebuild = await MeasureAsync(warmup: 1, options.RebuildOperations, options.Samples, SqliteRebuildAsync);
    Report("rebuild", terrabaseRebuild, sqliteRebuild);
}

static Measurement Measure(BenchmarkOptions options, Action operation)
{
    for (var i = 0; i < options.Warmup; i++)
    {
        operation();
    }

    var samples = new List<Sample>(options.Samples);
    for (var sample = 0; sample < options.Samples; sample++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timings = new double[options.Operations];
        var stopwatch = Stopwatch.StartNew();
        for (var operationIndex = 0; operationIndex < options.Operations; operationIndex++)
        {
            var started = Stopwatch.GetTimestamp();
            operation();
            timings[operationIndex] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        }

        stopwatch.Stop();
        samples.Add(new Sample(
            stopwatch.Elapsed.TotalMilliseconds,
            Percentile(timings, 0.50),
            Percentile(timings, 0.95),
            Percentile(timings, 0.99),
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));
    }

    return Finish(samples, options.Operations);
}

static async Task<Measurement> MeasureAsync(int warmup, int operations, int samples, Func<Task> operation)
{
    for (var i = 0; i < warmup; i++)
    {
        await operation();
    }

    var collected = new List<Sample>(samples);
    for (var sample = 0; sample < samples; sample++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timings = new double[operations];
        var stopwatch = Stopwatch.StartNew();
        for (var operationIndex = 0; operationIndex < operations; operationIndex++)
        {
            var started = Stopwatch.GetTimestamp();
            await operation();
            timings[operationIndex] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        }

        stopwatch.Stop();
        collected.Add(new Sample(
            stopwatch.Elapsed.TotalMilliseconds,
            Percentile(timings, 0.50),
            Percentile(timings, 0.95),
            Percentile(timings, 0.99),
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));
    }

    return Finish(collected, operations);
}

static Measurement Finish(List<Sample> samples, int operations) =>
    new(
        Median(samples.Select(static sample => sample.ElapsedMilliseconds)),
        Median(samples.Select(static sample => sample.P50Microseconds)),
        Median(samples.Select(static sample => sample.P95Microseconds)),
        Median(samples.Select(static sample => sample.P99Microseconds)),
        Median(samples.Select(sample => sample.AllocatedBytes / (double)operations)));

static void Report(string lane, Measurement terrabase, Measurement sqlite)
{
    Console.WriteLine(
        $"lane={lane} engine=terrabase p50_us={terrabase.P50Microseconds:F3} p95_us={terrabase.P95Microseconds:F3} " +
        $"p99_us={terrabase.P99Microseconds:F3} allocated_bytes_op={terrabase.AllocatedBytesPerOperation:F1}");
    Console.WriteLine(
        $"lane={lane} engine=sqlite p50_us={sqlite.P50Microseconds:F3} p95_us={sqlite.P95Microseconds:F3} " +
        $"p99_us={sqlite.P99Microseconds:F3} allocated_bytes_op={sqlite.AllocatedBytesPerOperation:F1}");
    Console.WriteLine(
        $"lane={lane} ratio_p50_terrabase_over_sqlite={terrabase.P50Microseconds / Math.Max(sqlite.P50Microseconds, 0.001):F3} " +
        $"ratio_p50_sqlite_over_terrabase={sqlite.P50Microseconds / Math.Max(terrabase.P50Microseconds, 0.001):F3}");
}

static JsonElement Body(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}

static double Percentile(double[] values, double percentile)
{
    Array.Sort(values);
    var position = (values.Length - 1) * percentile;
    var lower = (int)Math.Floor(position);
    var upper = (int)Math.Ceiling(position);
    return values[lower] + (values[upper] - values[lower]) * (position - lower);
}

static double Median(IEnumerable<double> values)
{
    var ordered = values.Order().ToArray();
    return Percentile(ordered, 0.5);
}

static void TryDelete(string path)
{
    try
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch (IOException)
    {
    }
}

file sealed record Sample(
    double ElapsedMilliseconds,
    double P50Microseconds,
    double P95Microseconds,
    double P99Microseconds,
    long AllocatedBytes);

file sealed record Measurement(
    double ElapsedMilliseconds,
    double P50Microseconds,
    double P95Microseconds,
    double P99Microseconds,
    double AllocatedBytesPerOperation);

file sealed record BenchmarkOptions(
    int Records,
    int Tokens,
    int Samples,
    int Warmup,
    int Operations,
    int WriteBatch,
    int WriteOperations,
    int RebuildOperations,
    int Seed)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        var options = new BenchmarkOptions(2_000, 12, 5, 100, 200, 32, 3, 2, 1337);
        for (var index = 0; index < args.Length; index++)
        {
            var (name, value) = ReadOption(args, ref index);
            var parsed = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            options = name switch
            {
                "records" => options with { Records = parsed },
                "tokens" => options with { Tokens = parsed },
                "samples" => options with { Samples = parsed },
                "warmup" => options with { Warmup = parsed },
                "operations" => options with { Operations = parsed },
                "write-batch" => options with { WriteBatch = parsed },
                "write-operations" => options with { WriteOperations = parsed },
                "rebuild-operations" => options with { RebuildOperations = parsed },
                "seed" => options with { Seed = parsed },
                _ => throw new ArgumentException($"Unknown option '--{name}'."),
            };
        }

        if (options.Records < 1 || options.Tokens < 1 || options.Samples < 5 || options.Warmup < 0
            || options.Operations < 1 || options.WriteBatch < 1 || options.WriteOperations < 1
            || options.RebuildOperations < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "records/tokens/operations/write-batch/write-operations/rebuild-operations must be positive, " +
                "samples must be at least five, and warmup cannot be negative.");
        }

        return options;
    }

    private static (string Name, string Value) ReadOption(string[] args, ref int index)
    {
        var argument = args[index];
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Expected an option, got '{argument}'.");
        }

        var separator = argument.IndexOf('=');
        if (separator > 2)
        {
            return (argument[2..separator], argument[(separator + 1)..]);
        }

        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Option '{argument}' requires a value.");
        }

        return (argument[2..], args[++index]);
    }
}
