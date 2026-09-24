using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
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
    private const int MaxTrackedRewrittenKeys = 1_000_000;
    private const int MaxDeleteOperations = 1_000_000;

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
        configuration = ApplyEnvironmentOverrides(configuration);
        configuration.Validate();
        KeyDistribution.ValidateBoundedSampling();

        var receiptDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(receiptDirectory);
        var rawPath = Path.Combine(receiptDirectory, $"raw-samples-larger-than-memory-{configuration.Backend}.jsonl");
        var storagePath = Path.Combine(receiptDirectory, "larger-than-memory", configuration.Backend.ToString());
        Directory.CreateDirectory(storagePath);
        configuration.ActiveStoragePath = storagePath;

        var keys = new KeySpace(configuration.DatasetSize);
        using var payloadFactory = new PayloadFactory(configuration);
        var expectedState = new ExpectedState();
        var estimatedDatasetBytes = EstimateDatasetBytes(keys, payloadFactory.SerializedPayloadBytes);
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
            PayloadMode = configuration.PayloadMode.ToString(),
            PayloadGenerator = payloadFactory.GeneratorName,
            DeleteOperations = configuration.DeleteOperations,
            EstimatedLogicalDatasetBytes = estimatedDatasetBytes,
            ConfiguredMemoryCapBytes = configuration.MemoryCapBytes,
            Configuration = configuration,
            StartedAtUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            await using var handle = Open(storagePath, configuration.Backend);
            await SeedAsync(handle.Engine.Documents, keys, payloadFactory, cancellationToken).ConfigureAwait(false);
            await VerifyCollectionAsync(
                handle.Engine.Documents,
                keys,
                payloadFactory,
                expectedState,
                "post-seed",
                cancellationToken).ConfigureAwait(false);
            var seededStorage = StorageMetrics.Read(storagePath);
            result.SeededStorageBytes = seededStorage?.TotalBytes;
            result.SeededStorageExceedsMemoryCap = seededStorage is { } metrics
                ? metrics.TotalBytes > configuration.MemoryCapBytes
                : null;
            var distribution = new KeyDistribution(keys.Count, configuration.ZipfExponent);
            await RunOperationsAsync(
                handle.Engine.Documents,
                configuration,
                keys,
                payloadFactory,
                expectedState,
                configuration.WarmupOperations,
                distribution,
                "warmup",
                ReadWriteMix.ReadHeavy,
                configuration.InitialDistribution,
                randomSalt: 0,
                cancellationToken).ConfigureAwait(false);

            await using var raw = new StreamWriter(rawPath, append: false, new UTF8Encoding(false));
            for (var sampleIndex = 0; sampleIndex < configuration.Samples; sampleIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sample = await RunSampleAsync(
                    handle,
                    configuration,
                    keys,
                    payloadFactory,
                    expectedState,
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
        KeySpace keys,
        PayloadFactory payloadFactory,
        ExpectedState expectedState,
        KeyDistribution distribution,
        int sampleIndex,
        CancellationToken cancellationToken)
    {
        var sampleStarted = DateTimeOffset.UtcNow;
        var phases = new List<PhaseResult>();
        DeleteResult? deletes = null;
        IReadOnlySet<int>? deletedIndexes = null;
        phases.Add(await MeasureOperationsAsync(
            handle.Engine.Documents,
            configuration,
            keys,
            payloadFactory,
            expectedState,
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
        await VerifyCollectionAsync(
            handle.Engine.Documents,
            keys,
            payloadFactory,
            expectedState,
            "post-reopen",
            cancellationToken).ConfigureAwait(false);
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
            payloadFactory,
            expectedState,
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
            payloadFactory,
            expectedState,
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
                payloadFactory,
                expectedState,
                distribution,
                configuration.SustainedWriteOperations,
                "sustained-writes",
                ReadWriteMix.WriteOnly,
                shift.Distribution,
                sampleIndex * 17 + 4,
                cancellationToken).ConfigureAwait(false));
        }

        // Keep the delete set at the end of the final sample. Earlier samples
        // intentionally retain the complete key space so their read phases
        // remain comparable and do not accidentally probe tombstones.
        if (sampleIndex == configuration.Samples - 1 && configuration.DeleteOperations > 0)
        {
            if (configuration.Backend != Backend.StorageEngine)
            {
                deletes = new DeleteResult
                {
                    Status = "skipped",
                    RequestedCount = configuration.DeleteOperations,
                    SurvivingCount = configuration.DatasetSize,
                    Notes = "Skipped: the larger-than-memory delete qualification targets the StorageEngine backend."
                };
            }
            else
            {
                var deletePlan = CreateDeletePlan(keys, configuration.DeleteOperations);
                deletedIndexes = deletePlan.IndexSet;
                var deleteMeasurement = await MeasureDeletesAsync(
                    handle.Engine.Documents,
                    configuration,
                    keys,
                    payloadFactory,
                    expectedState,
                    deletePlan,
                    cancellationToken).ConfigureAwait(false);
                phases.Add(deleteMeasurement.Phase);

                var immediateVerification = await VerifyDeleteStateAsync(
                    handle.Engine.Documents,
                    keys,
                    payloadFactory,
                    expectedState,
                    deletedIndexes,
                    "post-delete",
                    cancellationToken).ConfigureAwait(false);

                var deleteReopen = await MeasureDeleteReopenAsync(
                    handle,
                    configuration,
                    keys,
                    payloadFactory,
                    expectedState,
                    deletedIndexes,
                    cancellationToken).ConfigureAwait(false);
                phases.Add(deleteReopen.Phase);

                deletes = new DeleteResult
                {
                    Status = "complete",
                    RequestedCount = deletePlan.Indexes.Length,
                    DeletedCount = deleteMeasurement.DeletedCount,
                    SurvivingCount = keys.Count - deletePlan.Indexes.Length,
                    DeletedAbsenceVerified = immediateVerification.DeletedAbsenceVerified,
                    SurvivingBodiesVerified = immediateVerification.SurvivingBodiesVerified,
                    ColdReopenDeletedAbsenceVerified = deleteReopen.Verification.DeletedAbsenceVerified,
                    ColdReopenSurvivingBodiesVerified = deleteReopen.Verification.SurvivingBodiesVerified,
                    DeleteElapsedMilliseconds = deleteMeasurement.Phase.ElapsedMilliseconds,
                    ColdReopenElapsedMilliseconds = deleteReopen.Phase.ElapsedMilliseconds,
                    Notes = "The final sample deletes a deterministic, evenly spread key set after sustained upserts, verifies the survivors and absences, then reopens the StorageEngine and repeats both checks."
                };
            }
        }

        if (configuration.CompactAfterSample)
        {
            phases.Add(await MeasureCompactionAsync(handle, configuration, sampleIndex, cancellationToken).ConfigureAwait(false));
            await VerifyCollectionAsync(
                handle.Engine.Documents,
                keys,
                payloadFactory,
                expectedState,
                "post-compaction",
                cancellationToken,
                deletedIndexes).ConfigureAwait(false);
        }

        return new SampleResult
        {
            Sample = sampleIndex + 1,
            StartedAtUtc = sampleStarted,
            FinishedAtUtc = DateTimeOffset.UtcNow,
            Backend = configuration.Backend.ToString(),
            DatasetSize = configuration.DatasetSize,
            PayloadSize = configuration.PayloadSize,
            PayloadMode = configuration.PayloadMode.ToString(),
            PayloadGenerator = payloadFactory.GeneratorName,
            EstimatedLogicalDatasetBytes = EstimateDatasetBytes(keys, payloadFactory.SerializedPayloadBytes),
            ConfiguredMemoryCapBytes = configuration.MemoryCapBytes,
            Seed = configuration.Seed,
            ZipfExponent = configuration.ZipfExponent,
            InitialDistribution = configuration.InitialDistribution.ToString(),
            WorkloadShift = shift.Name,
            DeleteOperations = configuration.DeleteOperations,
            Deletes = deletes,
            Phases = phases,
        };
    }

    private static async Task<PhaseResult> MeasureOperationsAsync(
        DocumentStore store,
        Configuration configuration,
        KeySpace keys,
        PayloadFactory payloadFactory,
        ExpectedState expectedState,
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
            payloadFactory,
            expectedState,
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
        KeySpace keys,
        PayloadFactory payloadFactory,
        ExpectedState expectedState,
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
                    var key = keys.GetKey(keyIndex);
                    var write = mix switch
                    {
                        ReadWriteMix.WriteOnly => true,
                        ReadWriteMix.ReadOnly => false,
                        _ => random.NextDouble() >= configuration.ReadRatio,
                    };
                    var started = Stopwatch.GetTimestamp();
                    var bytes = write
                        ? await WriteAsync(store, key, payloadFactory, expectedState, configuration.Concurrency == 1, cancellationToken).ConfigureAwait(false)
                        : await ReadAsync(store, key, payloadFactory, expectedState, configuration.Concurrency == 1, cancellationToken).ConfigureAwait(false);
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
        PayloadFactory payloadFactory,
        ExpectedState expectedState,
        bool requireExactVersion,
        CancellationToken cancellationToken)
    {
        var document = await store.ReadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The larger-than-memory workload read missed seeded key {key.DocumentId}.");
        if (document.Key != key)
        {
            throw new InvalidDataException($"The larger-than-memory read returned {document.Key.DocumentId}; expected {key.DocumentId}.");
        }

        payloadFactory.ValidateDocument(
            document,
            expectedState.GetExpectedVersion(key),
            requireExactVersion,
            "read");
        var bytes = Encoding.UTF8.GetByteCount(document.Body.GetRawText());
        return new OperationBytes(bytes, bytes);
    }

    private static async ValueTask<OperationBytes> WriteAsync(
        DocumentStore store,
        DocumentKey key,
        PayloadFactory payloadFactory,
        ExpectedState expectedState,
        bool requireExactVersion,
        CancellationToken cancellationToken)
    {
        using var generatedPayload = payloadFactory.CreateFor(key);
        var payload = generatedPayload?.RootElement ?? payloadFactory.SharedPayload;
        var result = await store.UpsertAsync(key, payload, DocumentPrecondition.Any, cancellationToken).ConfigureAwait(false);
        if (result.Outcome is not (WriteOutcome.Created or WriteOutcome.Updated))
        {
            throw new InvalidOperationException($"The larger-than-memory workload write failed for {key.DocumentId}: {result.Outcome}.");
        }

        var document = result.Document
            ?? throw new InvalidDataException($"The larger-than-memory workload write returned no document for {key.DocumentId}.");
        if (document.Key != key)
        {
            throw new InvalidDataException($"The larger-than-memory write returned {document.Key.DocumentId}; expected {key.DocumentId}.");
        }

        payloadFactory.ValidateDocument(document, document.Version, exactVersion: true, "write");
        expectedState.RecordWrite(key, document.Version, requireExactVersion);

        return new OperationBytes(payloadFactory.SerializedPayloadBytes, 0);
    }

    private static DeletePlan CreateDeletePlan(KeySpace keys, int count)
    {
        if (count <= 0 || count > keys.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"The delete set must contain between one and {keys.Count:N0} keys.");
        }

        var indexes = new int[count];
        var indexSet = new HashSet<int>();
        for (var position = 0; position < count; position++)
        {
            // Evenly spread tombstones through the ordered key space. Since
            // count is bounded by the dataset size, integer division yields a
            // unique, sorted set without retaining document objects.
            var index = (int)((long)position * keys.Count / count);
            indexes[position] = index;
            indexSet.Add(index);
        }

        return new DeletePlan(indexes, indexSet);
    }

    private static async Task<DeleteMeasurement> MeasureDeletesAsync(
        DocumentStore store,
        Configuration configuration,
        KeySpace keys,
        PayloadFactory payloadFactory,
        ExpectedState expectedState,
        DeletePlan deletePlan,
        CancellationToken cancellationToken)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var rssBefore = process.WorkingSet64;
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var storageBefore = StorageMetrics.Read(configuration.ActiveStoragePath);
        var latencies = new long[deletePlan.Indexes.Length];
        var started = Stopwatch.GetTimestamp();

        for (var position = 0; position < deletePlan.Indexes.Length; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = keys.GetKey(deletePlan.Indexes[position]);
            var expectedVersion = expectedState.GetExpectedVersion(key);
            var operationStarted = Stopwatch.GetTimestamp();
            var result = await store.DeleteAsync(
                key,
                DocumentPrecondition.Version(expectedVersion),
                cancellationToken).ConfigureAwait(false);
            latencies[position] = Stopwatch.GetTimestamp() - operationStarted;
            if (result.Outcome != WriteOutcome.Deleted || result.Document is null)
            {
                throw new InvalidDataException(
                    $"The larger-than-memory delete for {key.DocumentId} returned {result.Outcome}.");
            }

            if (result.Document.Key != key)
            {
                throw new InvalidDataException(
                    $"The larger-than-memory delete returned {result.Document.Key.DocumentId}; expected {key.DocumentId}.");
            }

            payloadFactory.ValidateDocument(result.Document, expectedVersion, exactVersion: true, "delete");
        }

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
        var phase = new PhaseResult
        {
            Name = "deletes",
            Kind = "delete",
            Distribution = "bounded-set",
            Operations = deletePlan.Indexes.Length,
            ElapsedMilliseconds = elapsed.TotalMilliseconds,
            ThroughputOpsPerSecond = deletePlan.Indexes.Length / Math.Max(elapsed.TotalSeconds, double.Epsilon),
            P50Microseconds = ToMicroseconds(Percentile(latencies, 0.50)),
            P95Microseconds = ToMicroseconds(Percentile(latencies, 0.95)),
            P99Microseconds = ToMicroseconds(Percentile(latencies, 0.99)),
            LatencyHistogram = LatencyHistogram.Create(latencies),
            AllocatedBytes = allocated,
            AllocatedBytesPerOperation = allocated / (double)deletePlan.Indexes.Length,
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
            LogBytesDelta = logDelta,
            WriteAmplification = null,
            Notes = "Deterministic, evenly spread StorageEngine tombstone set after sustained upserts."
        };
        return new DeleteMeasurement(phase, deletePlan.Indexes.Length);
    }

    private static async Task<DeleteReopenMeasurement> MeasureDeleteReopenAsync(
        EngineHandle handle,
        Configuration configuration,
        KeySpace keys,
        PayloadFactory payloadFactory,
        ExpectedState expectedState,
        IReadOnlySet<int> deletedIndexes,
        CancellationToken cancellationToken)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var rssBefore = process.WorkingSet64;
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var storageBefore = StorageMetrics.Read(configuration.ActiveStoragePath);
        var started = Stopwatch.GetTimestamp();
        await handle.ReopenAsync(cancellationToken).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var rssAfter = process.WorkingSet64;
        var allocated = Math.Max(0, GC.GetTotalAllocatedBytes(false) - allocatedBefore);
        var storageAfter = StorageMetrics.Read(configuration.ActiveStoragePath);
        var verification = await VerifyDeleteStateAsync(
            handle.Engine.Documents,
            keys,
            payloadFactory,
            expectedState,
            deletedIndexes,
            "post-delete-reopen",
            cancellationToken).ConfigureAwait(false);
        var phase = new PhaseResult
        {
            Name = "delete-cold-reopen",
            Kind = "reopen",
            Distribution = "bounded-set",
            Operations = 0,
            ElapsedMilliseconds = elapsed.TotalMilliseconds,
            ThroughputOpsPerSecond = 0,
            P50Microseconds = null,
            P95Microseconds = null,
            P99Microseconds = null,
            LatencyHistogram = new LatencyHistogram(),
            AllocatedBytes = allocated,
            AllocatedBytesPerOperation = null,
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
            Notes = "Elapsed time covers disposal of the post-delete engine and durable reopen; the phase also records the exact survivor and absence verification that follows."
        };
        return new DeleteReopenMeasurement(phase, verification);
    }

    private static async Task<VerificationResult> VerifyDeleteStateAsync(
        DocumentStore store,
        KeySpace keys,
        PayloadFactory payloadFactory,
        ExpectedState expectedState,
        IReadOnlySet<int> deletedIndexes,
        string phase,
        CancellationToken cancellationToken)
    {
        var survivingBodiesVerified = await VerifyCollectionAsync(
            store,
            keys,
            payloadFactory,
            expectedState,
            phase,
            cancellationToken,
            deletedIndexes).ConfigureAwait(false);
        var deletedAbsenceVerified = 0;
        foreach (var index in deletedIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await store.ReadAsync(keys.GetKey(index), cancellationToken).ConfigureAwait(false);
            if (document is not null)
            {
                throw new InvalidDataException(
                    $"The larger-than-memory {phase} read returned deleted key {document.Key.DocumentId}.");
            }

            deletedAbsenceVerified++;
        }

        return new VerificationResult(deletedAbsenceVerified, survivingBodiesVerified);
    }

    private static async Task<int> VerifyCollectionAsync(
        DocumentStore store,
        KeySpace keys,
        PayloadFactory payloadFactory,
        ExpectedState expectedState,
        string phase,
        CancellationToken cancellationToken,
        IReadOnlySet<int>? deletedIndexes = null)
    {
        if (keys.Count == 0)
        {
            throw new InvalidOperationException("The larger-than-memory workload cannot verify an empty collection.");
        }

        var firstKey = keys.GetKey(0);
        var tenant = firstKey.Tenant;
        var collection = firstKey.Collection;
        var verified = 0;
        var expectedIndex = 0;
        var expectedCount = keys.Count - (deletedIndexes?.Count ?? 0);
        await foreach (var document in store.ScanAsync(tenant, collection, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (deletedIndexes?.Contains(expectedIndex) == true)
            {
                expectedIndex++;
            }

            if (verified >= expectedCount || expectedIndex >= keys.Count)
            {
                throw new InvalidDataException($"The {phase} scan returned more than {expectedCount:N0} surviving documents.");
            }

            var expectedKey = keys.GetKey(expectedIndex);
            if (document.Key != expectedKey)
            {
                throw new InvalidDataException(
                    $"The {phase} scan returned {document.Key.DocumentId} at key index {expectedIndex:N0}; expected {expectedKey.DocumentId}.");
            }

            payloadFactory.ValidateDocument(
                document,
                expectedState.GetExpectedVersion(document.Key),
                exactVersion: true,
                phase);
            verified++;
            expectedIndex++;
        }

        while (deletedIndexes?.Contains(expectedIndex) == true)
        {
            expectedIndex++;
        }

        if (verified != expectedCount || expectedIndex != keys.Count)
        {
            throw new InvalidDataException($"The {phase} scan returned {verified:N0} surviving documents; expected {expectedCount:N0}.");
        }

        return verified;
    }

    private static async Task SeedAsync(
        DocumentStore store,
        KeySpace keys,
        PayloadFactory payloadFactory,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < keys.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = keys.GetKey(index);
            using var generatedPayload = payloadFactory.CreateFor(key);
            var payload = generatedPayload?.RootElement ?? payloadFactory.SharedPayload;
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
            return new EngineHandle(
                new TerraBaseEngine(
                    provider,
                    options: new TerraBaseOptions
                    {
                        AllowSchemaless = true,
                    }),
                storageEngine,
                path,
                backend);
        }
        catch
        {
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static long EstimateDatasetBytes(KeySpace keys, long serializedPayloadBytes) =>
        checked(keys.Count * (KeySpace.DocumentIdBytes + serializedPayloadBytes));

    /// <summary>
    /// Describes the deterministic key range without retaining one managed key
    /// object or document identifier per row.
    /// </summary>
    private sealed class KeySpace
    {
        private readonly TenantId tenant = new("larger-than-memory-tenant");
        private readonly CollectionId collection = new("larger-than-memory-collection");

        public KeySpace(int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "The key space must contain at least one key.");
            }

            Count = count;
        }

        public const int DocumentIdBytes = 14;

        public int Count { get; }

        public DocumentKey GetKey(int index)
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "The key index is outside the key space.");
            }

            return new DocumentKey(tenant, collection, $"key-{index:D10}");
        }
    }

    private static Configuration ApplyEnvironmentOverrides(Configuration configuration)
    {
        var rawMode = Environment.GetEnvironmentVariable("TERRABASE_LARGER_MEMORY_PAYLOAD_MODE");
        if (string.IsNullOrWhiteSpace(rawMode))
        {
            return configuration;
        }

        var normalized = rawMode.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        var mode = normalized.Equals("perkeyincompressible", StringComparison.OrdinalIgnoreCase)
            ? PayloadMode.PerKeyIncompressible
            : normalized.Equals("repeated", StringComparison.OrdinalIgnoreCase)
                ? PayloadMode.Repeated
                : throw new ArgumentException(
                    "TERRABASE_LARGER_MEMORY_PAYLOAD_MODE must be 'repeated' or 'per-key-incompressible'.");
        return configuration with { PayloadMode = mode };
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
    private sealed record DeletePlan(int[] Indexes, HashSet<int> IndexSet);
    private readonly record struct DeleteMeasurement(PhaseResult Phase, int DeletedCount);
    private readonly record struct DeleteReopenMeasurement(PhaseResult Phase, VerificationResult Verification);
    private readonly record struct VerificationResult(int DeletedAbsenceVerified, int SurvivingBodiesVerified);

    /// <summary>
    /// Tracks only versions for keys that the workload rewrites. Seeded keys
    /// intentionally use the stateless version-one default, so this does not
    /// allocate a second dataset-sized expected-state table. The distinct-key
    /// update state is explicitly bounded so a write-heavy profile cannot grow
    /// managed state without limit.
    /// </summary>
    private sealed class ExpectedState
    {
        private readonly object gate = new();
        private readonly Dictionary<DocumentKey, ulong> rewrittenVersions = [];

        public ulong GetExpectedVersion(DocumentKey key)
        {
            lock (gate)
            {
                return rewrittenVersions.TryGetValue(key, out var version) ? version : 1;
            }
        }

        public void RecordWrite(DocumentKey key, ulong version, bool requireExactVersion)
        {
            if (version == 0)
            {
                throw new InvalidDataException($"The larger-than-memory workload received version zero for {key.DocumentId}.");
            }

            lock (gate)
            {
                var previous = rewrittenVersions.TryGetValue(key, out var current) ? current : 1;
                if (requireExactVersion && version != checked(previous + 1))
                {
                    throw new InvalidDataException(
                        $"The larger-than-memory workload write for {key.DocumentId} returned version {version}; expected {previous + 1}.");
                }

                if (version > previous)
                {
                    if (!rewrittenVersions.ContainsKey(key) && rewrittenVersions.Count >= MaxTrackedRewrittenKeys)
                    {
                        throw new InvalidOperationException(
                            $"The larger-than-memory workload exceeded its bounded rewritten-key state limit of {MaxTrackedRewrittenKeys:N0}. Reduce the distinct update count or use a profile with fewer tracked rewrites.");
                    }

                    rewrittenVersions[key] = version;
                }
            }
        }
    }

    private sealed class PayloadFactory : IDisposable
    {
        private readonly Configuration configuration;
        private readonly JsonDocument? sharedPayload;
        private readonly string? repeatedBody;

        public PayloadFactory(Configuration configuration)
        {
            this.configuration = configuration;
            if (configuration.PayloadMode == PayloadMode.Repeated)
            {
                repeatedBody = new string('x', configuration.PayloadSize);
                sharedPayload = CreateRepeatedPayload(repeatedBody);
            }

            SerializedPayloadBytes = Encoding.UTF8.GetByteCount(
                JsonSerializer.Serialize(new { payload = new string('x', configuration.PayloadSize) }));
        }

        public long SerializedPayloadBytes { get; }

        public string GeneratorName => configuration.PayloadMode switch
        {
            PayloadMode.Repeated => "repeated-x",
            PayloadMode.PerKeyIncompressible => "sha256-base64url-per-key",
            _ => throw new InvalidOperationException($"Unsupported payload mode '{configuration.PayloadMode}'."),
        };

        public JsonElement SharedPayload => sharedPayload?.RootElement
            ?? throw new InvalidOperationException("A shared payload is only available for the repeated payload mode.");

        public void ValidateDocument(
            Document document,
            ulong expectedVersion,
            bool exactVersion,
            string phase)
        {
            if (exactVersion && document.Version != expectedVersion)
            {
                throw new InvalidDataException(
                    $"The larger-than-memory {phase} for {document.Key.DocumentId} returned version {document.Version}; expected {expectedVersion}.");
            }

            var body = document.Body;
            if (body.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"The larger-than-memory {phase} for {document.Key.DocumentId} returned a non-object body.");
            }

            var properties = body.EnumerateObject();
            if (!properties.MoveNext())
            {
                throw new InvalidDataException($"The larger-than-memory {phase} for {document.Key.DocumentId} returned an empty body.");
            }

            var payload = properties.Current;
            var expectedBody = configuration.PayloadMode == PayloadMode.Repeated
                ? repeatedBody!
                : CreateIncompressibleBody(configuration.PayloadSize, configuration.Seed, document.Key.DocumentId);
            if (!string.Equals(payload.Name, "payload", StringComparison.Ordinal)
                || payload.Value.ValueKind != JsonValueKind.String
                || !string.Equals(payload.Value.GetString(), expectedBody, StringComparison.Ordinal)
                || properties.MoveNext())
            {
                throw new InvalidDataException($"The larger-than-memory {phase} for {document.Key.DocumentId} did not match the expected body.");
            }
        }

        public JsonDocument? CreateFor(DocumentKey key) => configuration.PayloadMode switch
        {
            PayloadMode.Repeated => null,
            PayloadMode.PerKeyIncompressible => CreateIncompressiblePayload(
                configuration.PayloadSize,
                configuration.Seed,
                key.DocumentId),
            _ => throw new InvalidOperationException($"Unsupported payload mode '{configuration.PayloadMode}'."),
        };

        public void Dispose() => sharedPayload?.Dispose();

        private static JsonDocument CreateRepeatedPayload(string body)
        {
            return JsonDocument.Parse(JsonSerializer.Serialize(new { payload = body }));
        }

        private static JsonDocument CreateIncompressiblePayload(int payloadSize, int seed, string key)
        {
            var body = CreateIncompressibleBody(payloadSize, seed, key);
            return JsonDocument.Parse(JsonSerializer.Serialize(new { payload = body }));
        }

        private static string CreateIncompressibleBody(int payloadSize, int seed, string key)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var seedBytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(seedBytes, seed);
            var blockBytes = new byte[sizeof(int)];
            var body = new StringBuilder(payloadSize);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var block = 0; body.Length < payloadSize; block++)
            {
                hash.AppendData(seedBytes);
                hash.AppendData(keyBytes);
                BinaryPrimitives.WriteInt32LittleEndian(blockBytes, block);
                hash.AppendData(blockBytes);
                var encoded = Convert.ToBase64String(hash.GetHashAndReset());
                for (var index = 0; index < encoded.Length && body.Length < payloadSize; index++)
                {
                    body.Append(encoded[index] switch
                    {
                        '+' => '-',
                        '/' => '_',
                        '=' => '.',
                        var value => value,
                    });
                }
            }

            return body.ToString();
        }
    }

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

    /// <summary>How seed and write payloads are generated.</summary>
    internal enum PayloadMode
    {
        Repeated,
        PerKeyIncompressible,
    }

    /// <summary>Configuration for one bounded run.</summary>
    internal sealed record Configuration
    {
        public Backend Backend { get; init; } = Backend.StorageEngine;
        public int DatasetSize { get; init; } = 32_768;
        public int PayloadSize { get; init; } = 2_048;
        public PayloadMode PayloadMode { get; init; } = PayloadMode.Repeated;
        public string PayloadModeName => PayloadMode.ToString();
        public long MemoryCapBytes { get; init; } = 256L * 1024 * 1024;
        public int Samples { get; init; } = 5;
        public int OperationsPerSample { get; init; } = 2_000;
        public int SustainedWriteOperations { get; init; } = 2_000;
        public int DeleteOperations { get; init; }
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

        internal string ActiveStoragePath { get; set; } = string.Empty;

        internal void Validate()
        {
            if (!Enum.IsDefined(Backend))
            {
                throw new ArgumentException($"Unknown larger-than-memory backend '{Backend}'.");
            }

            if (!Enum.IsDefined(PayloadMode))
            {
                throw new ArgumentException($"Unknown larger-than-memory payload mode '{PayloadMode}'.");
            }

            if (DatasetSize <= 0 || PayloadSize <= 0 || MemoryCapBytes <= 0 || Samples <= 0 || OperationsPerSample <= 0 ||
                SustainedWriteOperations < 0 || DeleteOperations < 0 || DeleteOperations > Math.Min(DatasetSize, MaxDeleteOperations) || WarmupOperations < 0 || Concurrency <= 0)
            {
                throw new ArgumentException($"Dataset, payload, memory cap, samples, operations, and concurrency must be positive; warmup, sustained writes, and deletes may be zero; deletes must not exceed {MaxDeleteOperations:N0} or the dataset size.");
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
        public string PayloadMode { get; init; } = string.Empty;
        public string PayloadGenerator { get; init; } = string.Empty;
        public int DeleteOperations { get; init; }
        public long EstimatedLogicalDatasetBytes { get; init; }
        public long ConfiguredMemoryCapBytes { get; init; }
        public long? SeededStorageBytes { get; set; }
        public bool? SeededStorageExceedsMemoryCap { get; set; }
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
        public string PayloadMode { get; init; } = string.Empty;
        public string PayloadGenerator { get; init; } = string.Empty;
        public long EstimatedLogicalDatasetBytes { get; init; }
        public long ConfiguredMemoryCapBytes { get; init; }
        public int Seed { get; init; }
        public double ZipfExponent { get; init; }
        public string InitialDistribution { get; init; } = string.Empty;
        public string WorkloadShift { get; init; } = string.Empty;
        public int DeleteOperations { get; init; }
        public DeleteResult? Deletes { get; init; }
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

    /// <summary>Delete qualification counts and durable verification evidence.</summary>
    internal sealed class DeleteResult
    {
        public string Status { get; init; } = string.Empty;
        public int RequestedCount { get; init; }
        public int DeletedCount { get; init; }
        public int SurvivingCount { get; init; }
        public int DeletedAbsenceVerified { get; init; }
        public int SurvivingBodiesVerified { get; init; }
        public int ColdReopenDeletedAbsenceVerified { get; init; }
        public int ColdReopenSurvivingBodiesVerified { get; init; }
        public double DeleteElapsedMilliseconds { get; init; }
        public double ColdReopenElapsedMilliseconds { get; init; }
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

    /// <summary>
    /// Samples the configured Zipf-shaped rank distribution with a continuous
    /// inverse CDF. The sampler retains only scalar parameters, so its memory
    /// use does not grow with the dataset size.
    /// </summary>
    private sealed class KeyDistribution
    {
        private readonly int count;
        private readonly double exponentComplement;
        private readonly double upperPower;

        public KeyDistribution(int count, double exponent)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "The distribution must contain at least one key.");
            }

            this.count = count;
            exponentComplement = 1d - exponent;
            upperPower = Math.Pow(count + 1d, exponentComplement);
        }

        public int NextZipf(Random random)
        {
            ArgumentNullException.ThrowIfNull(random);
            return SampleZipf(random.NextDouble());
        }

        public static void ValidateBoundedSampling()
        {
            var distribution = new KeyDistribution(2, 1.2);
            var first = distribution.SampleZipf(0d);
            var last = distribution.SampleZipf(Math.BitDecrement(1d));
            if (first != 0 || last != 1)
            {
                throw new InvalidOperationException(
                    $"The bounded Zipf sampler endpoint check returned {first} and {last}; expected 0 and 1.");
            }

            foreach (var value in new[] { 0d, 0.25d, 0.5d, Math.BitDecrement(1d) })
            {
                var index = distribution.SampleZipf(value);
                if ((uint)index >= 2u)
                {
                    throw new InvalidOperationException($"The bounded Zipf sampler returned out-of-range index {index}.");
                }
            }
        }

        private int SampleZipf(double value)
        {
            var rank = exponentComplement == 0d
                ? Math.Exp(value * Math.Log(count))
                : Math.Pow(1d + value * (upperPower - 1d), 1d / exponentComplement);
            var index = (int)Math.Min(count - 1d, Math.Floor(rank) - 1d);
            if (index < 0)
            {
                return 0;
            }

            return index;
        }
    }
}
