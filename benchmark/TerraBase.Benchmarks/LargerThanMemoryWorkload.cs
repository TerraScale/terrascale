using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TerraBase;
using TerraBase.StorageEngine;

/// <summary>
/// Bounded, durable larger-than-memory workload support for the benchmark
/// runner. The runner supplies the memory cap; this class never changes a
/// cgroup limit and never weakens the backend durability settings.
/// </summary>
internal static class LargerThanMemoryWorkload
{
    private static readonly long[] HistogramBucketUpperBoundsNanoseconds =
    [
        1_000,
        2_000,
        5_000,
        10_000,
        25_000,
        50_000,
        100_000,
        250_000,
        500_000,
        1_000_000,
        2_500_000,
        5_000_000,
        10_000_000,
        25_000_000,
        50_000_000,
        100_000_000,
    ];

    /// <summary>Runs a bounded larger-than-memory storage workload.</summary>
    /// <param name="configuration">The validated workload configuration.</param>
    /// <param name="outputDirectory">The receipt directory for raw samples and storage files.</param>
    /// <param name="cancellationToken">The cancellation signal.</param>
    /// <returns>The machine-readable workload result.</returns>
    public static async Task<RunResult> RunAsync(
        Configuration configuration,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        configuration.Validate();

        var receiptDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(receiptDirectory);
        var rawPath = Path.Combine(receiptDirectory, $"raw-samples-larger-than-memory-{configuration.Backend}.jsonl");
        var storagePath = Path.Combine(receiptDirectory, "larger-than-memory", configuration.Backend.ToString());
        Directory.CreateDirectory(storagePath);
        configuration.ActiveStoragePath = storagePath;

        var keys = CreateKeys(configuration.DatasetSize);
        using var payload = CreatePayload(configuration.PayloadSize);
        var estimatedDatasetBytes = EstimateDatasetBytes(keys, payload.RootElement);
        if (estimatedDatasetBytes <= configuration.MemoryCapBytes)
        {
            throw new ArgumentException(
                $"The estimated logical dataset ({estimatedDatasetBytes:N0} bytes) must exceed the configured memory cap ({configuration.MemoryCapBytes:N0} bytes).",
                nameof(configuration));
        }

        var result = new RunResult
        {
            Status = "failed",
            Backend = configuration.Backend.ToString(),
            RawSamplesPath = rawPath,
            StoragePath = storagePath,
            DatasetSize = configuration.DatasetSize,
            PayloadSize = configuration.PayloadSize,
            EstimatedLogicalDatasetBytes = estimatedDatasetBytes,
            ConfiguredMemoryCapBytes = configuration.MemoryCapBytes,
            Configuration = configuration,
            StartedAtUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            await using var handle = Open(storagePath, configuration.Backend);
            await SeedAsync(handle.Engine.Documents, keys, payload.RootElement, cancellationToken).ConfigureAwait(false);
            var distribution = new KeyDistribution(keys.Count, configuration.ZipfExponent);
            await RunOperationsAsync(
                handle.Engine.Documents,
                configuration,
                keys,
                payload.RootElement,
                configuration.WarmupOperations,
                distribution,
                "warmup",
                ReadWriteMix.ReadHeavy,
                configuration.InitialDistribution,
                cancellationToken).ConfigureAwait(false);

            await using var raw = new StreamWriter(rawPath, append: false, new UTF8Encoding(false));
            for (var sampleIndex = 0; sampleIndex < configuration.Samples; sampleIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sample = await RunSampleAsync(
                    handle,
                    configuration,
                    keys,
                    payload.RootElement,
                    distribution,
                    sampleIndex,
                    cancellationToken).ConfigureAwait(false);
                result.Samples.Add(sample);
                await raw.WriteLineAsync(JsonSerializer.Serialize(sample, JsonOptions)).ConfigureAwait(false);
                await raw.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            result.Status = "complete";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Error = "Cancelled by user.";
        }
        catch (Exception exception)
        {
            result.Error = exception.ToString();
        }
        finally
        {
            result.FinishedAtUtc = DateTimeOffset.UtcNow;
        }

        return result;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private static async Task<SampleResult> RunSampleAsync(
        EngineHandle handle,
        Configuration configuration,
        IReadOnlyList<DocumentKey> keys,
        JsonElement payload,
        KeyDistribution distribution,
        int sampleIndex,
        CancellationToken cancellationToken)
    {
        var sampleStarted = DateTimeOffset.UtcNow;
        var phases = new List<PhaseResult>();
        phases.Add(await MeasureOperationsAsync(
            handle.Engine.Documents,
            configuration,
            keys,
            payload,
            distribution,
            operations: configuration.OperationsPerSample,
            phaseName: "warm-cache",
            mix: ReadWriteMix.ReadHeavy,
            keyDistribution: configuration.InitialDistribution,
            randomSalt: sampleIndex * 17 + 1,
            cancellationToken).ConfigureAwait(false));

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var coldCpuBefore = process.TotalProcessorTime;
        var coldRssBefore = process.WorkingSet64;
        var coldAllocatedBefore = GC.GetTotalAllocatedBytes(false);
        var coldStorageBefore = StorageMetrics.Read(configuration.ActiveStoragePath);
        var coldStarted = Stopwatch.GetTimestamp();
        await handle.ReopenAsync(cancellationToken).ConfigureAwait(false);
        var coldOpen = Stopwatch.GetElapsedTime(coldStarted);
        process.Refresh();
        var coldCpu = process.TotalProcessorTime - coldCpuBefore;
        var coldRssAfter = process.WorkingSet64;
        var coldAllocated = Math.Max(0, GC.GetTotalAllocatedBytes(false) - coldAllocatedBefore);
        var coldStorageAfter = StorageMetrics.Read(configuration.ActiveStoragePath);
        phases.Add(new PhaseResult
        {
            Name = "cold-reopen",
            Kind = "reopen",
            Distribution = configuration.InitialDistribution.ToString(),
            Operations = 0,
            ElapsedMilliseconds = coldOpen.TotalMilliseconds,
            ThroughputOpsPerSecond = 0,
            P50Microseconds = null,
            P95Microseconds = null,
            P99Microseconds = null,
            LatencyHistogram = new LatencyHistogram(),
            AllocatedBytesPerOperation = null,
            ProcessCpuMilliseconds = coldCpu.TotalMilliseconds,
            ProcessCpuPercent = coldOpen.TotalSeconds <= 0 ? 0 : coldCpu.TotalSeconds / coldOpen.TotalSeconds / Math.Max(1, Environment.ProcessorCount) * 100,
            ProcessWorkingSetBytesBefore = coldRssBefore,
            ProcessWorkingSetBytesAfter = coldRssAfter,
            ProcessWorkingSetDeltaBytes = coldRssAfter - coldRssBefore,
            AllocatedBytes = coldAllocated,
            LogicalBytes = 0,
            ReturnedBytes = 0,
            StorageBytesBefore = coldStorageBefore?.TotalBytes,
            StorageBytesAfter = coldStorageAfter?.TotalBytes,
            WalBytesBefore = coldStorageBefore?.WalBytes,
            WalBytesAfter = coldStorageAfter?.WalBytes,
            LogBytesDelta = coldStorageBefore is not null && coldStorageAfter is not null
                ? coldStorageAfter.Value.WalBytes - coldStorageBefore.Value.WalBytes
                : null,
            WriteAmplification = null,
            Notes = "Elapsed time includes disposal of the prior engine and durable reopen."
        });

        phases.Add(await MeasureOperationsAsync(
            handle.Engine.Documents,
            configuration,
            keys,
            payload,
            distribution,
            operations: configuration.OperationsPerSample,
            phaseName: "cold-cache",
            mix: ReadWriteMix.ReadHeavy,
            keyDistribution: configuration.InitialDistribution,
            randomSalt: sampleIndex * 17 + 2,
            cancellationToken).ConfigureAwait(false));

        var shift = configuration.Shifts[sampleIndex % configuration.Shifts.Count];
        phases.Add(await MeasureOperationsAsync(
            handle.Engine.Documents,
            configuration,
            keys,
            payload,
            distribution,
            shift.Operations,
            shift.Name,
            shift.Mix,
            shift.Distribution,
            sampleIndex * 17 + 3,
            cancellationToken).ConfigureAwait(false));

        if (configuration.SustainedWriteOperations > 0)
        {
            phases.Add(await MeasureOperationsAsync(
                handle.Engine.Documents,
                configuration,
                keys,
                payload,
                distribution,
                configuration.SustainedWriteOperations,
                "sustained-writes",
                ReadWriteMix.WriteOnly,
                shift.Distribution,
                sampleIndex * 17 + 4,
                cancellationToken).ConfigureAwait(false));
        }

        if (configuration.CompactAfterSample)
        {
            phases.Add(await MeasureCompactionAsync(handle, configuration, sampleIndex, cancellationToken).ConfigureAwait(false));
        }

        return new SampleResult
        {
            Sample = sampleIndex + 1,
            StartedAtUtc = sampleStarted,
            FinishedAtUtc = DateTimeOffset.UtcNow,
            Backend = configuration.Backend.ToString(),
            DatasetSize = configuration.DatasetSize,
            PayloadSize = configuration.PayloadSize,
            EstimatedLogicalDatasetBytes = EstimateDatasetBytes(keys, payload),
            ConfiguredMemoryCapBytes = configuration.MemoryCapBytes,
            Seed = configuration.Seed,
            ZipfExponent = configuration.ZipfExponent,
            InitialDistribution = configuration.InitialDistribution.ToString(),
            WorkloadShift = shift.Name,
            Phases = phases,
        };
    }

    private static async Task<PhaseResult> MeasureOperationsAsync(
        DocumentStore store,
        Configuration configuration,
        IReadOnlyList<DocumentKey> keys,
        JsonElement payload,
        KeyDistribution distribution,
        int operations,
        string phaseName,
        ReadWriteMix mix,
        KeyDistributionKind keyDistribution,
        int randomSalt,
        CancellationToken cancellationToken)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var rssBefore = process.WorkingSet64;
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var storageBefore = StorageMetrics.Read(configuration.ActiveStoragePath);
        var latencies = new long[operations];
        var logicalBytes = 0L;
        var returnedBytes = 0L;
        var started = Stopwatch.GetTimestamp();

        await RunOperationsAsync(
            store,
            configuration,
            keys,
            payload,
            operations,
            distribution,
            phaseName,
            mix,
            keyDistribution,
            randomSalt,
            cancellationToken,
            latencies,
            bytes => Interlocked.Add(ref logicalBytes, bytes.LogicalBytes),
            bytes => Interlocked.Add(ref returnedBytes, bytes.ReturnedBytes)).ConfigureAwait(false);

        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var rssAfter = process.WorkingSet64;
        var allocated = Math.Max(0, GC.GetTotalAllocatedBytes(false) - allocatedBefore);
        var storageAfter = StorageMetrics.Read(configuration.ActiveStoragePath);
        Array.Sort(latencies);
        var logDelta = storageBefore is not null && storageAfter is not null
            ? Math.Max(0, storageAfter.Value.WalBytes - storageBefore.Value.WalBytes)
            : (long?)null;
        var cpuPercent = elapsed.TotalSeconds <= 0
            ? 0
            : cpu.TotalSeconds / elapsed.TotalSeconds / Math.Max(1, Environment.ProcessorCount) * 100;

        return new PhaseResult
        {
            Name = phaseName,
            Kind = "operations",
            Distribution = keyDistribution.ToString(),
            Operations = operations,
            ElapsedMilliseconds = elapsed.TotalMilliseconds,
            ThroughputOpsPerSecond = operations / Math.Max(elapsed.TotalSeconds, double.Epsilon),
            P50Microseconds = ToMicroseconds(Percentile(latencies, 0.50)),
            P95Microseconds = ToMicroseconds(Percentile(latencies, 0.95)),
            P99Microseconds = ToMicroseconds(Percentile(latencies, 0.99)),
            LatencyHistogram = LatencyHistogram.Create(latencies),
            AllocatedBytes = allocated,
            AllocatedBytesPerOperation = operations == 0 ? null : allocated / (double)operations,
            ProcessCpuMilliseconds = cpu.TotalMilliseconds,
            ProcessCpuPercent = cpuPercent,
            ProcessWorkingSetBytesBefore = rssBefore,
            ProcessWorkingSetBytesAfter = rssAfter,
            ProcessWorkingSetDeltaBytes = rssAfter - rssBefore,
            LogicalBytes = logicalBytes,
            ReturnedBytes = returnedBytes,
            StorageBytesBefore = storageBefore?.TotalBytes,
            StorageBytesAfter = storageAfter?.TotalBytes,
            WalBytesBefore = storageBefore?.WalBytes,
            WalBytesAfter = storageAfter?.WalBytes,
            LogBytesDelta = logDelta,
            WriteAmplification = logDelta is > 0 && logicalBytes > 0 ? logDelta.Value / (double)logicalBytes : null,
            Notes = mix == ReadWriteMix.ReadHeavy
                ? "Read-heavy phase; logical bytes are the returned payload bytes."
                : "Write phase; logical bytes are the UTF-8 payload bytes submitted."
        };
    }

    private static async Task<PhaseResult> MeasureCompactionAsync(
        EngineHandle handle,
        Configuration configuration,
        int sampleIndex,
        CancellationToken cancellationToken)
    {
        if (handle.StorageEngine is null)
        {
            return new PhaseResult
            {
                Name = "compaction",
                Kind = "compaction",
                Distribution = "none",
                Operations = 0,
                ElapsedMilliseconds = 0,
                ThroughputOpsPerSecond = 0,
                LatencyHistogram = new LatencyHistogram(),
                Notes = "Skipped: ZoneTree backend does not expose the StorageEngine compaction contract."
            };
        }

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var rssBefore = process.WorkingSet64;
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var storageBefore = StorageMetrics.Read(configuration.ActiveStoragePath);
        var started = Stopwatch.GetTimestamp();
        var compacted = await handle.StorageEngine.CompactAsync(cancellationToken).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var rssAfter = process.WorkingSet64;
        var allocated = Math.Max(0, GC.GetTotalAllocatedBytes(false) - allocatedBefore);
        var storageAfter = StorageMetrics.Read(configuration.ActiveStoragePath);

        return new PhaseResult
        {
            Name = "compaction",
            Kind = "compaction",
            Distribution = "none",
            Operations = 1,
            ElapsedMilliseconds = elapsed.TotalMilliseconds,
            ThroughputOpsPerSecond = 1 / Math.Max(elapsed.TotalSeconds, double.Epsilon),
            P50Microseconds = elapsed.TotalMilliseconds * 1_000,
            P95Microseconds = elapsed.TotalMilliseconds * 1_000,
            P99Microseconds = elapsed.TotalMilliseconds * 1_000,
            LatencyHistogram = LatencyHistogram.Create([ToStopwatchTicks(elapsed)]),
            AllocatedBytes = allocated,
            AllocatedBytesPerOperation = allocated,
            ProcessCpuMilliseconds = cpu.TotalMilliseconds,
            ProcessCpuPercent = elapsed.TotalSeconds <= 0 ? 0 : cpu.TotalSeconds / elapsed.TotalSeconds / Math.Max(1, Environment.ProcessorCount) * 100,
            ProcessWorkingSetBytesBefore = rssBefore,
            ProcessWorkingSetBytesAfter = rssAfter,
            ProcessWorkingSetDeltaBytes = rssAfter - rssBefore,
            LogicalBytes = 0,
            ReturnedBytes = 0,
            StorageBytesBefore = storageBefore?.TotalBytes,
            StorageBytesAfter = storageAfter?.TotalBytes,
            WalBytesBefore = storageBefore?.WalBytes,
            WalBytesAfter = storageAfter?.WalBytes,
            LogBytesDelta = storageBefore is not null && storageAfter is not null
                ? storageAfter.Value.WalBytes - storageBefore.Value.WalBytes
                : null,
            WriteAmplification = null,
            Notes = $"Published {compacted.RecordsWritten.ToString(CultureInfo.InvariantCulture)} records and retained {compacted.TombstonesRetained.ToString(CultureInfo.InvariantCulture)} tombstones. Sample {sampleIndex + 1}."
        };
    }

    private static async Task RunOperationsAsync(
        DocumentStore store,
        Configuration configuration,
        IReadOnlyList<DocumentKey> keys,
        JsonElement payload,
        int operationCount,
        KeyDistribution distribution,
        string phaseName,
        ReadWriteMix mix,
        KeyDistributionKind keyDistribution,
        int randomSalt,
        CancellationToken cancellationToken,
        long[]? latencies = null,
        Action<OperationBytes>? onLogicalBytes = null,
        Action<OperationBytes>? onReturnedBytes = null)
    {
        if (operationCount == 0)
        {
            return;
        }

        var workers = Enumerable.Range(0, Math.Min(configuration.Concurrency, operationCount))
            .Select(worker => Task.Run(async () =>
            {
                var random = new Random(unchecked(configuration.Seed + randomSalt * 7919 + worker * 104729));
                for (var index = worker; index < operationCount; index += Math.Min(configuration.Concurrency, operationCount))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var keyIndex = keyDistribution == KeyDistributionKind.Zipf
                        ? distribution.NextZipf(random)
                        : random.Next(keys.Count);
                    var key = keys[keyIndex];
                    var write = mix switch
                    {
                        ReadWriteMix.WriteOnly => true,
                        ReadWriteMix.ReadOnly => false,
                        _ => random.NextDouble() >= configuration.ReadRatio,
                    };
                    var started = Stopwatch.GetTimestamp();
                    var bytes = write
                        ? await WriteAsync(store, key, payload, cancellationToken).ConfigureAwait(false)
                        : await ReadAsync(store, key, cancellationToken).ConfigureAwait(false);
                    if (latencies is not null)
                    {
                        latencies[index] = Stopwatch.GetTimestamp() - started;
                    }

                    onLogicalBytes?.Invoke(bytes);
                    onReturnedBytes?.Invoke(write ? default : bytes);
                }
            }, cancellationToken))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private static async ValueTask<OperationBytes> ReadAsync(
        DocumentStore store,
        DocumentKey key,
        CancellationToken cancellationToken)
    {
        var document = await store.ReadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The larger-than-memory workload read missed seeded key {key.DocumentId}.");
        var bytes = Encoding.UTF8.GetByteCount(document.Body.GetRawText());
        return new OperationBytes(bytes, bytes);
    }

    private static async ValueTask<OperationBytes> WriteAsync(
        DocumentStore store,
        DocumentKey key,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var result = await store.UpsertAsync(key, payload, DocumentPrecondition.Any, cancellationToken).ConfigureAwait(false);
        if (result.Outcome is not (WriteOutcome.Created or WriteOutcome.Updated))
        {
            throw new InvalidOperationException($"The larger-than-memory workload write failed for {key.DocumentId}: {result.Outcome}.");
        }

        return new OperationBytes(Encoding.UTF8.GetByteCount(payload.GetRawText()), 0);
    }

    private static async Task SeedAsync(
        DocumentStore store,
        IReadOnlyList<DocumentKey> keys,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await store.UpsertAsync(key, payload, DocumentPrecondition.Missing, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != WriteOutcome.Created)
            {
                throw new InvalidOperationException($"The larger-than-memory seed did not create {key.DocumentId}: {result.Outcome}.");
            }
        }
    }

    private static EngineHandle Open(string path, Backend backend)
    {
        IStorageProvider provider;
        StorageEngine? storageEngine = null;
        if (backend == Backend.StorageEngine)
        {
            storageEngine = new StorageEngine(path);
            provider = storageEngine;
        }
        else
        {
            provider = new ZoneTreeStorageProvider(path);
        }

        try
        {
            return new EngineHandle(new TerraBaseEngine(provider), storageEngine, path, backend);
        }
        catch
        {
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static IReadOnlyList<DocumentKey> CreateKeys(int count)
    {
        var tenant = new TenantId("larger-than-memory-tenant");
        var collection = new CollectionId("larger-than-memory-collection");
        return Enumerable.Range(0, count)
            .Select(index => new DocumentKey(tenant, collection, $"key-{index:D10}"))
            .ToArray();
    }

    private static JsonDocument CreatePayload(int payloadSize)
    {
        var body = new string('x', payloadSize);
        return JsonDocument.Parse(JsonSerializer.Serialize(new { payload = body }));
    }

    private static long EstimateDatasetBytes(IReadOnlyList<DocumentKey> keys, JsonElement payload)
    {
        var payloadBytes = Encoding.UTF8.GetByteCount(payload.GetRawText());
        return checked(keys.Sum(key => (long)Encoding.UTF8.GetByteCount(key.DocumentId) + payloadBytes));
    }

    private static long Percentile(long[] sortedTicks, double percentile)
    {
        if (sortedTicks.Length == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(sortedTicks.Length * percentile) - 1;
        return sortedTicks[Math.Clamp(index, 0, sortedTicks.Length - 1)];
    }

    private static double ToMicroseconds(long stopwatchTicks) => stopwatchTicks * 1_000_000d / Stopwatch.Frequency;

    private static long ToStopwatchTicks(TimeSpan elapsed) =>
        checked((long)(elapsed.TotalSeconds * Stopwatch.Frequency));

    private readonly record struct OperationBytes(long LogicalBytes, long ReturnedBytes);

    /// <summary>The durable backend under test.</summary>
    internal enum Backend
    {
        StorageEngine,
        ZoneTree,
    }

    /// <summary>The key-selection distribution.</summary>
    internal enum KeyDistributionKind
    {
        Uniform,
        Zipf,
    }

    internal enum ReadWriteMix
    {
        ReadOnly,
        ReadHeavy,
        WriteOnly,
    }

    /// <summary>Configuration for one bounded run.</summary>
    internal sealed record Configuration
    {
        public Backend Backend { get; init; } = Backend.StorageEngine;
        public int DatasetSize { get; init; } = 32_768;
        public int PayloadSize { get; init; } = 2_048;
        public long MemoryCapBytes { get; init; } = 256L * 1024 * 1024;
        public int Samples { get; init; } = 5;
        public int OperationsPerSample { get; init; } = 2_000;
        public int SustainedWriteOperations { get; init; } = 2_000;
        public int WarmupOperations { get; init; } = 1_000;
        public int Concurrency { get; init; } = 1;
        public int Seed { get; init; } = 1337;
        public double ZipfExponent { get; init; } = 1.2;
        public double ReadRatio { get; init; } = 0.8;
        public KeyDistributionKind InitialDistribution { get; init; } = KeyDistributionKind.Uniform;
        public bool CompactAfterSample { get; init; } = true;
        public IReadOnlyList<Shift> Shifts { get; init; } =
        [
            new Shift("uniform-read-heavy", 2_000, KeyDistributionKind.Uniform, ReadWriteMix.ReadHeavy),
            new Shift("zipf-read-heavy", 2_000, KeyDistributionKind.Zipf, ReadWriteMix.ReadHeavy),
            new Shift("zipf-write-heavy", 2_000, KeyDistributionKind.Zipf, ReadWriteMix.WriteOnly),
        ];

        internal string ActiveStoragePath { get; private set; } = string.Empty;

        internal void Validate()
        {
            if (!Enum.IsDefined(Backend))
            {
                throw new ArgumentException($"Unknown larger-than-memory backend '{Backend}'.");
            }

            if (DatasetSize <= 0 || PayloadSize <= 0 || MemoryCapBytes <= 0 || Samples <= 0 || OperationsPerSample <= 0 ||
                SustainedWriteOperations < 0 || WarmupOperations < 0 || Concurrency <= 0)
            {
                throw new ArgumentException("Dataset, payload, memory cap, samples, operations, and concurrency must be positive; warmup and sustained writes may be zero.");
            }

            if (ZipfExponent <= 0 || double.IsNaN(ZipfExponent) || double.IsInfinity(ZipfExponent))
            {
                throw new ArgumentException("ZipfExponent must be finite and greater than zero.");
            }

            if (ReadRatio is < 0 or > 1 || double.IsNaN(ReadRatio) || double.IsInfinity(ReadRatio))
            {
                throw new ArgumentException("ReadRatio must be finite and between zero and one.");
            }

            if (Shifts is null || Shifts.Count == 0 || Shifts.Any(static shift => shift is null || shift.Operations <= 0 || string.IsNullOrWhiteSpace(shift.Name)))
            {
                throw new ArgumentException("At least one positive workload shift is required.");
            }
        }
    }

    /// <summary>One distribution/mix shift in the sustained workload.</summary>
    internal sealed record Shift(
        string Name,
        int Operations,
        KeyDistributionKind Distribution,
        ReadWriteMix Mix);

    /// <summary>Machine-readable result for the larger-than-memory workload.</summary>
    internal sealed class RunResult
    {
        public string Status { get; set; } = "failed";
        public string Backend { get; init; } = string.Empty;
        public string RawSamplesPath { get; init; } = string.Empty;
        public string StoragePath { get; init; } = string.Empty;
        public int DatasetSize { get; init; }
        public int PayloadSize { get; init; }
        public long EstimatedLogicalDatasetBytes { get; init; }
        public long ConfiguredMemoryCapBytes { get; init; }
        public Configuration Configuration { get; init; } = new();
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset FinishedAtUtc { get; set; }
        public string? Error { get; set; }
        public List<SampleResult> Samples { get; } = [];
    }

    /// <summary>One post-warmup sample containing every workload phase.</summary>
    internal sealed class SampleResult
    {
        public int Sample { get; init; }
        public string Backend { get; init; } = string.Empty;
        public int DatasetSize { get; init; }
        public int PayloadSize { get; init; }
        public long EstimatedLogicalDatasetBytes { get; init; }
        public long ConfiguredMemoryCapBytes { get; init; }
        public int Seed { get; init; }
        public double ZipfExponent { get; init; }
        public string InitialDistribution { get; init; } = string.Empty;
        public string WorkloadShift { get; init; } = string.Empty;
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset FinishedAtUtc { get; init; }
        public List<PhaseResult> Phases { get; init; } = [];
    }

    /// <summary>One measured operation, reopen, or compaction phase.</summary>
    internal sealed class PhaseResult
    {
        public string Name { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string Distribution { get; init; } = string.Empty;
        public int Operations { get; init; }
        public double ElapsedMilliseconds { get; init; }
        public double ThroughputOpsPerSecond { get; init; }
        public double? P50Microseconds { get; init; }
        public double? P95Microseconds { get; init; }
        public double? P99Microseconds { get; init; }
        public LatencyHistogram LatencyHistogram { get; init; } = new();
        public long AllocatedBytes { get; init; }
        public double? AllocatedBytesPerOperation { get; init; }
        public double ProcessCpuMilliseconds { get; init; }
        public double ProcessCpuPercent { get; init; }
        public long ProcessWorkingSetBytesBefore { get; init; }
        public long ProcessWorkingSetBytesAfter { get; init; }
        public long ProcessWorkingSetDeltaBytes { get; init; }
        public long LogicalBytes { get; init; }
        public long ReturnedBytes { get; init; }
        public long? StorageBytesBefore { get; init; }
        public long? StorageBytesAfter { get; init; }
        public long? WalBytesBefore { get; init; }
        public long? WalBytesAfter { get; init; }
        public long? LogBytesDelta { get; init; }
        public double? WriteAmplification { get; init; }
        public string? Notes { get; init; }
    }

    /// <summary>Fixed wall-clock latency histogram with an overflow bucket.</summary>
    internal sealed class LatencyHistogram
    {
        public Dictionary<string, long> Buckets { get; init; } = new(StringComparer.Ordinal);

        public static LatencyHistogram Create(long[] stopwatchTicks)
        {
            var histogram = new LatencyHistogram();
            foreach (var tick in stopwatchTicks)
            {
                var nanoseconds = tick * 1_000_000_000d / Stopwatch.Frequency;
                var bucket = HistogramBucketUpperBoundsNanoseconds.FirstOrDefault(bound => nanoseconds <= bound);
                var label = bucket == 0 ? ">100ms" : $"<={bucket.ToString(CultureInfo.InvariantCulture)}ns";
                histogram.Buckets[label] = histogram.Buckets.GetValueOrDefault(label) + 1;
            }

            return histogram;
        }
    }

    private readonly record struct StorageMetrics(long TotalBytes, long WalBytes)
    {
        public static StorageMetrics? Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return null;
            }

            try
            {
                var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Select(static file => new FileInfo(file))
                    .ToArray();
                var total = files.Sum(static file => file.Length);
                var wal = files
                    .Where(static file => string.Equals(file.Name, "wal", StringComparison.OrdinalIgnoreCase))
                    .Sum(static file => file.Length);
                return new StorageMetrics(total, wal);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    private sealed class EngineHandle : IAsyncDisposable
    {
        private readonly string path;
        private readonly Backend backend;

        public EngineHandle(TerraBaseEngine engine, StorageEngine? storageEngine, string path, Backend backend)
        {
            Engine = engine;
            StorageEngine = storageEngine;
            this.path = path;
            this.backend = backend;
        }

        public TerraBaseEngine Engine { get; private set; }
        public StorageEngine? StorageEngine { get; private set; }

        public async ValueTask ReopenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Engine.DisposeAsync().ConfigureAwait(false);
            var replacement = Open(path, backend);
            Engine = replacement.Engine;
            StorageEngine = replacement.StorageEngine;
        }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private sealed class KeyDistribution
    {
        private readonly double[] cumulative;

        public KeyDistribution(int count, double exponent)
        {
            cumulative = new double[count];
            var total = 0d;
            for (var index = 1; index <= count; index++)
            {
                total += 1d / Math.Pow(index, exponent);
                cumulative[index - 1] = total;
            }

            for (var index = 0; index < cumulative.Length; index++)
            {
                cumulative[index] /= total;
            }
        }

        public int NextZipf(Random random)
        {
            var value = random.NextDouble();
            var index = Array.BinarySearch(cumulative, value);
            if (index < 0)
            {
                index = ~index;
            }

            return Math.Min(index, cumulative.Length - 1);
        }
    }
}
