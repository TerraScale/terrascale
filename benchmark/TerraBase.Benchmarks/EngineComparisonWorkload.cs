using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase;
using TerraBase.Api;
using TerraBase.Indexing;
using TerraBase.Indexing.Automatic;
using TerraBase.Security;
using TerraBase.Storage.DatabaseBinding;
using TerraBase.StorageEngine;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace TerraBase.Benchmarks;

/// <summary>
/// Compares the complete TerraBase HTTP host flow (PUT, point GET, bounded query)
/// against embedded reference databases (Microsoft.Data.Sqlite, LiteDB, and the
/// Turso .NET SQLite-compatible provider) under one shared lane contract, one
/// process, and one resource envelope.
/// </summary>
/// <remarks>
/// The measured boundaries are deliberately different and are recorded per case:
/// TerraBase is measured through its public HTTP document API against a real
/// in-process host, while every comparator is an embedded in-process engine.
/// All sides are C# on the same .NET runtime so the language is not a
/// differentiator. The lane contract mirrors the historical SE-15 comparator:
/// seed documents with unique names, unique-key point reads, equality queries
/// on the secondary field, unique-id creates for writes, and a 50/30/20
/// read/write/query mixed split by operation index.
/// </remarks>
internal static partial class EngineComparisonWorkload
{
    private const string DatabaseRoute = "bench";
    private const string Collection = "users";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions RawJsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsRequested(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--workload=", StringComparison.OrdinalIgnoreCase)
                && argument["--workload=".Length..].Equals("engine-comparison", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (argument.Equals("--workload", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length
                && args[index + 1].Equals("engine-comparison", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        ComparisonOptions options;
        try
        {
            options = ComparisonOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid engine-comparison configuration: {exception.Message}");
            ComparisonOptions.PrintHelp();
            return 2;
        }

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var environment = ComparisonEnvironment.Create();
        var startedAtUtc = DateTimeOffset.UtcNow;
        if (options.ShowHelp)
        {
            ComparisonOptions.PrintHelp();
            return 0;
        }

        if (!string.Equals(environment.BuildConfiguration, "Release", StringComparison.OrdinalIgnoreCase))
        {
            await WriteReceiptAsync(
                InvalidReceipt(options, environment, startedAtUtc, "Benchmark execution requires a Release artifact."),
                outputDirectory).ConfigureAwait(false);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var matrix = FilterMatrix(options);
            var cases = new List<ComparisonCaseResult>();
            // Parity pairs run their samples interleaved (A,B,A,B) so machine
            // drift between engines cannot bias the ratio: each sample of one
            // engine is immediately followed by its paired sample of the other.
            var partners = new Dictionary<CaseDefinition, CaseDefinition>();
            foreach (var group in matrix.GroupBy(static definition => (definition.Lane, definition.Concurrency)))
            {
                var engine = group.FirstOrDefault(static definition => definition.Engine == "terrabase-engine");
                var sqlite = group.FirstOrDefault(static definition => definition.Engine == "sqlite");
                if (engine is not null && sqlite is not null)
                {
                    partners[engine] = sqlite;
                }
            }

            foreach (var definition in matrix)
            {
                if (partners.TryGetValue(definition, out var partner))
                {
                    Console.WriteLine($"Running {definition.Slug} + {partner.Slug} interleaved ({options.Samples} samples of {options.OperationsPerSample} operations)...");
                    cases.AddRange(await RunInterleavedPairAsync(
                        options, definition, partner, outputDirectory, cancellation.Token).ConfigureAwait(false));
                    partners.Remove(definition);
                    CollectBetweenCases();
                    continue;
                }

                if (partners.ContainsValue(definition))
                {
                    continue;
                }

                Console.WriteLine($"Running {definition.Slug} ({options.Samples} samples of {options.OperationsPerSample} operations)...");
                cases.Add(await RunCaseAsync(options, definition, outputDirectory, cancellation.Token).ConfigureAwait(false));
                CollectBetweenCases();
            }

            var status = cases.All(static result => result.Status == "complete") ? "complete" : "failed";
            var receipt = new EngineComparisonReceipt(
                status,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                BuildConfiguration(options, outputDirectory),
                environment,
                CreateComparators(),
                cases,
                Environment.CommandLine,
                outputDirectory);
            await WriteReceiptAsync(receipt, outputDirectory).ConfigureAwait(false);
            return status == "complete" ? 0 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static EngineComparisonReceipt InvalidReceipt(
        ComparisonOptions options,
        ComparisonEnvironment environment,
        DateTimeOffset startedAtUtc,
        string error) =>
        new(
            "invalid",
            startedAtUtc,
            DateTimeOffset.UtcNow,
            BuildConfiguration(options, Path.GetFullPath(options.OutputDirectory)),
            environment,
            CreateComparators(),
            [new ComparisonCaseResult
            {
                Engine = "runner",
                Lane = "runner",
                Concurrency = 0,
                Status = "invalid",
                Error = error,
                StartedAtUtc = startedAtUtc,
                FinishedAtUtc = DateTimeOffset.UtcNow,
            }],
            Environment.CommandLine,
            Path.GetFullPath(options.OutputDirectory));

    private static ComparisonConfiguration BuildConfiguration(ComparisonOptions options, string outputDirectory) =>
        new(
            Storage: "terrabase-http+sdk-hosts+sqlite/litedb/turso-embedded",
            OutputDirectory: outputDirectory,
            DatasetSize: options.DatasetSize,
            PayloadSize: options.PayloadSize,
            Samples: options.Samples,
            WarmupOperations: options.WarmupOperations,
            OperationsPerSample: options.OperationsPerSample,
            ConcurrencyLanes: [.. FilterMatrix(options).Select(static definition => definition.Concurrency).Distinct().OrderBy(static value => value)],
            Seed: options.Seed,
            Workloads: [.. Enum.GetNames<ComparisonLane>()],
            Temperatures: ["warm"]);

    private static CaseDefinition[] FilterMatrix(ComparisonOptions options) =>
        [.. CaseMatrix
            .Where(definition => options.Engines is null || options.Engines.Contains(definition.Engine, StringComparer.Ordinal))
            .Where(definition => options.Lanes is null || options.Lanes.Contains(definition.Lane.ToString().ToLowerInvariant(), StringComparer.Ordinal))];

    /// <summary>
    /// The benchmark host's socket path: kept under the system temporary
    /// directory because Unix socket paths are limited to 108 bytes and the
    /// receipt tree is deeper than that.
    /// </summary>
    internal static string UnixSocketPathFor(string storagePath)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(storagePath))))[..16].ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), $"terrabase-bench-{hash}.sock");
    }

    /// <summary>
    /// A handler that dials the benchmark host's Unix domain socket instead of
    /// TCP, keeping the loopback IP stack out of every measured operation.
    /// </summary>
    internal static SocketsHttpHandler CreateUnixSocketHandler(string storagePath)
    {
        var socketPath = UnixSocketPathFor(storagePath);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            // HTTP/2 over cleartext with prior knowledge: the server endpoint
            // is pinned to the Unix socket, so upgrade negotiation is moot.
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    private static async Task WriteReceiptAsync(EngineComparisonReceipt receipt, string outputDirectory)
    {
        var receiptPath = Path.Combine(outputDirectory, "engine-comparison-receipt.json");
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, JsonOptions)).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "engine-comparison-summary.md"),
            EngineComparisonMarkdown.Create(receipt, receiptPath)).ConfigureAwait(false);
        Console.WriteLine($"Receipt: {receiptPath}");
        Console.WriteLine($"Summary: {Path.Combine(outputDirectory, "engine-comparison-summary.md")}");
    }

    private enum ComparisonLane
    {
        Read,
        Query,
        Write,
        Mixed,
    }

    private sealed record CaseDefinition(string Engine, string? Synchronous, ComparisonLane Lane, int Concurrency)
    {
        public string Slug =>
            $"{Engine}{(Synchronous is null ? string.Empty : "-" + Synchronous.ToLowerInvariant())}-{Lane.ToString().ToLowerInvariant()}-c{Concurrency}";
    }

    private static readonly CaseDefinition[] CaseMatrix =
    [
        new("sqlite", "FULL", ComparisonLane.Read, 1),
        new("sqlite", "FULL", ComparisonLane.Query, 1),
        new("sqlite", "FULL", ComparisonLane.Write, 1),
        new("sqlite", "FULL", ComparisonLane.Mixed, 1),
        new("sqlite", "FULL", ComparisonLane.Read, 16),
        new("sqlite", "FULL", ComparisonLane.Write, 16),
        new("sqlite", "NORMAL", ComparisonLane.Write, 1),
        new("sqlite", "NORMAL", ComparisonLane.Mixed, 1),
        new("litedb", null, ComparisonLane.Read, 1),
        new("litedb", null, ComparisonLane.Query, 1),
        new("litedb", null, ComparisonLane.Write, 1),
        new("litedb", null, ComparisonLane.Mixed, 1),
        new("litedb", null, ComparisonLane.Read, 16),
        new("litedb", null, ComparisonLane.Write, 16),
        new("turso", null, ComparisonLane.Read, 1),
        new("turso", null, ComparisonLane.Query, 1),
        new("turso", null, ComparisonLane.Write, 1),
        new("turso", null, ComparisonLane.Mixed, 1),
        new("turso", null, ComparisonLane.Read, 16),
        new("turso", null, ComparisonLane.Write, 16),
        new("terrabase-engine", null, ComparisonLane.Read, 1),
        new("terrabase-engine", null, ComparisonLane.Query, 1),
        new("terrabase-engine", null, ComparisonLane.Write, 1),
        new("terrabase-engine", null, ComparisonLane.Mixed, 1),
        new("terrabase-engine", null, ComparisonLane.Read, 16),
        new("terrabase-engine", null, ComparisonLane.Write, 16),
        new("terrabase-http", null, ComparisonLane.Read, 1),
        new("terrabase-http", null, ComparisonLane.Query, 1),
        new("terrabase-http", null, ComparisonLane.Write, 1),
        new("terrabase-http", null, ComparisonLane.Mixed, 1),
        new("terrabase-http", null, ComparisonLane.Read, 16),
        new("terrabase-http", null, ComparisonLane.Write, 16),
#if TERRASCALE_SDK
        new("terrabase-sdk", null, ComparisonLane.Read, 1),
        new("terrabase-sdk", null, ComparisonLane.Query, 1),
        new("terrabase-sdk", null, ComparisonLane.Write, 1),
        new("terrabase-sdk", null, ComparisonLane.Mixed, 1),
        new("terrabase-sdk", null, ComparisonLane.Read, 16),
        new("terrabase-sdk", null, ComparisonLane.Write, 16),
        // Matched-defaults durability context: LiteDB's engine default does
        // not device-sync per commit; ASYNC rows hold TerraBase to the same
        // class (AsyncWal) the way sqlite NORMAL rows contextualize FULL.
        new("terrabase-sdk", "ASYNC", ComparisonLane.Read, 1),
        new("terrabase-sdk", "ASYNC", ComparisonLane.Query, 1),
        new("terrabase-sdk", "ASYNC", ComparisonLane.Write, 1),
        new("terrabase-sdk", "ASYNC", ComparisonLane.Mixed, 1),
        new("terrabase-sdk", "ASYNC", ComparisonLane.Read, 16),
        new("terrabase-sdk", "ASYNC", ComparisonLane.Write, 16),
#endif
    ];

    private sealed class CaseCounters
    {
        public long WriteSequence;
    }

    /// <summary>
    /// Reclaims case garbage between measurements (never during a sample) so one
    /// engine's large-object-heap churn cannot push a later case out of memory.
    /// </summary>
    private static void CollectBetweenCases()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private sealed class PreparedCase : IAsyncDisposable
    {
        public required IComparisonBackend Backend { get; init; }
        public required ComparisonCaseResult Result { get; init; }
        public required CaseCounters Counters { get; init; }
        public required string DataPath { get; init; }
        public required StreamWriter Raw { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Raw.DisposeAsync().ConfigureAwait(false);
            await Backend.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static ComparisonCaseResult NewCaseResult(CaseDefinition definition, string outputDirectory) =>
        new ComparisonCaseResult
        {
            Engine = definition.Engine,
            Synchronous = definition.Synchronous,
            Lane = definition.Lane.ToString().ToLowerInvariant(),
            Concurrency = definition.Concurrency,
            RawSamplesPath = Path.Combine(outputDirectory, $"raw-samples-{definition.Slug}.jsonl"),
            StartedAtUtc = DateTimeOffset.UtcNow,
        };

    private static void FailCase(ComparisonCaseResult result, string error)
    {
        if (result.Status == "complete")
        {
            return;
        }

        result.Status = "failed";
        result.Error = error;
    }

    /// <summary>
    /// Runs the two engines of a parity pair with per-sample interleaving so
    /// wall-clock drift and thermal state hit both engines sample-for-sample.
    /// </summary>
    private static async Task<List<ComparisonCaseResult>> RunInterleavedPairAsync(
        ComparisonOptions options,
        CaseDefinition first,
        CaseDefinition second,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var firstResult = NewCaseResult(first, outputDirectory);
        var secondResult = NewCaseResult(second, outputDirectory);
        PreparedCase? preparedFirst = null;
        PreparedCase? preparedSecond = null;
        try
        {
            preparedFirst = await PrepareCaseAsync(options, first, firstResult, outputDirectory, cancellationToken).ConfigureAwait(false);
            preparedSecond = await PrepareCaseAsync(options, second, secondResult, outputDirectory, cancellationToken).ConfigureAwait(false);
            for (var sampleIndex = 0; sampleIndex < options.Samples; sampleIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RunOneSampleAsync(preparedFirst, first, options, sampleIndex, cancellationToken).ConfigureAwait(false);
                await RunOneSampleAsync(preparedSecond, second, options, sampleIndex, cancellationToken).ConfigureAwait(false);
            }

            firstResult.Status = "complete";
            secondResult.Status = "complete";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FailCase(firstResult, "Cancelled by user.");
            FailCase(secondResult, "Cancelled by user.");
        }
        catch (Exception exception)
        {
            FailCase(firstResult, exception.ToString());
            FailCase(secondResult, exception.ToString());
        }
        finally
        {
            if (preparedFirst is not null)
            {
                await preparedFirst.DisposeAsync().ConfigureAwait(false);
            }

            if (preparedSecond is not null)
            {
                await preparedSecond.DisposeAsync().ConfigureAwait(false);
            }

            firstResult.FinishedAtUtc = DateTimeOffset.UtcNow;
            secondResult.FinishedAtUtc = DateTimeOffset.UtcNow;
        }

        return [firstResult, secondResult];
    }

    private static async Task<ComparisonCaseResult> RunCaseAsync(
        ComparisonOptions options,
        CaseDefinition definition,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var result = NewCaseResult(definition, outputDirectory);
        try
        {
            await using var prepared = await PrepareCaseAsync(
                options, definition, result, outputDirectory, cancellationToken).ConfigureAwait(false);
            for (var sampleIndex = 0; sampleIndex < options.Samples; sampleIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RunOneSampleAsync(prepared, definition, options, sampleIndex, cancellationToken).ConfigureAwait(false);
            }

            result.Status = "complete";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FailCase(result, "Cancelled by user.");
        }
        catch (Exception exception)
        {
            FailCase(result, exception.ToString());
        }

        result.FinishedAtUtc = DateTimeOffset.UtcNow;
        return result;
    }

    /// <summary>Opens, seeds, warms up, and arms the raw-sample writer for one case.</summary>
    private static async Task<PreparedCase> PrepareCaseAsync(
        ComparisonOptions options,
        CaseDefinition definition,
        ComparisonCaseResult result,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var dataPath = Path.Combine(outputDirectory, "data", definition.Slug);
        IComparisonBackend? backend = null;
        StreamWriter? raw = null;
        try
        {
            switch (definition.Engine)
            {
                case "terrabase-engine":
                    backend = await TerrabaseEngineBackend.OpenAsync(
                        dataPath, options.DatasetSize, options.PayloadSize, cancellationToken).ConfigureAwait(false);
                    break;
                case "terrabase-http":
                    backend = await TerrabaseHttpBackend.OpenAsync(
                        dataPath, options.DatasetSize, options.PayloadSize, cancellationToken).ConfigureAwait(false);
                    break;
#if TERRASCALE_SDK
                case "terrabase-sdk":
                    backend = await TerrabaseSdkBackend.OpenAsync(
                        dataPath, options.DatasetSize, options.PayloadSize,
                        durability: definition.Synchronous == "ASYNC"
                            ? StorageWalDurability.AsyncWal
                            : StorageWalDurability.Synced,
                        cancellationToken).ConfigureAwait(false);
                    break;
#endif
                case "sqlite":
                    backend = await AdoBackend.OpenMicrosoftSqliteAsync(
                        dataPath, definition.Synchronous ?? "FULL", definition.Concurrency, options.PayloadSize, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case "turso":
                    backend = await AdoBackend.OpenTursoAsync(
                        dataPath, definition.Concurrency, options.PayloadSize, cancellationToken).ConfigureAwait(false);
                    break;
                case "litedb":
                    backend = LiteDbBackend.Open(dataPath, options.PayloadSize);
                    break;
                default:
                    throw new ArgumentException($"Unknown comparison engine '{definition.Engine}'.");
            }

            for (var index = 0; index < options.DatasetSize; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await backend.SeedAsync(index, cancellationToken).ConfigureAwait(false);
            }

            if (backend is TerrabaseEngineBackend engine)
            {
                await engine.BuildNameIndexAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (backend is TerrabaseHttpBackend terrabase)
            {
                await TerrabaseHttpBackend.BuildNameIndexAsync(terrabase.App, cancellationToken).ConfigureAwait(false);
            }
#if TERRASCALE_SDK
            else if (backend is TerrabaseSdkBackend sdk)
            {
                await TerrabaseHttpBackend.BuildNameIndexAsync(sdk.App, cancellationToken).ConfigureAwait(false);
            }
#endif

            result.EngineNotes = backend.EngineNotes;
            result.WorkloadDigest = CreateDigest(options, definition);
            var counters = new CaseCounters();
            await RunOperationsAsync(
                backend, definition.Lane, definition.Concurrency, options, options.WarmupOperations, null, counters, 0, cancellationToken)
                .ConfigureAwait(false);
            if (definition.Lane is ComparisonLane.Query or ComparisonLane.Mixed)
            {
                result.QueryPlan = await backend.CaptureQueryPlanAsync(cancellationToken).ConfigureAwait(false);
            }

            raw = new StreamWriter(
                result.RawSamplesPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return new PreparedCase
            {
                Backend = backend,
                Result = result,
                Counters = counters,
                DataPath = dataPath,
                Raw = raw,
            };
        }
        catch
        {
            if (raw is not null)
            {
                await raw.DisposeAsync().ConfigureAwait(false);
            }

            if (backend is not null)
            {
                await backend.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task RunOneSampleAsync(
        PreparedCase prepared,
        CaseDefinition definition,
        ComparisonOptions options,
        int sampleIndex,
        CancellationToken cancellationToken)
    {
        var sample = await RunSampleAsync(
            prepared.Backend, definition, options, prepared.Counters, sampleIndex, prepared.DataPath, cancellationToken)
            .ConfigureAwait(false);
        prepared.Result.Samples.Add(sample);
        await prepared.Raw.WriteLineAsync(JsonSerializer.Serialize(sample, RawJsonOptions)).ConfigureAwait(false);
        await prepared.Raw.FlushAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine(
            $"  {definition.Engine} sample {sampleIndex + 1}/{options.Samples}: {sample.ThroughputOpsPerSecond.ToString("F1", CultureInfo.InvariantCulture)} ops/s");
    }

    private static string CreateDigest(ComparisonOptions options, CaseDefinition definition)
    {
        var text = string.Join("\n",
        [
            definition.Engine,
            definition.Synchronous ?? "-",
            definition.Lane.ToString(),
            definition.Concurrency.ToString(CultureInfo.InvariantCulture),
            options.DatasetSize.ToString(CultureInfo.InvariantCulture),
            options.PayloadSize.ToString(CultureInfo.InvariantCulture),
            options.Samples.ToString(CultureInfo.InvariantCulture),
            options.WarmupOperations.ToString(CultureInfo.InvariantCulture),
            options.OperationsPerSample.ToString(CultureInfo.InvariantCulture),
            options.Seed.ToString(CultureInfo.InvariantCulture),
            "terrabase-engine-comparison-v1",
        ]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static async Task<(long ReturnedBytes, long LogicalBytes)> RunOperationsAsync(
        IComparisonBackend backend,
        ComparisonLane lane,
        int concurrency,
        ComparisonOptions options,
        int operationCount,
        long[]? latencies,
        CaseCounters counters,
        int randomSalt,
        CancellationToken cancellationToken)
    {
        long returnedBytes = 0;
        long logicalBytes = 0;
        var workers = Enumerable.Range(0, concurrency)
            .Select(worker => Task.Run(async () =>
            {
                var random = new Random(unchecked(options.Seed + randomSalt * 7919 + worker * 104729));
                for (var index = worker; index < operationCount; index += concurrency)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = random.Next(options.DatasetSize);
                    var started = Stopwatch.GetTimestamp();
                    long returned;
                    long logical;
                    switch (lane)
                    {
                        case ComparisonLane.Read:
                            (returned, logical) = await backend.ReadAsync(key, worker, cancellationToken).ConfigureAwait(false);
                            break;
                        case ComparisonLane.Query:
                            (returned, logical) = await backend.QueryAsync(key, worker, cancellationToken).ConfigureAwait(false);
                            break;
                        case ComparisonLane.Write:
                            (returned, logical) = await backend
                                .WriteAsync(Interlocked.Increment(ref counters.WriteSequence), worker, cancellationToken)
                                .ConfigureAwait(false);
                            break;
                        default:
                            if (index % 10 < 5)
                            {
                                (returned, logical) = await backend.ReadAsync(key, worker, cancellationToken).ConfigureAwait(false);
                            }
                            else if (index % 10 < 8)
                            {
                                (returned, logical) = await backend
                                    .WriteAsync(Interlocked.Increment(ref counters.WriteSequence), worker, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                (returned, logical) = await backend.QueryAsync(key, worker, cancellationToken).ConfigureAwait(false);
                            }

                            break;
                    }

                    if (latencies is not null)
                    {
                        latencies[index] = Stopwatch.GetTimestamp() - started;
                    }

                    Interlocked.Add(ref returnedBytes, returned);
                    Interlocked.Add(ref logicalBytes, logical);
                }
            }, cancellationToken))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        return (returnedBytes, logicalBytes);
    }

    private static async Task<ComparisonSample> RunSampleAsync(
        IComparisonBackend backend,
        CaseDefinition definition,
        ComparisonOptions options,
        CaseCounters counters,
        int sampleIndex,
        string dataPath,
        CancellationToken cancellationToken)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var processCpuBefore = process.TotalProcessorTime;
        var workingSetBefore = process.WorkingSet64;
        var allocationsBefore = GC.GetTotalAllocatedBytes(false);
        var storageBefore = StorageBytes(dataPath);
        var latencies = new long[options.OperationsPerSample];
        var wallStart = Stopwatch.GetTimestamp();

        var (returnedBytes, logicalBytes) = await RunOperationsAsync(
            backend, definition.Lane, definition.Concurrency, options, options.OperationsPerSample, latencies, counters, sampleIndex + 1, cancellationToken)
            .ConfigureAwait(false);

        var elapsed = Stopwatch.GetElapsedTime(wallStart);
        process.Refresh();
        var processCpuAfter = process.TotalProcessorTime;
        var workingSetAfter = process.WorkingSet64;
        var allocationsAfter = GC.GetTotalAllocatedBytes(false);
        var storageAfter = StorageBytes(dataPath);
        Array.Sort(latencies);

        var bytesWritten = Math.Max(0, storageAfter - storageBefore);
        var cpuTime = processCpuAfter - processCpuBefore;
        var cpuPercent = elapsed.TotalSeconds <= 0
            ? 0
            : cpuTime.TotalSeconds / elapsed.TotalSeconds / Environment.ProcessorCount * 100;

        return new ComparisonSample
        {
            Sample = sampleIndex + 1,
            Operations = options.OperationsPerSample,
            Concurrency = definition.Concurrency,
            ElapsedMilliseconds = elapsed.TotalMilliseconds,
            ThroughputOpsPerSecond = options.OperationsPerSample / Math.Max(elapsed.TotalSeconds, double.Epsilon),
            P50Microseconds = ToMicroseconds(Percentile(latencies, 0.50)),
            P95Microseconds = ToMicroseconds(Percentile(latencies, 0.95)),
            P99Microseconds = ToMicroseconds(Percentile(latencies, 0.99)),
            AllocatedBytes = Math.Max(0, allocationsAfter - allocationsBefore),
            AllocatedBytesPerOperation = Math.Max(0, allocationsAfter - allocationsBefore) / (double)options.OperationsPerSample,
            ProcessCpuMilliseconds = cpuTime.TotalMilliseconds,
            ProcessCpuPercent = cpuPercent,
            ProcessWorkingSetBytesBefore = workingSetBefore,
            ProcessWorkingSetBytesAfter = workingSetAfter,
            ProcessWorkingSetDeltaBytes = workingSetAfter - workingSetBefore,
            LogicalBytes = logicalBytes,
            ReturnedBytes = returnedBytes,
            StorageBytesBefore = storageBefore,
            StorageBytesAfter = storageAfter,
            BytesWritten = bytesWritten,
            WriteAmplification = bytesWritten > 0 && logicalBytes > 0 ? bytesWritten / (double)logicalBytes : null,
        };
    }

    private static long StorageBytes(string dataPath)
    {
        if (!Directory.Exists(dataPath))
        {
            return 0;
        }

        return Directory.EnumerateFiles(dataPath, "*", SearchOption.AllDirectories)
            .Sum(static path => new FileInfo(path).Length);
    }

    private static long Percentile(long[] sortedTicks, double percentile)
    {
        var index = (int)Math.Ceiling(sortedTicks.Length * percentile) - 1;
        return sortedTicks[Math.Clamp(index, 0, sortedTicks.Length - 1)];
    }

    private static double ToMicroseconds(long stopwatchTicks) => stopwatchTicks * 1_000_000d / Stopwatch.Frequency;

    private interface IComparisonBackend : IAsyncDisposable
    {
        string Engine { get; }

        string? EngineNotes { get; }

        ValueTask SeedAsync(int index, CancellationToken cancellationToken);

        ValueTask<(long ReturnedBytes, long LogicalBytes)> ReadAsync(int index, int slot, CancellationToken cancellationToken);

        ValueTask<(long ReturnedBytes, long LogicalBytes)> QueryAsync(int index, int slot, CancellationToken cancellationToken);

        ValueTask<(long ReturnedBytes, long LogicalBytes)> WriteAsync(long sequence, int slot, CancellationToken cancellationToken);

        ValueTask<ComparisonQueryPlan?> CaptureQueryPlanAsync(CancellationToken cancellationToken);
    }

    private sealed class TerrabaseHttpBackend : IComparisonBackend
    {
        internal static readonly EntityId BenchmarkDatabaseUlid =
            EntityId.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV", CultureInfo.InvariantCulture);

        private readonly WebApplication app;
        private readonly HttpClient http;
        private readonly string payload;
        private readonly string[] queryBodies;

        private TerrabaseHttpBackend(WebApplication app, HttpClient http, string payload, string[] queryBodies)
        {
            this.app = app;
            this.http = http;
            this.payload = payload;
            this.queryBodies = queryBodies;
        }

        public string Engine => "terrabase-http";

        public WebApplication App => app;

        public string? EngineNotes => "durable StorageEngine backend; HTTP document API on an in-process host over a Unix domain socket; host log level raised to Warning so request logging is not measured; automatic index on the name field built and activated before warm-up";

        public static async ValueTask<TerrabaseHttpBackend> OpenAsync(
            string storagePath,
            int datasetSize,
            int payloadSize,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(storagePath);
            var payload = new string('x', payloadSize);
            var queryBodies = Enumerable.Range(0, datasetSize)
                .Select(index => SeedName(index))
                .Select(name => $"{{\"predicates\":[{{\"path\":\"name\",\"operator\":\"equal\",\"value\":\"{name}\"}}],\"projection\":[\"payload\"],\"limit\":10}}")
                .ToArray();
            var app = await CreateBenchmarkHostAsync(storagePath, cancellationToken).ConfigureAwait(false);
            try
            {
                var http = new HttpClient(CreateUnixSocketHandler(storagePath))
                {
                    BaseAddress = new Uri("http://127.0.0.1/"),
                    Timeout = TimeSpan.FromSeconds(30),
                };
                return new TerrabaseHttpBackend(app, http, payload, queryBodies);
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>Creates the shared benchmark host configuration used by every TerraBase lane.</summary>
        public static async Task<WebApplication> CreateBenchmarkHostAsync(string storagePath, CancellationToken cancellationToken)
        {
            // A Unix domain socket keeps the TCP/IP loopback stack out of every
            // measured round trip; the socket lives under a short temporary
            // path because Unix socket names are capped at 108 bytes and the
            // receipt tree is deeper than that.
            var socketPath = UnixSocketPathFor(storagePath);
            TryDeleteSocket(socketPath);
            var app = TerraBaseHost.Create(
                $"http://unix:{socketPath}",
                builder => ConfigureBenchmarkBuilder(builder, storagePath, StorageWalDurability.Synced));
            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                return app;
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// The same host configuration bound to the in-process embedded server,
        /// so the SDK lane measures the full pipeline without a transport.
        /// </summary>
        public static async Task<EmbeddedTerraBaseHost> CreateBenchmarkEmbeddedHostAsync(
            string storagePath,
            StorageWalDurability durability = StorageWalDurability.Synced,
            CancellationToken cancellationToken = default)
        {
            var embedded = TerraBaseHost.CreateEmbedded(
                builder => ConfigureBenchmarkBuilder(builder, storagePath, durability));
            try
            {
                await embedded.Application.StartAsync(cancellationToken).ConfigureAwait(false);
                return embedded;
            }
            catch
            {
                await embedded.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private static void ConfigureBenchmarkBuilder(
            WebApplicationBuilder builder,
            string storagePath,
            StorageWalDurability durability)
        {
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Configuration["TERRABASE_REGION"] = "GRU";
            builder.Configuration["TERRABASE_ALLOW_SCHEMALESS"] = "true";
            builder.Services.AddSingleton<ITenantAuthorizationHook, BenchmarkAllowAllAuthorizationHook>();
            builder.Services.AddSingleton<IStartupFilter, BenchmarkIdentityStartupFilter>();
            builder.Services.AddSingleton(new DatabaseBindingCatalog(
            [
                new DatabaseBindingDescriptor(
                    new DatabaseBindingKey(new TenantId("benchmark-tenant"), new DatabaseRouteId(DatabaseRoute)),
                    BenchmarkDatabaseUlid,
                    storagePath,
                    DatabaseStorageBackend.StorageEngine,
                    DatabaseBindingMode.Explicit,
                    Region.Gru,
                    new HashSet<Region> { Region.Gru },
                    durability),
            ]));
        }

        public async ValueTask SeedAsync(int index, CancellationToken cancellationToken)
        {
            var body = SeedBody(index, payload);
            using var request = CreateWriteRequest(SeedId(index), body);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureStatusAsync(response, HttpStatusCode.Created, $"seed {SeedId(index)}", cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Builds and activates the automatic index on the name field so the query lane
        /// measures an index seek, matching the secondary index every comparator creates,
        /// and so bounded queries stay inside the endpoint's row budget.
        /// </summary>
        public static async Task BuildNameIndexAsync(WebApplication host, CancellationToken cancellationToken)
        {
            var resolver = host.Services.GetRequiredService<IDatabaseBindingResolver>();
            var resolution = await resolver.ResolveAsync(new DatabaseBindingRequest(
                new SecurityPrincipal("engine-comparison"),
                new DatabaseRouteId(DatabaseRoute),
                BenchmarkDatabaseUlid,
                TenantCapability.QueryExecute,
                Collection), cancellationToken).ConfigureAwait(false);
            var binding = resolution.Binding
                ?? throw new InvalidOperationException($"The binding '{DatabaseRoute}' did not resolve for index build.");
            var runtime = await host.Services.GetRequiredService<DatabaseBindingEngineRegistry>()
                .GetQueryRuntimeAsync(binding, cancellationToken).ConfigureAwait(false);
            var scope = new AutomaticIndexScope("benchmark-tenant", BenchmarkDatabaseUlid.ToString(), Collection);
            var candidate = new AutomaticIndexCandidate(scope, ["name"]);
            await runtime.ApplyManualAsync(new ScoredIndexCandidate(candidate, new CandidateScore(100, 90, 5, 1)), cancellationToken).ConfigureAwait(false);
            await runtime.BuildAsync(candidate.CandidateId, cancellationToken).ConfigureAwait(false);
            var shadow = Enumerable.Repeat(new LogicalWork(100, 100, 200), AutomaticIndexShadowVerifier.MinimumSamples).ToArray();
            var indexed = Enumerable.Repeat(new LogicalWork(1, 1, 2), AutomaticIndexShadowVerifier.MinimumSamples).ToArray();
            var verified = await runtime.VerifyShadowAsync(candidate.CandidateId, shadow, indexed, null, cancellationToken).ConfigureAwait(false);
            if (verified.Record.Phase != AutomaticIndexPhase.Active)
            {
                throw new InvalidOperationException($"The benchmark name index did not activate ({verified.Record.Phase}).");
            }
        }

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> ReadAsync(int index, int slot, CancellationToken cancellationToken)
        {
            using var response = await http.GetAsync(DocumentPath(SeedId(index)), cancellationToken).ConfigureAwait(false);
            var body = await ReadBodyAsync(response, HttpStatusCode.OK, $"read {SeedId(index)}", cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.GetProperty("name").GetString() != SeedName(index))
            {
                throw new InvalidOperationException($"TerraBase point read returned the wrong document for {SeedId(index)}.");
            }

            var bytes = (long)Encoding.UTF8.GetByteCount(body);
            return (bytes, 0);
        }

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> QueryAsync(int index, int slot, CancellationToken cancellationToken)
        {
            using var content = new StringContent(queryBodies[index], Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(QueryPath(), content, cancellationToken).ConfigureAwait(false);
            var body = await ReadBodyAsync(response, HttpStatusCode.OK, $"query {SeedName(index)}", cancellationToken).ConfigureAwait(false);
            using var result = JsonDocument.Parse(body);
            var documents = result.RootElement.GetProperty("documents");
            if (documents.GetArrayLength() != 1 || documents[0].GetProperty("id").GetString() != SeedId(index))
            {
                throw new InvalidOperationException($"TerraBase query for {SeedName(index)} did not return exactly {SeedId(index)}.");
            }

            var bytes = (long)Encoding.UTF8.GetByteCount(body);
            return (bytes, 0);
        }

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> WriteAsync(long sequence, int slot, CancellationToken cancellationToken)
        {
            var id = WriteId(sequence);
            var name = WriteName(sequence);
            var body = $"{{\"name\":\"{name}\",\"group\":{sequence % 100},\"payload\":\"{payload}\"}}";
            using var request = CreateWriteRequest(id, body);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureStatusAsync(response, HttpStatusCode.Created, $"write {id}", cancellationToken).ConfigureAwait(false);
            return (0, (long)Encoding.UTF8.GetByteCount(body));
        }

        public async ValueTask<ComparisonQueryPlan?> CaptureQueryPlanAsync(CancellationToken cancellationToken)
        {
            using var content = new StringContent(queryBodies[0], Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(QueryPath(), content, cancellationToken).ConfigureAwait(false);
            var body = await ReadBodyAsync(response, HttpStatusCode.OK, "capture query plan", cancellationToken).ConfigureAwait(false);
            using var result = JsonDocument.Parse(body);
            var plan = result.RootElement.GetProperty("plan");
            var work = result.RootElement.GetProperty("work");
            return new ComparisonQueryPlan(
                Access: plan.TryGetProperty("access", out var access) && access.GetString() is { } accessName ? accessName : "unknown",
                IndexName: plan.TryGetProperty("indexName", out var indexName) ? indexName.GetString() : null,
                UsedSnapshotFallback: plan.TryGetProperty("usedSnapshotFallback", out var fallback) && fallback.GetBoolean(),
                Reason: plan.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
                RowsExamined: work.TryGetProperty("rowsExamined", out var rows) && rows.TryGetDouble(out var rowsValue) ? rowsValue : 0,
                WorkUnits: work.TryGetProperty("workUnits", out var units) && units.TryGetDouble(out var unitsValue) ? unitsValue : 0);
        }

        public async ValueTask DisposeAsync()
        {
            http.Dispose();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await app.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                await app.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static string DocumentPath(string id) => $"/api/v1/databases/{DatabaseRoute}/collections/{Collection}/documents/{id}";

        private static string QueryPath() => $"/api/v1/databases/{DatabaseRoute}/collections/{Collection}/documents/query";

        private static void TryDeleteSocket(string socketPath)
        {
            try
            {
                if (File.Exists(socketPath))
                {
                    File.Delete(socketPath);
                }
            }
            catch (IOException)
            {
                // A stale socket only matters if the bind then fails, which
                // surfaces as a normal host start error.
            }
        }

        internal static string SeedId(int index) => $"seed-{index:D5}";

        internal static string SeedName(int index) => $"user-{index}";

        internal static string WriteId(long sequence) => $"write-{sequence:D8}";

        internal static string WriteName(long sequence) => $"user-w{sequence}";

        internal static string SeedBody(int index, string payload) =>
            $"{{\"name\":\"{SeedName(index)}\",\"group\":{index % 100},\"payload\":\"{payload}\"}}";

        private static HttpRequestMessage CreateWriteRequest(string id, string body)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, DocumentPath(id))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            return request;
        }

        private static async Task<string> ReadBodyAsync(
            HttpResponseMessage response, HttpStatusCode expected, string operation, CancellationToken cancellationToken)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != expected)
            {
                throw new InvalidOperationException(
                    $"TerraBase {operation} returned {(int)response.StatusCode} ({response.StatusCode}); body: {Truncate(body)}.");
            }

            return body;
        }

        private static async Task EnsureStatusAsync(
            HttpResponseMessage response, HttpStatusCode expected, string operation, CancellationToken cancellationToken)
        {
            if (response.StatusCode != expected)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"TerraBase {operation} returned {(int)response.StatusCode} ({response.StatusCode}); body: {Truncate(body)}.");
            }
        }

        private static string Truncate(string body) => body.Length <= 512 ? body : body[..512];
    }

    private sealed class BenchmarkAllowAllAuthorizationHook : ITenantAuthorizationHook
    {
        public ValueTask<TenantAuthorizationDecision> AuthorizeAsync(
            TenantAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TenantAuthorizationDecision(true, DateTimeOffset.UtcNow.AddSeconds(10)));
    }

    private sealed class BenchmarkIdentityStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (context, continuation) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "engine-comparison")],
                    "engine-comparison"));
                await continuation().ConfigureAwait(false);
            });
            next(application);
        };
    }

    /// <summary>
    /// One ADO.NET backend for both SQLite-compatible providers: Microsoft.Data.Sqlite
    /// and the Turso SQLite-compatible provider. Connections use one serialized writer
    /// plus per-slot readers with cached prepared statements and autocommit semantics.
    /// </summary>
    private sealed class AdoBackend : IComparisonBackend
    {
        private const string Tenant = "benchmark-tenant";

        private readonly DbConnection writer;
        private readonly DbCommand writerInsert;
        private readonly SemaphoreSlim writerLock = new(1, 1);
        private readonly DbConnection[] readers;
        private readonly DbCommand[] readCommands;
        private readonly DbCommand[] queryCommands;
        private readonly string payload;
        private readonly bool strictPragmas;

        private AdoBackend(
            string engine,
            string engineNotes,
            DbConnection writer,
            DbCommand writerInsert,
            DbConnection[] readers,
            DbCommand[] readCommands,
            DbCommand[] queryCommands,
            string payload,
            bool strictPragmas)
        {
            Engine = engine;
            EngineNotes = engineNotes;
            this.writer = writer;
            this.writerInsert = writerInsert;
            this.readers = readers;
            this.readCommands = readCommands;
            this.queryCommands = queryCommands;
            this.payload = payload;
            this.strictPragmas = strictPragmas;
        }

        public string Engine { get; }

        public string? EngineNotes { get; }

        public static async ValueTask<AdoBackend> OpenMicrosoftSqliteAsync(
            string storagePath, string synchronous, int slots, int payloadSize, CancellationToken cancellationToken) =>
            await OpenAsync(
                "sqlite",
                strictPragmas: true,
                synchronous,
                storagePath,
                slots,
                payloadSize,
                path => new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"),
                engineNotes => $"Microsoft.Data.Sqlite embedded; WAL journal; synchronous={synchronous}; busy_timeout=5000; {engineNotes}",
                cancellationToken).ConfigureAwait(false);

        public static async ValueTask<AdoBackend> OpenTursoAsync(
            string storagePath, int slots, int payloadSize, CancellationToken cancellationToken) =>
            await OpenAsync(
                "turso",
                strictPragmas: false,
                "DEFAULT",
                storagePath,
                slots,
                payloadSize,
                path => new Turso.Data.Sqlite.SqliteConnection($"Data Source={path}"),
                engineNotes => $"Turso.Data.Sqlite.Provider embedded (Turso SDK native runtime); {engineNotes}",
                cancellationToken).ConfigureAwait(false);

        private static async ValueTask<AdoBackend> OpenAsync(
            string engine,
            bool strictPragmas,
            string synchronous,
            string storagePath,
            int slots,
            int payloadSize,
            Func<string, DbConnection> connectionFactory,
            Func<string, string> buildNotes,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(storagePath);
            var payload = new string('x', payloadSize);
            var databasePath = Path.Combine(storagePath, "reference.db");
            await using (var setup = connectionFactory(databasePath))
            {
                await setup.OpenAsync(cancellationToken).ConfigureAwait(false);
                var journal = await ExecutePragmaAsync(setup, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
                if (strictPragmas && journal != "wal")
                {
                    throw new InvalidOperationException($"SQLite journal_mode returned '{journal}' instead of 'wal'.");
                }

                if (synchronous != "DEFAULT")
                {
                    await ExecuteAsync(setup, $"PRAGMA synchronous={synchronous};", cancellationToken).ConfigureAwait(false);
                }

                await ExecuteAsync(
                    setup,
                    "CREATE TABLE users (tenant TEXT NOT NULL, id TEXT NOT NULL, name TEXT NOT NULL, \"group\" INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY (tenant, id));",
                    cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(
                    setup,
                    "CREATE INDEX users_tenant_name ON users (tenant, name);",
                    cancellationToken).ConfigureAwait(false);
                var writer = await OpenConnectionAsync(connectionFactory, databasePath, synchronous, cancellationToken).ConfigureAwait(false);
                var writerInsert = CreateInsertCommand(writer);
                var readers = new DbConnection[slots];
                var readCommands = new DbCommand[slots];
                var queryCommands = new DbCommand[slots];
                try
                {
                    for (var slot = 0; slot < slots; slot++)
                    {
                        readers[slot] = await OpenConnectionAsync(connectionFactory, databasePath, synchronous, cancellationToken).ConfigureAwait(false);
                        readCommands[slot] = CreateReadCommand(readers[slot]);
                        queryCommands[slot] = CreateQueryCommand(readers[slot]);
                    }
                }
                catch
                {
                    await DisposeCoreAsync(writer, writerInsert, readers, readCommands, queryCommands).ConfigureAwait(false);
                    throw;
                }

                return new AdoBackend(
                    engine,
                    buildNotes($"journal_mode={journal}"),
                    writer,
                    writerInsert,
                    readers,
                    readCommands,
                    queryCommands,
                    payload,
                    strictPragmas);
            }
        }

        private static async Task<DbConnection> OpenConnectionAsync(
            Func<string, DbConnection> connectionFactory,
            string databasePath,
            string synchronous,
            CancellationToken cancellationToken)
        {
            var connection = connectionFactory(databasePath);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
            if (synchronous != "DEFAULT")
            {
                await ExecuteAsync(connection, $"PRAGMA synchronous={synchronous};", cancellationToken).ConfigureAwait(false);
            }

            return connection;
        }

        private static async Task ExecuteAsync(DbConnection connection, string commandText, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<string?> ExecutePragmaAsync(
            DbConnection connection, string commandText, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static DbCommand CreateInsertCommand(DbConnection connection)
        {
            var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO users (tenant, id, name, \"group\", payload) VALUES (@tenant, @id, @name, @group, @payload);";
            AddTextParameter(command, "@tenant");
            AddTextParameter(command, "@id");
            AddTextParameter(command, "@name");
            AddParameter(command, "@group");
            AddTextParameter(command, "@payload");
            return command;
        }

        private static DbCommand CreateReadCommand(DbConnection connection)
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT name, \"group\", payload FROM users WHERE tenant = @tenant AND id = @id;";
            AddTextParameter(command, "@tenant");
            AddTextParameter(command, "@id");
            return command;
        }

        private static DbCommand CreateQueryCommand(DbConnection connection)
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT id, payload FROM users WHERE tenant = @tenant AND name = @name;";
            AddTextParameter(command, "@tenant");
            AddTextParameter(command, "@name");
            return command;
        }

        private static void AddTextParameter(DbCommand command, string name)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = string.Empty;
            command.Parameters.Add(parameter);
        }

        private static void AddParameter(DbCommand command, string name)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = 0L;
            command.Parameters.Add(parameter);
        }

        public async ValueTask SeedAsync(int index, CancellationToken cancellationToken)
        {
            await writerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await InsertAsync(writerInsert, SeedId(index), SeedName(index), index % 100, payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writerLock.Release();
            }
        }

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> ReadAsync(int index, int slot, CancellationToken cancellationToken)
        {
            var command = readCommands[slot];
            command.Parameters[0]!.Value = Tenant;
            command.Parameters[1]!.Value = SeedId(index);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.GetString(0) != SeedName(index))
            {
                throw new InvalidOperationException($"{Engine} point read missed or mismatched {SeedId(index)}.");
            }

            var bytes = (long)Encoding.UTF8.GetByteCount(reader.GetString(0)) + sizeof(long)
                + Encoding.UTF8.GetByteCount(reader.GetString(2));
            return (bytes, 0);
        }

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> QueryAsync(int index, int slot, CancellationToken cancellationToken)
        {
            var command = queryCommands[slot];
            command.Parameters[0]!.Value = Tenant;
            command.Parameters[1]!.Value = SeedName(index);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.GetString(0) != SeedId(index))
            {
                throw new InvalidOperationException($"{Engine} query for {SeedName(index)} did not return {SeedId(index)}.");
            }

            var bytes = (long)Encoding.UTF8.GetByteCount(reader.GetString(0)) + Encoding.UTF8.GetByteCount(reader.GetString(1));
            return (bytes, 0);
        }

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> WriteAsync(long sequence, int slot, CancellationToken cancellationToken)
        {
            var id = WriteId(sequence);
            var name = WriteName(sequence);
            await writerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var logical = await InsertAsync(writerInsert, id, name, sequence % 100, payload, cancellationToken).ConfigureAwait(false);
                return (0, logical);
            }
            finally
            {
                writerLock.Release();
            }
        }

        public async ValueTask<ComparisonQueryPlan?> CaptureQueryPlanAsync(CancellationToken cancellationToken)
        {
            await writerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var command = writer.CreateCommand();
                command.CommandText = "EXPLAIN QUERY PLAN SELECT id, payload FROM users WHERE tenant = @tenant AND name = @name;";
                AddTextParameter(command, "@tenant");
                AddTextParameter(command, "@name");
                command.Parameters[0]!.Value = Tenant;
                command.Parameters[1]!.Value = SeedName(0);
                var detail = new List<string>();
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        detail.Add(reader.GetString(3));
                    }
                }

                var text = string.Join("; ", detail);
                var usesIndex = text.Contains("USING INDEX", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("USING COVERING INDEX", StringComparison.OrdinalIgnoreCase);
                var indexName = usesIndex
                    ? text[(text.IndexOf("INDEX", StringComparison.OrdinalIgnoreCase) + "INDEX".Length)..].Trim()
                    : null;
                return new ComparisonQueryPlan(
                    Access: usesIndex ? "IndexSeek" : "Scan",
                    IndexName: indexName,
                    UsedSnapshotFallback: false,
                    Reason: text,
                    RowsExamined: null,
                    WorkUnits: null);
            }
            finally
            {
                writerLock.Release();
            }
        }

        public async ValueTask DisposeAsync() =>
            await DisposeCoreAsync(writer, writerInsert, readers, readCommands, queryCommands).ConfigureAwait(false);

        private static async ValueTask DisposeCoreAsync(
            DbConnection writer,
            DbCommand writerInsert,
            DbConnection[] readers,
            DbCommand[] readCommands,
            DbCommand[] queryCommands)
        {
            writerInsert.Dispose();
            await writer.DisposeAsync().ConfigureAwait(false);
            for (var slot = 0; slot < readers.Length; slot++)
            {
                readCommands[slot]?.Dispose();
                queryCommands[slot]?.Dispose();
                if (readers[slot] is not null)
                {
                    await readers[slot].DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task<long> InsertAsync(
            DbCommand command, string id, string name, long group, string payload, CancellationToken cancellationToken)
        {
            command.Parameters[0]!.Value = Tenant;
            command.Parameters[1]!.Value = id;
            command.Parameters[2]!.Value = name;
            command.Parameters[3]!.Value = group;
            command.Parameters[4]!.Value = payload;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetByteCount(id) + Encoding.UTF8.GetByteCount(name) + sizeof(long) + Encoding.UTF8.GetByteCount(payload);
        }

        private static string SeedId(int index) => $"seed-{index:D5}";

        private static string SeedName(int index) => $"user-{index}";

        private static string WriteId(long sequence) => $"write-{sequence:D8}";

        private static string WriteName(long sequence) => $"user-w{sequence}";
    }

    /// <summary>
    /// Embedded LiteDB v6 reference: one shared thread-safe LiteDatabase, a unique
    /// index on name, and autocommit per operation (no explicit transactions).
    /// </summary>
    private sealed class LiteDbBackend : IComparisonBackend
    {
        private readonly LiteDatabase database;
        private readonly ILiteCollection<BsonDocument> users;
        private readonly string payload;

        private LiteDbBackend(LiteDatabase database, ILiteCollection<BsonDocument> users, string payload)
        {
            this.database = database;
            this.users = users;
            this.payload = payload;
        }

        public string Engine => "litedb";

        public string? EngineNotes => "LiteDB 6.0.0-prerelease embedded; shared thread-safe engine (v6 WAL rewrite); unique index on name";

        public static LiteDbBackend Open(string storagePath, int payloadSize)
        {
            Directory.CreateDirectory(storagePath);
            var payload = new string('x', payloadSize);
            var database = new LiteDatabase($"Filename={Path.Combine(storagePath, "reference.db")};Connection=direct");
            var users = database.GetCollection<BsonDocument>("users");
            users.EnsureIndex("name", "$.name", unique: true);
            return new LiteDbBackend(database, users, payload);
        }

        public ValueTask SeedAsync(int index, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            users.Insert(CreateSeedDocument(index, payload));
            return ValueTask.CompletedTask;
        }

        public ValueTask<(long ReturnedBytes, long LogicalBytes)> ReadAsync(int index, int slot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = users.FindById(SeedId(index))
                ?? throw new InvalidOperationException($"LiteDB point read missed {SeedId(index)}.");
            if (document["name"].AsString != SeedName(index))
            {
                throw new InvalidOperationException($"LiteDB point read mismatched {SeedId(index)}.");
            }

            var bytes = (long)Encoding.UTF8.GetByteCount(document["name"].AsString) + sizeof(long)
                + Encoding.UTF8.GetByteCount(document["payload"].AsString);
            return ValueTask.FromResult((bytes, 0L));
        }

        public ValueTask<(long ReturnedBytes, long LogicalBytes)> QueryAsync(int index, int slot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = users.FindOne(Query.EQ("name", SeedName(index)))
                ?? throw new InvalidOperationException($"LiteDB query missed {SeedName(index)}.");
            if (document["_id"].AsString != SeedId(index))
            {
                throw new InvalidOperationException($"LiteDB query for {SeedName(index)} did not return {SeedId(index)}.");
            }

            var bytes = (long)Encoding.UTF8.GetByteCount(document["_id"].AsString)
                + Encoding.UTF8.GetByteCount(document["payload"].AsString);
            return ValueTask.FromResult((bytes, 0L));
        }

        public ValueTask<(long ReturnedBytes, long LogicalBytes)> WriteAsync(long sequence, int slot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = WriteId(sequence);
            var name = WriteName(sequence);
            users.Insert(new BsonDocument
            {
                ["_id"] = id,
                ["name"] = name,
                ["group"] = sequence % 100,
                ["payload"] = payload,
            });
            var logical = (long)(Encoding.UTF8.GetByteCount(id) + Encoding.UTF8.GetByteCount(name) + sizeof(long) + Encoding.UTF8.GetByteCount(payload));
            return ValueTask.FromResult((0L, logical));
        }

        public ValueTask<ComparisonQueryPlan?> CaptureQueryPlanAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nameIndex = false;
            foreach (var index in database.Execute("SELECT $ FROM $indexes").ToEnumerable())
            {
                if (index["name"].AsString == "name" && index["unique"].AsBoolean && index["expression"].AsString == "$.name")
                {
                    nameIndex = true;
                }
            }

            return ValueTask.FromResult<ComparisonQueryPlan?>(new ComparisonQueryPlan(
                Access: nameIndex ? "IndexLookup" : "Scan",
                IndexName: "name",
                UsedSnapshotFallback: false,
                Reason: nameIndex
                    ? "LiteDB 6.0.0-prerelease.181 has no EXPLAIN surface; the unique $.name index is verified via $indexes, and per-query cost grows ~15% from 1,000 to 10,000 documents (probe evidence), consistent with index selection rather than a scan."
                    : "The unique $.name index was not present in $indexes; FindOne(Query.EQ) executes as a collection scan.",
                RowsExamined: null,
                WorkUnits: null));
        }

        public ValueTask DisposeAsync()
        {
            database.Dispose();
            return ValueTask.CompletedTask;
        }

        private static BsonDocument CreateSeedDocument(int index, string payload) =>
            new()
            {
                ["_id"] = SeedId(index),
                ["name"] = SeedName(index),
                ["group"] = index % 100,
                ["payload"] = payload,
            };

        private static string SeedId(int index) => $"seed-{index:D5}";

        private static string SeedName(int index) => $"user-{index}";

        private static string WriteId(long sequence) => $"write-{sequence:D8}";

        private static string WriteName(long sequence) => $"user-w{sequence}";
    }

    private sealed class ComparisonOptions
    {
        public int DatasetSize { get; private set; } = 1_000;
        public int PayloadSize { get; private set; } = 256;
        public int Samples { get; private set; } = 5;
        public int WarmupOperations { get; private set; } = 1_000;
        public int OperationsPerSample { get; private set; } = 10_000;
        public int Seed { get; private set; } = 1337;
        public string OutputDirectory { get; private set; } = DefaultOutput();
        public string[]? Engines { get; private set; }
        public string[]? Lanes { get; private set; }
        public bool ShowHelp { get; private set; }

        public static ComparisonOptions Parse(string[] args)
        {
            var options = new ComparisonOptions();
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (argument is "--help" or "-h")
                {
                    options.ShowHelp = true;
                    continue;
                }

                var (name, value) = ReadOption(args, ref index, argument);
                switch (name)
                {
                    case "workload":
                        if (!value.Equals("engine-comparison", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ArgumentException("--workload must be engine-comparison.");
                        }

                        break;
                    case "dataset-size":
                        options.DatasetSize = ParseInteger(value, name, 1);
                        break;
                    case "payload-size":
                        options.PayloadSize = ParseInteger(value, name, 1);
                        break;
                    case "samples":
                        options.Samples = ParseInteger(value, name, 5);
                        break;
                    case "warmup":
                        options.WarmupOperations = ParseInteger(value, name, 0);
                        break;
                    case "operations":
                        options.OperationsPerSample = ParseInteger(value, name, 1);
                        break;
                    case "seed":
                        options.Seed = ParseInteger(value, name, int.MinValue);
                        break;
                    case "output":
                        options.OutputDirectory = value;
                        break;
                    case "engines":
                        options.Engines = ParseList(value, name);
                        break;
                    case "lanes":
                        options.Lanes = ParseList(value, name);
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '--{name}'.");
                }
            }

            return options;
        }

        public static void PrintHelp() => Console.WriteLine("""
            TerraBase vs SQLite/LiteDB/Turso full-flow comparison benchmark
              --workload engine-comparison      Run this workload
              --dataset-size N                  Seeded documents (default: 1000)
              --payload-size N                  Payload string bytes (default: 256)
              --samples N                       Post-warmup samples, minimum 5 (default: 5)
              --warmup N                        Warmup operations (default: 1000)
              --operations N                    Operations per sample (default: 10000)
              --seed N                          Fixed workload seed (default: 1337)
              --engines a,b,c                   Restrict to engines (sqlite,litedb,turso,terrabase-engine,terrabase-http,terrabase-sdk)
              --lanes a,b,c                     Restrict to lanes (read,query,write,mixed)
              --output DIR                      Receipt directory
            All engines run in one process under one envelope: TerraBase through
            its HTTP document API against an in-process host; SQLite, LiteDB, and
            Turso as embedded references (C# on the same .NET runtime throughout).
            """);

        private static string DefaultOutput() => Path.Combine(
            "bench",
            "receipts",
            $"engine-comparison-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}");

        private static (string Name, string Value) ReadOption(string[] args, ref int index, string argument)
        {
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Expected an option, got '{argument}'.");
            }

            var separator = argument.IndexOf('=');
            if (separator > 2)
            {
                return (argument[2..separator], argument[(separator + 1)..]);
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '{argument}' requires a value.");
            }

            return (argument[2..], args[++index]);
        }

        private static int ParseInteger(string value, string name, int minimum)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum)
            {
                throw new ArgumentException($"--{name} must be an integer >= {minimum}.");
            }

            return parsed;
        }

        private static string[] ParseList(string value, string name) =>
            value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } items
                ? items
                : throw new ArgumentException($"--{name} must be a comma-separated list.");
    }

    private sealed record EngineComparisonReceipt(
        string Status,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        ComparisonConfiguration Configuration,
        ComparisonEnvironment Environment,
        IReadOnlyList<ComparatorIdentity> Comparators,
        IReadOnlyList<ComparisonCaseResult> Cases,
        string Command,
        string ReceiptDirectory);

    private sealed record ComparatorIdentity(
        string Engine,
        string Package,
        string? Version,
        string? AssemblySha256,
        string? NativeRuntimeSha256);

    private static List<ComparatorIdentity> CreateComparators()
    {
        var comparators = new List<ComparatorIdentity>
        {
            CreateComparator("sqlite", "Microsoft.Data.Sqlite", typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly, nativeRuntimeSha256: null),
            CreateComparator("litedb", "LiteDB", typeof(LiteDatabase).Assembly, nativeRuntimeSha256: null),
            CreateComparator("turso", "Turso.Data.Sqlite.Provider", typeof(Turso.Data.Sqlite.SqliteConnection).Assembly, FindTursoNativeRuntimeSha256()),
        };
        return comparators;
    }

    private static ComparatorIdentity CreateComparator(string engine, string package, Assembly assembly, string? nativeRuntimeSha256)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();
        return new ComparatorIdentity(
            engine,
            package,
            version,
            Sha256File(assembly.Location),
            nativeRuntimeSha256);
    }

    private static string? FindTursoNativeRuntimeSha256()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "libturso_sdk_kit.so"),
            Path.Combine(AppContext.BaseDirectory, "libturso_sdk_kit.so"),
        };
        return candidates.FirstOrDefault(File.Exists) is { } path ? Sha256File(path) : null;
    }

    private static string? Sha256File(string? path) =>
        path is not null && File.Exists(path)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            : null;

    private sealed record ComparisonConfiguration(
        string Storage,
        string OutputDirectory,
        int DatasetSize,
        int PayloadSize,
        int Samples,
        int WarmupOperations,
        int OperationsPerSample,
        int[] ConcurrencyLanes,
        int Seed,
        string[] Workloads,
        string[] Temperatures);

    private sealed record ComparisonEnvironment(
        DateTimeOffset StartedAtUtc,
        string Runtime,
        string FrameworkDescription,
        string BuildConfiguration,
        string OperatingSystem,
        string Architecture,
        int ProcessorCount,
        long AvailableMemoryBytes,
        string CpuModel,
        bool ServerGc,
        string GcLatencyMode,
        string? GitCommit,
        bool? GitDirty,
        string? ArtifactPath,
        string? ArtifactSha256)
    {
        public static ComparisonEnvironment Create()
        {
            var artifactPath = Assembly.GetExecutingAssembly().Location;
            var buildConfiguration = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";
            var artifactHash = File.Exists(artifactPath)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifactPath))).ToLowerInvariant()
                : null;
            var gitRoot = FindGitRoot();
            var commit = gitRoot is null ? null : RunGit(gitRoot, "rev-parse", "HEAD");
            bool? dirty = null;
            if (gitRoot is not null)
            {
                dirty = RunGit(gitRoot, "status", "--porcelain") is { } status && status.Length != 0;
            }

            return new ComparisonEnvironment(
                DateTimeOffset.UtcNow,
                Environment.Version.ToString(),
                RuntimeInformation.FrameworkDescription,
                buildConfiguration,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                ReadCpuModel(),
                GCSettings.IsServerGC,
                GCSettings.LatencyMode.ToString(),
                commit,
                dirty,
                string.IsNullOrWhiteSpace(artifactPath) ? null : artifactPath,
                artifactHash);
        }

        private static string? FindGitRoot()
        {
            var candidates = new[]
            {
                new DirectoryInfo(Environment.CurrentDirectory),
                new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory),
            };
            foreach (var candidate in candidates)
            {
                var current = candidate;
                while (current is not null)
                {
                    if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                    {
                        return current.FullName;
                    }

                    current = current.Parent;
                }
            }

            return null;
        }

        private static string? RunGit(string workingDirectory, params string[] arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return null;
                }

                // Drain the redirected pipes while the process runs: a
                // status-heavy repository can exceed the pipe buffer and
                // deadlock a WaitForExit that has not read stdout yet.
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(TimeSpan.FromSeconds(30));
                return process.ExitCode == 0 ? output.Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string ReadCpuModel()
        {
            try
            {
                if (File.Exists("/proc/cpuinfo"))
                {
                    var line = File.ReadLines("/proc/cpuinfo")
                        .FirstOrDefault(static line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
                    if (line is not null)
                    {
                        var separator = line.IndexOf(':');
                        return separator >= 0 ? line[(separator + 1)..].Trim() : line.Trim();
                    }
                }
            }
            catch
            {
                // The model is optional environment metadata.
            }

            return "unknown";
        }
    }

    private sealed class ComparisonCaseResult
    {
        public string Engine { get; init; } = string.Empty;
        public string? Synchronous { get; init; }
        public string? EngineNotes { get; set; }
        public string Lane { get; init; } = string.Empty;
        public int Concurrency { get; init; }
        public string Status { get; set; } = "failed";
        public string? Error { get; set; }
        public string RawSamplesPath { get; init; } = string.Empty;
        public string? WorkloadDigest { get; set; }
        public ComparisonQueryPlan? QueryPlan { get; set; }
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset FinishedAtUtc { get; set; }
        public List<ComparisonSample> Samples { get; } = [];
    }

    private sealed record ComparisonQueryPlan(
        string Access,
        string? IndexName,
        bool UsedSnapshotFallback,
        string? Reason,
        double? RowsExamined,
        double? WorkUnits);

    private sealed class ComparisonSample
    {
        public int Sample { get; init; }
        public int Operations { get; init; }
        public int Concurrency { get; init; }
        public double ElapsedMilliseconds { get; init; }
        public double ThroughputOpsPerSecond { get; init; }
        public double P50Microseconds { get; init; }
        public double P95Microseconds { get; init; }
        public double P99Microseconds { get; init; }
        public long AllocatedBytes { get; init; }
        public double AllocatedBytesPerOperation { get; init; }
        public double ProcessCpuMilliseconds { get; init; }
        public double ProcessCpuPercent { get; init; }
        public long ProcessWorkingSetBytesBefore { get; init; }
        public long ProcessWorkingSetBytesAfter { get; init; }
        public long ProcessWorkingSetDeltaBytes { get; init; }
        public long LogicalBytes { get; init; }
        public long ReturnedBytes { get; init; }
        public long? StorageBytesBefore { get; init; }
        public long? StorageBytesAfter { get; init; }
        public long? BytesWritten { get; init; }
        public double? WriteAmplification { get; init; }
    }

    private static class EngineComparisonMarkdown
    {
        private static readonly string[] ComparisonOrder =
        [
            "sqlite",
            "litedb",
            "turso",
            "terrabase-engine",
            "terrabase-http",
#if TERRASCALE_SDK
            "terrabase-sdk",
#endif
        ];

        public static string Create(EngineComparisonReceipt receipt, string receiptPath)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# TerraBase vs embedded engines full-flow comparison receipt");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"Status: **{receipt.Status}**");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"Receipt JSON: `{Path.GetFileName(receiptPath)}`");
            builder.AppendLine();
            builder.AppendLine("## Environment");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Commit: `{receipt.Environment.GitCommit ?? "unavailable"}` (dirty: `{receipt.Environment.GitDirty?.ToString() ?? "unknown"}`)");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Artifact SHA-256: `{receipt.Environment.ArtifactSha256 ?? "unavailable"}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Runtime: {receipt.Environment.FrameworkDescription}; build `{receipt.Environment.BuildConfiguration}`; {receipt.Environment.OperatingSystem}; {receipt.Environment.Architecture}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- CPU: {receipt.Environment.CpuModel}; {receipt.Environment.ProcessorCount} logical processors; available memory {receipt.Environment.AvailableMemoryBytes:N0} bytes");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- GC: server `{receipt.Environment.ServerGc}`, latency mode `{receipt.Environment.GcLatencyMode}`");
            builder.AppendLine();
            builder.AppendLine("## Comparators");
            builder.AppendLine();
            builder.AppendLine("| Engine | Package | Version | Assembly SHA-256 | Native runtime SHA-256 |");
            builder.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var comparator in receipt.Comparators)
            {
                builder.Append('|').Append(comparator.Engine).Append(" | ").Append(comparator.Package).Append(" | ")
                    .Append(comparator.Version ?? "—").Append(" | ")
                    .Append('`').Append(comparator.AssemblySha256 ?? "—").Append("` | ")
                    .Append('`').Append(comparator.NativeRuntimeSha256 ?? "—").AppendLine("` |");
            }

            builder.AppendLine();
            builder.AppendLine("## Boundaries");
            builder.AppendLine();
            builder.AppendLine("TerraBase runs in three lanes: the `terrabase-engine` lane drives the durable StorageEngine and AutomaticIndexRuntime directly in-process (no HTTP — the same comparison class as the embedded engines), while the `terrabase-http` lane serves the real HTTP document API (PUT/GET/bounded JSON-DSL query) over a Unix-socket host using a bare HttpClient, and the `terrabase-sdk` lane drives that host through the official TerraScale.DotNet SDK (`TerraScaleClient`) as the interface. SQLite (Microsoft.Data.Sqlite), LiteDB, and Turso (Turso.Data.Sqlite.Provider over the Turso native runtime) are embedded in-process engines. All engines share one process, one C#/.NET runtime, and one resource envelope; each case owns a fresh database seeded with the same documents.");
            builder.AppendLine();
            builder.AppendLine("## Results");
            builder.AppendLine();
            builder.AppendLine("| Engine | Sync | Lane | Conc | Status | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | Bytes written | Query plan | Raw samples |");
            builder.AppendLine("| --- | --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |");
            foreach (var item in receipt.Cases)
            {
                var samples = item.Samples;
                builder.Append('|').Append(item.Engine).Append(" | ").Append(item.Synchronous ?? "—").Append(" | ")
                    .Append(item.Lane).Append(" | ").Append(item.Concurrency.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(item.Status).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.ThroughputOpsPerSecond)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.P50Microseconds)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.P95Microseconds)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.P99Microseconds)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.AllocatedBytesPerOperation)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.ProcessCpuPercent)))).Append(" | ");
                builder.Append(FormatLong(MedianLong(samples.Select(static sample => sample.ProcessWorkingSetBytesAfter)))).Append(" | ");
                builder.Append(FormatLong(MedianNullableLong(samples.Select(static sample => sample.BytesWritten)))).Append(" | ");
                builder.Append(item.QueryPlan is { } plan ? plan.Access : "—").Append(" | ");
                builder.Append('`').Append(Path.GetFileName(item.RawSamplesPath)).AppendLine("` |");
                if (item.Error is not null)
                {
                    builder.AppendLine();
                    builder.AppendLine(CultureInfo.InvariantCulture, $"Failure for `{item.Engine}/{item.Synchronous ?? "-"}/{item.Lane}/c{item.Concurrency}`: `{item.Error.Replace("\n", " ", StringComparison.Ordinal)}`");
                }
            }

            AppendComparisonTable(builder, receipt, "Throughput ops/s (median of per-sample medians; higher is better)", "TerraBase/SQLite", static sample => sample.ThroughputOpsPerSecond);
            AppendComparisonTable(builder, receipt, "p50 latency µs (median of per-sample p50s; a TerraBase/SQLite factor below 1 means TerraBase is faster)", "TerraBase/SQLite", static sample => sample.P50Microseconds);

            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"Command: `{receipt.Command}`");
            builder.AppendLine();
            builder.AppendLine("All percentile values are medians of per-sample percentiles. `Bytes written` is the per-sample storage-directory size delta for the case's own data directory. The SQLite synchronous=NORMAL rows are a durability-sensitivity context, not part of the matched comparison tables. Query plans are captured once per case after warm-up (TerraBase plan envelope; EXPLAIN QUERY PLAN for the SQL engines; index presence for LiteDB).");
            return builder.ToString();
        }

        private static void AppendComparisonTable(
            StringBuilder builder,
            EngineComparisonReceipt receipt,
            string title,
            string ratioHeader,
            Func<ComparisonSample, double> selector)
        {
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"## Matched comparison — {title}");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"| Lane | Conc | {string.Join(" | ", ComparisonOrder.Select(DisplayName))} | {ratioHeader} |");
            builder.AppendLine(CultureInfo.InvariantCulture, $"| --- | --- |{string.Join("", ComparisonOrder.Select(_ => " ---: |"))} ---: |");
            foreach (var group in receipt.Cases.Where(static item => item.Status == "complete")
                .GroupBy(static item => (item.Lane, item.Concurrency))
                .OrderBy(static group => group.Key.Concurrency)
                .ThenBy(static group => group.Key.Lane, StringComparer.Ordinal))
            {
                var sqlite = group.FirstOrDefault(static item => item.Engine == "sqlite" && item.Synchronous == "FULL");
                if (sqlite is null)
                {
                    continue;
                }

                builder.Append('|').Append(group.Key.Lane).Append(" | ").Append(group.Key.Concurrency.ToString(CultureInfo.InvariantCulture)).Append(" | ");
                double? sqliteValue = null;
                foreach (var engine in ComparisonOrder)
                {
                    var item = engine == "sqlite"
                        ? sqlite
                        : group.FirstOrDefault(candidate => candidate.Engine == engine && candidate.Synchronous is null);
                    double? value = item is null ? null : Median(item.Samples.Select(selector));
                    if (engine == "sqlite")
                    {
                        sqliteValue = value;
                    }

                    builder.Append(Format(value)).Append(" | ");
                }

                var terrabase = group.FirstOrDefault(static item => item.Engine is "terrabase-engine" or "terrabase-http" or "terrabase-sdk");
                var terrabaseValue = terrabase is null ? null : Median(terrabase.Samples.Select(selector));
                builder.Append(Format(Ratio(terrabaseValue, sqliteValue))).AppendLine(" |");
            }
        }

        private static string DisplayName(string engine) => engine switch
        {
            "sqlite" => "SQLite FULL",
            "litedb" => "LiteDB",
            "turso" => "Turso",
            "terrabase-engine" => "TerraBase engine",
            "terrabase-http" => "TerraBase HTTP",
            "terrabase-sdk" => "TerraBase SDK",
            _ => engine,
        };

        private static double? Ratio(double? value, double? baseline)
        {
            if (value is null || baseline is null || baseline.Value == 0)
            {
                return null;
            }

            return value.Value / baseline.Value;
        }

        private static string Format(double? value) => value is null ? "—" : value.Value.ToString("F2", CultureInfo.InvariantCulture);

        private static string FormatLong(long? value) => value is null ? "—" : value.Value.ToString("N0", CultureInfo.InvariantCulture);

        private static double? Median(IEnumerable<double> values)
        {
            var array = values.ToArray();
            if (array.Length == 0)
            {
                return null;
            }

            Array.Sort(array);
            return array[array.Length / 2];
        }

        private static long? MedianLong(IEnumerable<long> values)
        {
            var array = values.ToArray();
            if (array.Length == 0)
            {
                return null;
            }

            Array.Sort(array);
            return array[array.Length / 2];
        }

        private static long? MedianNullableLong(IEnumerable<long?> values)
        {
            var array = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
            return array.Length == 0 ? null : MedianLong(array);
        }
    }
}
