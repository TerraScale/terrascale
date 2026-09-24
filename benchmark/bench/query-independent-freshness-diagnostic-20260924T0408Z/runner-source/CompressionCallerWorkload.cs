using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZoneTree;
using ZoneTree.Backup;
using ZoneTree.Comparers;
using ZoneTree.Hashers;
using ZoneTree.Options;
using ZoneTree.Serializers;

internal enum CompressionCaller
{
    Primitive,
    DiskSegment,
    LiveBackup,
    All,
}

internal enum CompressionCallerPhase
{
    All,
    ColdWrite,
    ColdRead,
    WarmWrite,
    WarmRead,
}

internal sealed record CompressionCallerOptions(
    int Samples,
    int WarmupOperations,
    int OperationsPerSample,
    int Concurrency,
    bool ConcurrencySpecified,
    int Seed,
    int Lz4CompressionLevel,
    CompressionCaller Caller,
    string? Profile = null,
    CompressionMethod? Method = null,
    CompressionCallerPhase Phase = CompressionCallerPhase.All);

/// <summary>
/// Measures the two supported persisted callers separately from the primitive
/// DataCompression boundary. This runner intentionally uses ZoneTree directly
/// for disk segments so provider behavior is not mislabeled as provider
/// throughput.
/// </summary>
internal static class CompressionCallerWorkload
{
    internal const string TimingMethodologyVersion = "real-caller-warm-window-v2";

    private const int DiskBlockBytes = 4 * 1024 * 1024;
    private const int BackupBlockBytes = 1 * 1024 * 1024;
    private const int ControlledMutableSegmentMaxItems = 1_000;
    private const int ControlledDiskSegmentMaxItems = 10_000;
    private const string WalMode = "Sync";
    private const string SegmentThresholdMode = "controlled mutable threshold";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions RawJsonOptions = new(JsonSerializerDefaults.Web);
    private static long blackHole;

    public static async Task<bool> RunAsync(
        CompressionCallerOptions options,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ValidateSelection(options);
        var profiles = SelectedProfiles(options);
        var methods = SelectedMethods(options);
        var startedAt = DateTimeOffset.UtcNow;
        var cases = new List<object>();
        var rawPath = Path.Combine(outputDirectory, "compression-real-raw-samples.jsonl");
        await using var raw = new StreamWriter(
            rawPath,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            if (options.Caller is CompressionCaller.DiskSegment or CompressionCaller.All)
            {
                foreach (var profile in profiles)
                {
                    foreach (var method in methods)
                    {
                        foreach (var concurrency in DiskConcurrency(options))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var result = await RunDiskCaseAsync(
                                options,
                                profile,
                                method,
                                concurrency,
                                raw,
                                cancellationToken).ConfigureAwait(false);
                            cases.Add(result);
                        }
                    }
                }
            }

            if (options.Caller is CompressionCaller.LiveBackup or CompressionCaller.All)
            {
                foreach (var profile in profiles)
                {
                    foreach (var method in methods)
                    {
                        foreach (var concurrency in BackupConcurrency(options))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var result = await RunBackupCaseAsync(
                                options,
                                profile,
                                method,
                                concurrency,
                                raw,
                                cancellationToken).ConfigureAwait(false);
                            cases.Add(result);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cases.Add(new CompressionCallerCase
            {
                Caller = "real-caller",
                Status = "failed",
                Error = "Cancelled by user.",
            });
        }
        catch (Exception exception)
        {
            cases.Add(new CompressionCallerCase
            {
                Caller = "real-caller",
                Status = "failed",
                Error = exception.ToString(),
            });
        }

        var receipt = new CompressionCallerReceipt
        {
            Status = cases.Count > 0 && cases.All(static item => item is CompressionCallerCase { Status: "complete" })
                ? "complete"
                : "failed",
            MeasurementMethodology = TimingMethodologyVersion,
            StartedAtUtc = startedAt,
            FinishedAtUtc = DateTimeOffset.UtcNow,
            Configuration = options,
            Environment = CompressionCallerEnvironment.Create(),
            Cases = cases,
            RawSamplesPath = rawPath,
            Command = Environment.CommandLine,
        };
        var receiptPath = Path.Combine(outputDirectory, "compression-real-receipt.json");
        await File.WriteAllTextAsync(
            receiptPath,
            JsonSerializer.Serialize(receipt, JsonOptions),
            CancellationToken.None).ConfigureAwait(false);
        var summaryPath = Path.Combine(outputDirectory, "compression-real-summary.md");
        await File.WriteAllTextAsync(
            summaryPath,
            CompressionCallerMarkdown.Create(receipt),
            CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"Compression real-caller receipt: {receiptPath}");
        Console.WriteLine($"Compression real-caller summary: {summaryPath}");
        return receipt.Status == "complete";
    }

    private static async Task<CompressionCallerCase> RunDiskCaseAsync(
        CompressionCallerOptions options,
        CompressionCallerPayloadProfile profile,
        CompressionMethod method,
        int concurrency,
        StreamWriter raw,
        CancellationToken cancellationToken)
    {
        var result = new DiskSegmentCase
        {
            Caller = "forced-disk-segment",
            Profile = profile.Name,
            PayloadBytes = profile.SizeBytes,
            Method = method.ToString(),
            CompressionLevel = CompressionLevelFor(method, options),
            SourceMode = SourceModeFor(method),
            CompressionBlockBytes = DiskBlockBytes,
            WalMode = WalMode,
            Concurrency = concurrency,
            SegmentThresholdMode = SegmentThresholdMode,
            MutableSegmentMaxItemCount = ControlledMutableSegmentMaxItems,
        };
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"terrabase-compression-disk-{Guid.NewGuid():N}");

        try
        {
            var payload = CreatePayload(profile, options.Seed);
            var keys = CreateKeys(profile.RecordCount, options.Seed);
            result.RecordCount = keys.Length;
            result.LogicalInputBytes = keys.Sum(static key => (long)key.Length) +
                (long)payload.Length * keys.Length;
            Directory.CreateDirectory(dataPath);

            var build = MeasureScope(() =>
            {
                using var zoneTree = CreateZoneTree(dataPath, method, options);
                for (var index = 0; index < keys.Length; index++)
                {
                    var key = keys[index];
                    zoneTree.Upsert(key, payload);
                }

                zoneTree.Maintenance.MoveMutableSegmentForward();
                var merge = zoneTree.Maintenance.StartMergeOperation();
                merge?.Join();
                if (!Directory.EnumerateFiles(dataPath, "*.z", SearchOption.AllDirectories).Any())
                {
                    throw new InvalidOperationException("The forced disk-segment case did not create a .z segment file.");
                }
            });
            result.Build = build;
            result.FileBytes = ClassifyFiles(dataPath);
            result.StoredBytes = result.FileBytes.Values.Sum();
            result.SegmentFilesFound = result.FileBytes.TryGetValue("data.z", out var dataBytes) && dataBytes > 0;
            if (!result.SegmentFilesFound)
            {
                throw new InvalidOperationException("The forced disk-segment case did not create a data.z file.");
            }

            var reopened = OpenZoneTree(dataPath, method, options);
            result.Reopen = reopened.Observation;
            using var zoneTree = reopened.Tree;
            result.ColdRead = MeasureScope("cold-read", () =>
                {
                    var key = keys[0];
                    if (!zoneTree.TryGet(key, out var value) || !value.Span.SequenceEqual(payload.Span))
                    {
                        throw new InvalidDataException("The first reopened disk-segment read did not restore its value.");
                    }
                });
            ValidateDiskRecords(zoneTree, keys, payload);

            for (var sample = 1; sample <= options.Samples; sample++)
            {
                result.WarmSamples.Add(await MeasureDiskWarmSampleAsync(
                    options,
                    zoneTree,
                    keys,
                    payload,
                    concurrency,
                    cancellationToken).ConfigureAwait(false));
            }

            result.Status = "complete";
            await WriteRawAsync(raw, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Status = "failed";
            result.Error = "Cancelled by user.";
            await WriteRawAsync(raw, result, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            result.Status = "failed";
            result.Error = exception.ToString();
            await WriteRawAsync(raw, result, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            TryDeleteDirectory(dataPath);
        }
    }

    private static async Task<CompressionCallerCase> RunBackupCaseAsync(
        CompressionCallerOptions options,
        CompressionCallerPayloadProfile profile,
        CompressionMethod method,
        int concurrency,
        StreamWriter raw,
        CancellationToken cancellationToken)
    {
        var result = new LiveBackupCase
        {
            Caller = "live-backup-record-batch",
            Profile = profile.Name,
            PayloadBytes = profile.SizeBytes,
            Method = method.ToString(),
            CompressionLevel = CompressionLevelFor(method, options),
            SourceMode = SourceModeFor(method),
            CompressionBlockBytes = BackupBlockBytes,
            Concurrency = concurrency,
        };

        try
        {
            var payload = CreatePayload(profile, options.Seed);
            var records = CreateBackupRecords(profile, payload, options.Seed);
            result.RecordCount = records.Length;
            var coldWriter = await MeasureBackupWriterAsync(
                method,
                CompressionLevelFor(method, options),
                records,
                cancellationToken).ConfigureAwait(false);
            result.InputBytes = coldWriter.InputBytes;
            result.StoredBytes = coldWriter.StoredBytes;
            result.StoredRatio = result.StoredBytes / (double)Math.Max(result.InputBytes, 1);
            if (IncludesPhase(options, CompressionCallerPhase.ColdWrite))
            {
                result.ColdWriter = coldWriter.Observation;
            }

            if (IncludesPhase(options, CompressionCallerPhase.WarmWrite))
            {
                for (var sample = 1; sample <= options.Samples; sample++)
                {
                    result.WarmWriterSamples.Add(await MeasureBackupWarmSampleAsync(
                        options,
                        method,
                        CompressionLevelFor(method, options),
                        records,
                        concurrency,
                        isReader: false,
                        coldWriter.Bytes,
                        result.InputBytes,
                        result.StoredBytes,
                        cancellationToken).ConfigureAwait(false));
                }
            }

            var batch = coldWriter.Batch;
            if (IncludesPhase(options, CompressionCallerPhase.ColdRead))
            {
                result.ColdReader = MeasureScope(
                    "cold-read",
                    () => ReadAndValidateBatch(coldWriter.Bytes, batch, records),
                    result.InputBytes,
                    result.StoredBytes);
            }

            if (IncludesPhase(options, CompressionCallerPhase.WarmRead))
            {
                for (var sample = 1; sample <= options.Samples; sample++)
                {
                    result.WarmReaderSamples.Add(await MeasureBackupWarmSampleAsync(
                        options,
                        method,
                        CompressionLevelFor(method, options),
                        records,
                        concurrency,
                        isReader: true,
                        coldWriter.Bytes,
                        result.InputBytes,
                        result.StoredBytes,
                        cancellationToken).ConfigureAwait(false));
                }
            }
            result.Status = "complete";
            await WriteRawAsync(raw, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Status = "failed";
            result.Error = "Cancelled by user.";
            await WriteRawAsync(raw, result, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            result.Status = "failed";
            result.Error = exception.ToString();
            await WriteRawAsync(raw, result, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
    }

    private static ZoneTreeFactory<Memory<byte>, Memory<byte>> CreateFactory(
        string dataPath,
        CompressionMethod method,
        CompressionCallerOptions options) => new ZoneTreeFactory<Memory<byte>, Memory<byte>>()
        .SetDataDirectory(dataPath)
        .SetComparer(new ByteArrayComparerAscending())
        .SetKeyHasher(new ByteArrayKeyHasher())
        .SetKeySerializer(new ByteArraySerializer())
        .SetValueSerializer(new ByteArraySerializer())
        .SetMutableSegmentMaxItemCount(ControlledMutableSegmentMaxItems)
        .SetDiskSegmentMaxItemCount(ControlledDiskSegmentMaxItems)
        .ConfigureWriteAheadLogOptions(wal => wal.WriteAheadLogMode = WriteAheadLogMode.Sync)
        .ConfigureDiskSegmentOptions(disk =>
        {
            disk.DiskSegmentMode = DiskSegmentMode.SingleDiskSegment;
            disk.CompressionBlockSize = DiskBlockBytes;
            disk.CompressionMethod = method;
            disk.CompressionLevel = CompressionLevelFor(method, options);
        });

    private static IZoneTree<Memory<byte>, Memory<byte>> CreateZoneTree(
        string dataPath,
        CompressionMethod method,
        CompressionCallerOptions options) => CreateFactory(dataPath, method, options).OpenOrCreate();

    private static OpenZoneTreeResult OpenZoneTree(
        string dataPath,
        CompressionMethod method,
        CompressionCallerOptions options)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocationBefore = GC.GetTotalAllocatedBytes(false);
        var workingSetBefore = process.WorkingSet64;
        var started = Stopwatch.GetTimestamp();
        var tree = CreateZoneTree(dataPath, method, options);
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocations = Math.Max(0, GC.GetTotalAllocatedBytes(false) - allocationBefore);
        var workingSetAfter = process.WorkingSet64;
        var peakWorkingSet = process.PeakWorkingSet64;
        var ticks = Stopwatch.GetTimestamp() - started;
        return new OpenZoneTreeResult(
            tree,
            CreateObservation(
                "cold-reopen",
                1,
                1,
                elapsed,
                cpu,
                allocations,
                [ticks],
                0,
                0,
                new ProcessWorkingSetMeasurement(workingSetBefore, workingSetAfter, peakWorkingSet)));
    }

    private static void ValidateDiskRecords(
        IZoneTree<Memory<byte>, Memory<byte>> zoneTree,
        Memory<byte>[] keys,
        Memory<byte> payload)
    {
        foreach (var key in keys)
        {
            if (!zoneTree.TryGet(key, out var value) || !value.Span.SequenceEqual(payload.Span))
            {
                throw new InvalidDataException("A reopened disk-segment value did not match the deterministic payload.");
            }
        }
    }

    private static async Task<CallerObservation> MeasureDiskWarmSampleAsync(
        CompressionCallerOptions options,
        IZoneTree<Memory<byte>, Memory<byte>> zoneTree,
        Memory<byte>[] keys,
        Memory<byte> payload,
        int concurrency,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < options.WarmupOperations; index++)
        {
            var key = keys[index % keys.Length];
            if (!zoneTree.TryGet(key, out var value) || !value.Span.SequenceEqual(payload.Span))
            {
                throw new InvalidDataException("The disk-segment warmup read did not restore its value.");
            }
        }

        var operationCount = options.OperationsPerSample;
        var latencies = new long[operationCount];
        using var process = Process.GetCurrentProcess();
        var cpuBefore = TimeSpan.Zero;
        var cpuAfter = TimeSpan.Zero;
        var allocationBefore = 0L;
        var allocationAfter = 0L;
        var workingSetBefore = 0L;
        var workingSetAfter = 0L;
        var peakWorkingSet = 0L;
        var timing = await MeasureConcurrentWindowAsync(
            operationCount,
            concurrency,
            beforeWindow: () =>
            {
                process.Refresh();
                cpuBefore = process.TotalProcessorTime;
                allocationBefore = GC.GetTotalAllocatedBytes(false);
                workingSetBefore = process.WorkingSet64;
            },
            afterWindow: () =>
            {
                process.Refresh();
                cpuAfter = process.TotalProcessorTime;
                allocationAfter = GC.GetTotalAllocatedBytes(false);
                workingSetAfter = process.WorkingSet64;
                peakWorkingSet = process.PeakWorkingSet64;
            },
            worker =>
            {
                for (var index = worker; index < operationCount; index += concurrency)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = keys[index % keys.Length];
                    var operationStarted = Stopwatch.GetTimestamp();
                    if (!zoneTree.TryGet(key, out var value) || !value.Span.SequenceEqual(payload.Span))
                    {
                        throw new InvalidDataException("The disk-segment read did not restore its value.");
                    }

                    Volatile.Write(ref blackHole, value.Length);
                    latencies[index] = Stopwatch.GetTimestamp() - operationStarted;
                }
                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        var cpu = cpuAfter - cpuBefore;
        var allocations = Math.Max(0, allocationAfter - allocationBefore);
        Array.Sort(latencies);
        return CreateObservation(
            "warm-read",
            operationCount,
            concurrency,
            timing.OperationWindow,
            cpu,
            allocations,
            latencies,
            inputBytes: payload.Length,
            storedBytes: payload.Length,
            workingSet: new ProcessWorkingSetMeasurement(workingSetBefore, workingSetAfter, peakWorkingSet),
            timingBoundary: "worker-operation-window",
            scheduling: timing.Scheduling,
            join: timing.Join);
    }

    private static async Task<BackupWriteResult> MeasureBackupWriterAsync(
        CompressionMethod method,
        int compressionLevel,
        LiveBackupRecord[] records,
        CancellationToken cancellationToken)
    {
        var batch = CreateBatch(method, compressionLevel, records.Length);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocationBefore = GC.GetTotalAllocatedBytes(false);
        var workingSetBefore = process.WorkingSet64;
        var started = Stopwatch.GetTimestamp();
        var stream = new MemoryStream();
        await using (var writer = new LiveBackupRecordBatchWriter(stream, batch))
        {
            foreach (var record in records)
            {
                await writer.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocations = Math.Max(0, GC.GetTotalAllocatedBytes(false) - allocationBefore);
        var workingSetAfter = process.WorkingSet64;
        var peakWorkingSet = process.PeakWorkingSet64;
        var bytes = stream.ToArray();
        if (batch.UncompressedLength <= 0 || batch.StoredLength != bytes.Length)
        {
            throw new InvalidDataException("Live backup writer length metadata did not match the stored stream.");
        }

        Volatile.Write(ref blackHole, bytes.Length);
        return new BackupWriteResult(
            bytes,
            batch,
            batch.UncompressedLength,
            batch.StoredLength,
            CreateObservation(
                "cold-write",
                1,
                1,
                elapsed,
                cpu,
                allocations,
                [ToStopwatchTicks(elapsed)],
                batch.UncompressedLength,
                batch.StoredLength,
                new ProcessWorkingSetMeasurement(workingSetBefore, workingSetAfter, peakWorkingSet)));
    }

    private static async Task<CallerObservation> MeasureBackupWarmSampleAsync(
        CompressionCallerOptions options,
        CompressionMethod method,
        int compressionLevel,
        LiveBackupRecord[] records,
        int concurrency,
        bool isReader,
        byte[] storedBytes,
        long inputBytes,
        long outputBytes,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < options.WarmupOperations; index++)
        {
            if (isReader)
            {
                ReadAndValidateBatch(storedBytes, CreateBatch(method, compressionLevel, records.Length, inputBytes, outputBytes), records);
            }
            else
            {
                await WriteBatchAsync(method, compressionLevel, records, cancellationToken).ConfigureAwait(false);
            }
        }

        var operationCount = options.OperationsPerSample;
        var latencies = new long[operationCount];
        using var process = Process.GetCurrentProcess();
        var cpuBefore = TimeSpan.Zero;
        var cpuAfter = TimeSpan.Zero;
        var allocationBefore = 0L;
        var allocationAfter = 0L;
        var workingSetBefore = 0L;
        var workingSetAfter = 0L;
        var peakWorkingSet = 0L;
        var timing = await MeasureConcurrentWindowAsync(
            operationCount,
            concurrency,
            beforeWindow: () =>
            {
                process.Refresh();
                cpuBefore = process.TotalProcessorTime;
                allocationBefore = GC.GetTotalAllocatedBytes(false);
                workingSetBefore = process.WorkingSet64;
            },
            afterWindow: () =>
            {
                process.Refresh();
                cpuAfter = process.TotalProcessorTime;
                allocationAfter = GC.GetTotalAllocatedBytes(false);
                workingSetAfter = process.WorkingSet64;
                peakWorkingSet = process.PeakWorkingSet64;
            },
            async worker =>
            {
                for (var index = worker; index < operationCount; index += concurrency)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var operationStarted = Stopwatch.GetTimestamp();
                    if (isReader)
                    {
                        ReadAndValidateBatch(storedBytes, CreateBatch(method, compressionLevel, records.Length, inputBytes, outputBytes), records);
                    }
                    else
                    {
                        await WriteBatchAsync(method, compressionLevel, records, cancellationToken).ConfigureAwait(false);
                    }

                    Volatile.Write(ref blackHole, outputBytes);
                    latencies[index] = Stopwatch.GetTimestamp() - operationStarted;
                }
            },
            cancellationToken).ConfigureAwait(false);
        var cpu = cpuAfter - cpuBefore;
        var allocations = Math.Max(0, allocationAfter - allocationBefore);
        Array.Sort(latencies);
        return CreateObservation(
            isReader ? "warm-read" : "warm-write",
            operationCount,
            concurrency,
            timing.OperationWindow,
            cpu,
            allocations,
            latencies,
            inputBytes,
            outputBytes,
            new ProcessWorkingSetMeasurement(workingSetBefore, workingSetAfter, peakWorkingSet),
            recordCount: records.Length,
            timingBoundary: "worker-operation-window",
            scheduling: timing.Scheduling,
            join: timing.Join);
    }

    private static async Task<WorkerTiming> MeasureConcurrentWindowAsync(
        int operationCount,
        int concurrency,
        Action beforeWindow,
        Action afterWindow,
        Func<int, Task> worker,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operationCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);

        var schedulingStarted = Stopwatch.GetTimestamp();
        var workersReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windowReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startWindow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstOperationTicks = new long[concurrency];
        var lastOperationTicks = new long[concurrency];
        Array.Fill(firstOperationTicks, long.MaxValue);
        var readyCount = 0;
        var windowReadyCount = 0;
        var completedWorkerCount = 0;
        var activeWorkerCount = Math.Min(concurrency, operationCount);
        var workers = Enumerable.Range(0, concurrency)
            .Select(workerIndex => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref readyCount) == concurrency)
                    workersReady.TrySetResult(true);

                await workersReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (Interlocked.Increment(ref windowReadyCount) == concurrency)
                    windowReady.TrySetResult(true);

                await startWindow.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (workerIndex < operationCount)
                    firstOperationTicks[workerIndex] = Stopwatch.GetTimestamp();

                await worker(workerIndex).ConfigureAwait(false);

                if (workerIndex < operationCount)
                {
                    lastOperationTicks[workerIndex] = Stopwatch.GetTimestamp();
                    if (Interlocked.Increment(ref completedWorkerCount) == activeWorkerCount)
                        afterWindow();
                }
            }))
            .ToArray();

        try
        {
            await workersReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await windowReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            beforeWindow();
            startWindow.TrySetResult(true);
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch
        {
            startWindow.TrySetCanceled(CancellationToken.None);
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original worker or cancellation exception.
            }

            throw;
        }

        var joined = Stopwatch.GetTimestamp();
        var first = firstOperationTicks.Min();
        var last = lastOperationTicks.Max();
        if (first == long.MaxValue || last == 0 || last < first)
            throw new InvalidOperationException("The concurrent caller window did not record an operation boundary.");

        return new WorkerTiming(
            Stopwatch.GetElapsedTime(first, last),
            Stopwatch.GetElapsedTime(schedulingStarted, first),
            Stopwatch.GetElapsedTime(last, joined));
    }

    private static async Task WriteBatchAsync(
        CompressionMethod method,
        int compressionLevel,
        LiveBackupRecord[] records,
        CancellationToken cancellationToken)
    {
        var batch = CreateBatch(method, compressionLevel, records.Length);
        var stream = new MemoryStream();
        await using var writer = new LiveBackupRecordBatchWriter(stream, batch);
        foreach (var record in records)
        {
            await writer.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ReadAndValidateBatch(
        byte[] storedBytes,
        LiveBackupRecordBatch batch,
        LiveBackupRecord[] expected)
    {
        using var reader = new LiveBackupRecordBatchReader(new MemoryStream(storedBytes, writable: false), batch);
        var index = 0;
        while (reader.TryRead(out var actual))
        {
            if (index >= expected.Length || actual.RecordIndex != expected[index].RecordIndex ||
                actual.IsDeleted != expected[index].IsDeleted ||
                !actual.Key.Span.SequenceEqual(expected[index].Key.Span) ||
                !actual.Value.Span.SequenceEqual(expected[index].Value.Span))
            {
                throw new InvalidDataException("Live backup reader returned a record different from the deterministic input.");
            }

            index++;
        }

        if (index != expected.Length)
        {
            throw new InvalidDataException("Live backup reader returned fewer records than the deterministic input.");
        }
    }

    private static LiveBackupRecordBatch CreateBatch(
        CompressionMethod method,
        int compressionLevel,
        int recordCount,
        long inputBytes = 0,
        long storedBytes = 0) => new()
        {
            GenerationId = 1,
            BatchId = 1,
            RecordCount = recordCount,
            CompressionMethod = method,
            CompressionLevel = compressionLevel,
            CompressionBlockSize = BackupBlockBytes,
            UncompressedLength = inputBytes,
            StoredLength = storedBytes,
        };

    private sealed record WorkerTiming(
        TimeSpan OperationWindow,
        TimeSpan Scheduling,
        TimeSpan Join);

    private sealed record ProcessWorkingSetMeasurement(
        long ProcessWorkingSetBytesBefore,
        long ProcessWorkingSetBytesAfter,
        long ProcessPeakWorkingSetBytes)
    {
        public long ProcessWorkingSetDeltaBytes =>
            ProcessWorkingSetBytesAfter - ProcessWorkingSetBytesBefore;
    }

    private static CallerObservation MeasureScope(
        string operation,
        Action action,
        long inputBytes = 0,
        long storedBytes = 0)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocationBefore = GC.GetTotalAllocatedBytes(false);
        var workingSetBefore = process.WorkingSet64;
        var started = Stopwatch.GetTimestamp();
        action();
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocations = Math.Max(0, GC.GetTotalAllocatedBytes(false) - allocationBefore);
        var workingSetAfter = process.WorkingSet64;
        var peakWorkingSet = process.PeakWorkingSet64;
        return CreateObservation(
            operation,
            1,
            1,
            elapsed,
            cpu,
            allocations,
            [ToStopwatchTicks(elapsed)],
            inputBytes,
            storedBytes,
            new ProcessWorkingSetMeasurement(workingSetBefore, workingSetAfter, peakWorkingSet));
    }

    private static CallerObservation MeasureScope(Action action) => MeasureScope("setup", action);

    private static CallerObservation CreateObservation(
        string operation,
        int operations,
        int concurrency,
        TimeSpan elapsed,
        TimeSpan cpu,
        long allocations,
        long[] sortedLatencies,
        long inputBytes,
        long storedBytes,
        ProcessWorkingSetMeasurement workingSet,
        int recordCount = 1,
        string timingBoundary = "timed-action",
        TimeSpan scheduling = default,
        TimeSpan join = default) => new()
        {
            Operation = operation,
            Operations = operations,
            Concurrency = concurrency,
            TimingBoundary = timingBoundary,
            ElapsedMilliseconds = elapsed.TotalMilliseconds,
            ThroughputOpsPerSecond = operations / Math.Max(elapsed.TotalSeconds, double.Epsilon),
            P50Microseconds = ToMicroseconds(Percentile(sortedLatencies, 0.50)),
            P95Microseconds = ToMicroseconds(Percentile(sortedLatencies, 0.95)),
            P99Microseconds = ToMicroseconds(Percentile(sortedLatencies, 0.99)),
            AllocatedBytes = allocations,
            AllocatedBytesPerOperation = allocations / (double)Math.Max(operations, 1),
            AllocatedBytesPerRecord = allocations / (double)Math.Max((long)operations * recordCount, 1),
            ProcessCpuMilliseconds = cpu.TotalMilliseconds,
            ProcessCpuMillisecondsPerOperation = cpu.TotalMilliseconds / Math.Max(operations, 1),
            ProcessCpuPercent = CpuPercent(cpu, elapsed),
            ProcessWorkingSetBytesBefore = workingSet.ProcessWorkingSetBytesBefore,
            ProcessWorkingSetBytesAfter = workingSet.ProcessWorkingSetBytesAfter,
            ProcessWorkingSetDeltaBytes = workingSet.ProcessWorkingSetDeltaBytes,
            ProcessPeakWorkingSetBytes = workingSet.ProcessPeakWorkingSetBytes,
            InputBytes = inputBytes,
            StoredBytes = storedBytes,
            WorkerSchedulingMilliseconds = scheduling.TotalMilliseconds,
            WorkerJoinMilliseconds = join.TotalMilliseconds,
        };

    private static async Task WriteRawAsync(
        StreamWriter raw,
        object result,
        CancellationToken cancellationToken)
    {
        await raw.WriteLineAsync(JsonSerializer.Serialize(result, RawJsonOptions)).ConfigureAwait(false);
        await raw.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<int> DiskConcurrency(CompressionCallerOptions options) =>
        options.ConcurrencySpecified ? [options.Concurrency] : [1, 2, 4];

    private static IReadOnlyList<int> BackupConcurrency(CompressionCallerOptions options) =>
        options.ConcurrencySpecified ? [options.Concurrency] : [1, 4];

    private static IReadOnlyList<CompressionCallerPayloadProfile> SelectedProfiles(
        CompressionCallerOptions options)
    {
        var profileName = options.Profile;
        if (profileName is null)
            return CompressionCallerPayloadProfile.All;

        var profile = CompressionCallerPayloadProfile.All.FirstOrDefault(
            candidate => candidate.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        return profile is null ? throw UnknownProfile(profileName) : [profile];
    }

    private static CompressionMethod[] SelectedMethods(
        CompressionCallerOptions options) =>
        options.Method is { } method ? [method] : CompressionMethods;

    private static void ValidateSelection(CompressionCallerOptions options)
    {
        if (options.Profile is not null && string.IsNullOrWhiteSpace(options.Profile))
        {
            throw new ArgumentException("--profile must not be empty.");
        }

        if (options.Method is { } method && !CompressionMethods.Contains(method))
        {
            throw new ArgumentException(
                "--method must be none, lz4, or zstd.");
        }

        if (options.Phase is not CompressionCallerPhase.All and
            not CompressionCallerPhase.ColdWrite and
            not CompressionCallerPhase.ColdRead and
            not CompressionCallerPhase.WarmWrite and
            not CompressionCallerPhase.WarmRead)
        {
            throw new ArgumentException(
                "--phase must be all, cold-write, cold-read, warm-write, or warm-read.");
        }

        if ((options.Profile is not null || options.Method is not null ||
             options.Phase != CompressionCallerPhase.All) &&
            options.Caller != CompressionCaller.LiveBackup)
        {
            throw new ArgumentException(
                "--profile, --method, and --phase require --caller live-backup.");
        }
    }

    private static ArgumentException UnknownProfile(string profile) =>
        new($"Unknown --profile '{profile}'. Expected one of: " +
            string.Join(", ", CompressionCallerPayloadProfile.All.Select(static item => item.Name)) + ".");

    private static bool IncludesPhase(
        CompressionCallerOptions options,
        CompressionCallerPhase phase) =>
        options.Phase == CompressionCallerPhase.All || options.Phase == phase;

    private static int CompressionLevelFor(CompressionMethod method, CompressionCallerOptions options) => method switch
    {
        CompressionMethod.None => 0,
        CompressionMethod.Zstd => CompressionLevels.Zstd0,
        CompressionMethod.LZ4 => options.Lz4CompressionLevel,
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    private static string SourceModeFor(CompressionMethod method) => method switch
    {
        CompressionMethod.None => "DataCompression passthrough",
        CompressionMethod.LZ4 => "Native LZ4 frame; declared content size; content and block checksums",
        CompressionMethod.Zstd => "Native Zstandard frame; NativeCompressions default options",
        _ => method.ToString(),
    };

    private static readonly CompressionMethod[] CompressionMethods =
        [CompressionMethod.None, CompressionMethod.LZ4, CompressionMethod.Zstd];

    private static Memory<byte>[] CreateKeys(int count, int seed)
    {
        var keys = new Memory<byte>[count];
        for (var index = 0; index < count; index++)
        {
            keys[index] = Encoding.UTF8.GetBytes($"compression-{seed:x8}-{index:D8}");
        }

        return keys;
    }

    private static LiveBackupRecord[] CreateBackupRecords(
        CompressionCallerPayloadProfile profile,
        ReadOnlyMemory<byte> payload,
        int seed)
    {
        var keys = CreateKeys(profile.RecordCount, seed);
        var records = new LiveBackupRecord[keys.Length];
        for (var index = 0; index < records.Length; index++)
        {
            records[index] = new LiveBackupRecord
            {
                RecordIndex = index + 1,
                Key = keys[index].ToArray(),
                Value = payload.ToArray(),
            };
        }

        return records;
    }

    private static Memory<byte> CreatePayload(CompressionCallerPayloadProfile profile, int seed) =>
        profile.Compressible
            ? CreateDocumentPayload(profile.SizeBytes)
            : CreateIncompressiblePayload(profile.SizeBytes, seed);

    private static byte[] CreateDocumentPayload(int size)
    {
        const string prefix = "{\"tenant\":\"benchmark-tenant\",\"collection\":\"compression\",\"documentId\":\"document-00000001\",\"payload\":\"";
        const string suffix = "\"}";
        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var suffixBytes = Encoding.UTF8.GetBytes(suffix);
        if (size < prefixBytes.Length + suffixBytes.Length)
        {
            throw new ArgumentException("The document payload is smaller than its JSON envelope.", nameof(size));
        }

        var payload = new byte[size];
        prefixBytes.CopyTo(payload, 0);
        var body = payload.AsSpan(prefixBytes.Length, size - prefixBytes.Length - suffixBytes.Length);
        var pattern = Encoding.UTF8.GetBytes("terra-base compression benchmark document value; ");
        for (var offset = 0; offset < body.Length; offset += pattern.Length)
        {
            pattern.AsSpan(0, Math.Min(pattern.Length, body.Length - offset)).CopyTo(body[offset..]);
        }

        suffixBytes.CopyTo(payload, size - suffixBytes.Length);
        return payload;
    }

    private static byte[] CreateIncompressiblePayload(int size, int seed)
    {
        var payload = new byte[size];
        Span<byte> input = stackalloc byte[8];
        Span<byte> hash = stackalloc byte[32];
        for (var offset = 0; offset < payload.Length; offset += hash.Length)
        {
            BinaryPrimitives.WriteInt32LittleEndian(input[..4], seed);
            BinaryPrimitives.WriteInt32LittleEndian(input[4..], offset / hash.Length);
            SHA256.TryHashData(input, hash, out _);
            hash[..Math.Min(hash.Length, payload.Length - offset)].CopyTo(payload.AsSpan(offset));
        }

        return payload;
    }

    private static Dictionary<string, long> ClassifyFiles(string root)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["data.z"] = 0,
            ["head.z"] = 0,
            ["sparse"] = 0,
            ["multi"] = 0,
            ["wal"] = 0,
            ["metadata"] = 0,
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var length = new FileInfo(path).Length;
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            var category = relative.EndsWith(".data.z", StringComparison.OrdinalIgnoreCase)
                ? "data.z"
                : relative.EndsWith(".head.z", StringComparison.OrdinalIgnoreCase)
                    ? "head.z"
                    : relative.Contains("sparse", StringComparison.OrdinalIgnoreCase)
                        ? "sparse"
                        : relative.Contains("multi", StringComparison.OrdinalIgnoreCase)
                            ? "multi"
                            : relative.Contains("wal", StringComparison.OrdinalIgnoreCase)
                                ? "wal"
                                : "metadata";
            result[category] = checked(result[category] + length);
        }

        return result;
    }

    private static double CpuPercent(TimeSpan cpu, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0
            ? 0
            : cpu.TotalSeconds / elapsed.TotalSeconds / Environment.ProcessorCount * 100;

    private static double ToMicroseconds(long stopwatchTicks) =>
        stopwatchTicks * 1_000_000d / Stopwatch.Frequency;

    private static long ToStopwatchTicks(TimeSpan elapsed) =>
        (long)(elapsed.TotalSeconds * Stopwatch.Frequency);

    private static long Percentile(long[] sortedTicks, double percentile)
    {
        var index = (int)Math.Ceiling(sortedTicks.Length * percentile) - 1;
        return sortedTicks[Math.Clamp(index, 0, sortedTicks.Length - 1)];
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temporary benchmark cleanup is best effort after the result is captured.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary benchmark cleanup is best effort after the result is captured.
        }
    }
}

internal sealed record CompressionCallerPayloadProfile(
    string Name,
    int SizeBytes,
    bool Compressible,
    int RecordCount)
{
    public static IReadOnlyList<CompressionCallerPayloadProfile> All { get; } =
    [
        new("4KiB-compressible", 4 * 1024, true, 2_050),
        new("4KiB-incompressible", 4 * 1024, false, 2_050),
        new("256KiB-compressible", 256 * 1024, true, 32),
        new("256KiB-incompressible", 256 * 1024, false, 32),
        new("4MiB-compressible", 4 * 1024 * 1024, true, 8),
        new("4MiB-incompressible", 4 * 1024 * 1024, false, 8),
    ];
}

internal class CompressionCallerCase
{
    public string Caller { get; init; } = string.Empty;
    public string Profile { get; init; } = string.Empty;
    public int PayloadBytes { get; init; }
    public string Method { get; init; } = string.Empty;
    public int CompressionLevel { get; init; }
    public string SourceMode { get; init; } = string.Empty;
    public int Concurrency { get; init; }
    public string Status { get; set; } = "failed";
    public string? Error { get; set; }
}

internal sealed class DiskSegmentCase : CompressionCallerCase
{
    public int CompressionBlockBytes { get; init; }
    public string WalMode { get; init; } = string.Empty;
    public string SegmentThresholdMode { get; init; } = string.Empty;
    public int MutableSegmentMaxItemCount { get; init; }
    public int RecordCount { get; set; }
    public long LogicalInputBytes { get; set; }
    public long StoredBytes { get; set; }
    public bool SegmentFilesFound { get; set; }
    public Dictionary<string, long> FileBytes { get; set; } = new(StringComparer.Ordinal);
    public CallerObservation? Build { get; set; }
    public CallerObservation? Reopen { get; set; }
    public CallerObservation? ColdRead { get; set; }
    public List<CallerObservation> WarmSamples { get; } = [];
}

internal sealed class LiveBackupCase : CompressionCallerCase
{
    public int CompressionBlockBytes { get; init; }
    public int RecordCount { get; set; }
    public long InputBytes { get; set; }
    public long StoredBytes { get; set; }
    public double StoredRatio { get; set; }
    public CallerObservation? ColdWriter { get; set; }
    public CallerObservation? ColdReader { get; set; }
    public List<CallerObservation> WarmWriterSamples { get; } = [];
    public List<CallerObservation> WarmReaderSamples { get; } = [];
}

internal sealed class CallerObservation
{
    public string Operation { get; init; } = string.Empty;
    public int Operations { get; init; }
    public int Concurrency { get; init; }
    public string TimingBoundary { get; init; } = "timed-action";
    public double ElapsedMilliseconds { get; init; }
    public double ThroughputOpsPerSecond { get; init; }
    public double P50Microseconds { get; init; }
    public double P95Microseconds { get; init; }
    public double P99Microseconds { get; init; }
    public long AllocatedBytes { get; init; }
    public double AllocatedBytesPerOperation { get; init; }
    public double AllocatedBytesPerRecord { get; init; }
    public double ProcessCpuMilliseconds { get; init; }
    public double ProcessCpuMillisecondsPerOperation { get; init; }
    public double ProcessCpuPercent { get; init; }
    public long ProcessWorkingSetBytesBefore { get; init; }
    public long ProcessWorkingSetBytesAfter { get; init; }
    public long ProcessWorkingSetDeltaBytes { get; init; }
    public long ProcessPeakWorkingSetBytes { get; init; }
    public long InputBytes { get; init; }
    public long StoredBytes { get; init; }
    public double WorkerSchedulingMilliseconds { get; init; }
    public double WorkerJoinMilliseconds { get; init; }
}

internal sealed record BackupWriteResult(
    byte[] Bytes,
    LiveBackupRecordBatch Batch,
    long InputBytes,
    long StoredBytes,
    CallerObservation Observation);

internal sealed record OpenZoneTreeResult(
    IZoneTree<Memory<byte>, Memory<byte>> Tree,
    CallerObservation Observation);

internal sealed class CompressionCallerReceipt
{
    public string Status { get; init; } = "failed";
    public string MeasurementMethodology { get; init; } = CompressionCallerWorkload.TimingMethodologyVersion;
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset FinishedAtUtc { get; init; }
    public CompressionCallerOptions Configuration { get; init; } = new(0, 0, 0, 0, false, 0, 0, CompressionCaller.Primitive);
    public CompressionCallerEnvironment Environment { get; init; } = CompressionCallerEnvironment.Empty;
    public IReadOnlyList<object> Cases { get; init; } = [];
    public string RawSamplesPath { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
}

internal sealed record CompressionCallerEnvironment(
    string Runtime,
    string FrameworkDescription,
    string OperatingSystem,
    string Architecture,
    string BuildConfiguration,
    int ProcessorCount,
    long AvailableMemoryBytes,
    string CpuModel,
    bool ServerGc,
    string GcLatencyMode,
    string? GitCommit,
    bool? GitDirty,
    string? BenchmarkArtifactPath,
    string? BenchmarkArtifactSha256,
    string? ZoneTreeAssemblyPath,
    string? ZoneTreeAssemblySha256)
{
    public static CompressionCallerEnvironment Empty { get; } = new(
        "unknown",
        "unknown",
        "unknown",
        "unknown",
        "unknown",
        0,
        0,
        "unknown",
        false,
        "unknown",
        null,
        null,
        null,
        null,
        null,
        null);

    public static CompressionCallerEnvironment Create()
    {
        var benchmarkAssembly = Assembly.GetExecutingAssembly();
        var benchmarkPath = benchmarkAssembly.Location;
        var zoneTreeAssembly = typeof(ZoneTree.Compression.DataCompression).Assembly;
        var zoneTreePath = zoneTreeAssembly.Location;
        var gitRoot = FindGitRoot();
        return new CompressionCallerEnvironment(
            Environment.Version.ToString(),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            benchmarkAssembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            ReadCpuModel(),
            GCSettings.IsServerGC,
            GCSettings.LatencyMode.ToString(),
            gitRoot is null ? null : RunGit(gitRoot, "rev-parse", "HEAD"),
            gitRoot is null ? null : RunGit(gitRoot, "status", "--porcelain") is { Length: > 0 },
            string.IsNullOrWhiteSpace(benchmarkPath) ? null : benchmarkPath,
            HashFile(benchmarkPath),
            string.IsNullOrWhiteSpace(zoneTreePath) ? null : zoneTreePath,
            HashFile(zoneTreePath));
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
                    return current.FullName;
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
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;
            process.WaitForExit();
            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? HashFile(string path)
    {
        try
        {
            return File.Exists(path)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
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
                    .FirstOrDefault(static value => value.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
                if (line is not null)
                {
                    var separator = line.IndexOf(':');
                    return separator >= 0 ? line[(separator + 1)..].Trim() : line.Trim();
                }
            }
        }
        catch (IOException)
        {
            // Host model is optional metadata.
        }

        return "unknown";
    }
}

internal static class CompressionCallerMarkdown
{
    public static string Create(CompressionCallerReceipt receipt)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# TerraBase real compression caller benchmark receipt");
        builder.AppendLine();
        builder.AppendLine(FormattableString.Invariant($"Status: **{receipt.Status}**"));
        builder.AppendLine();
        builder.AppendLine(FormattableString.Invariant($"Measurement methodology: `{receipt.MeasurementMethodology}`. Warm caller throughput uses the worker operation window; worker scheduling and post-operation join are reported separately."));
        builder.AppendLine();
        builder.AppendLine(FormattableString.Invariant($"Commit: `{receipt.Environment.GitCommit ?? "unavailable"}` (dirty: `{receipt.Environment.GitDirty?.ToString() ?? "unknown"}`)"));
        builder.AppendLine(FormattableString.Invariant($"Benchmark artifact SHA-256: `{receipt.Environment.BenchmarkArtifactSha256 ?? "unavailable"}`; ZoneTree assembly SHA-256: `{receipt.Environment.ZoneTreeAssemblySha256 ?? "unavailable"}`"));
        builder.AppendLine(FormattableString.Invariant($"Runtime: {receipt.Environment.FrameworkDescription}; {receipt.Environment.Runtime}; {receipt.Environment.OperatingSystem}; {receipt.Environment.Architecture}; build `{receipt.Environment.BuildConfiguration}`"));
        builder.AppendLine(FormattableString.Invariant($"CPU: {receipt.Environment.CpuModel}; {receipt.Environment.ProcessorCount} logical processors; available memory {receipt.Environment.AvailableMemoryBytes:N0} bytes; GC server `{receipt.Environment.ServerGc}`, latency `{receipt.Environment.GcLatencyMode}`"));
        builder.AppendLine();
        builder.AppendLine("The forced disk-segment caller uses a direct ZoneTree with a controlled mutable threshold, 4 MiB disk blocks, and plain synchronous WAL. The live-backup caller uses the record-batch writer and reader with 1 MiB blocks. These are component measurements and do not establish provider throughput or production capacity.");
        builder.AppendLine();
        builder.AppendLine("| Caller | Profile | Method | Concurrency | Operation | Timing boundary | Window ms | Scheduling ms | Join ms | Input bytes | Stored bytes | Ratio | p50/p95/p99 µs | Alloc/op | Alloc/record | CPU ms/op | Working set before B | Working set after B | Working set delta B | Peak working set B (process lifetime) | Ops/s |");
        builder.AppendLine("| --- | --- | --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var item in receipt.Cases)
        {
            if (item is DiskSegmentCase disk)
            {
                Append(builder, disk, disk.ColdRead);
                foreach (var sample in disk.WarmSamples)
                    Append(builder, disk, sample);
            }
            else if (item is LiveBackupCase backup)
            {
                Append(builder, backup, backup.ColdWriter);
                Append(builder, backup, backup.ColdReader);
                foreach (var sample in backup.WarmWriterSamples)
                    Append(builder, backup, sample);
                foreach (var sample in backup.WarmReaderSamples)
                    Append(builder, backup, sample);
            }
            else if (item is CompressionCallerCase failed)
            {
                builder.Append('|').Append(failed.Caller).Append(" | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | —").AppendLine();
            }
        }

        builder.AppendLine();
        builder.AppendLine("Timing definitions: `Window ms` and `Ops/s` cover the interval from the first worker operation entry through the last worker operation completion. `Scheduling ms` covers worker task launch and synchronized readiness before that window; `Join ms` covers waiting for worker tasks after the last operation. Per-operation latency starts and ends around the caller operation inside that same window.");
        builder.AppendLine("Resource definitions: working-set values are process-level resident-memory samples taken outside the latency interval. `Working set delta B` is the signed after-minus-before change and may be negative. `Peak working set B (process lifetime)` is the process high-water mark since process start, including prior setup, warmup, other cases, GC/JIT, and concurrent workers; it is diagnostic and is not per-operation memory or a capacity claim.");
        builder.AppendLine();
        builder.AppendLine(FormattableString.Invariant($"Source mode/settings: primitive DataCompression uses None passthrough, LZ4 framed input with a 4 MiB frame block, declared content size and both checksums, or native Zstandard level 0. Disk segment runs use the selected method at the configured LZ4 level or level 0 for Zstandard, 4 MiB blocks, and `WriteAheadLogMode.Sync`. Live backup runs use the selected method at the configured LZ4 level or level 0 for Zstandard with 1 MiB record-batch blocks."));
        builder.AppendLine();
        builder.AppendLine("Suggested profiler invocation for the verified slow LZ4 4 MiB incompressible primitive case: `dotnet-trace collect --process-id <pid> --providers Microsoft-DotNETCore-SampleProfiler` while running a focused `--caller primitive --operations 20 --samples 5 --warmup 5 --concurrency 1` command. This receipt does not claim a profiler run.");
        builder.AppendLine();
        builder.AppendLine(FormattableString.Invariant($"Command: `{receipt.Command}`"));
        return builder.ToString();
    }

    private static void Append(
        StringBuilder builder,
        CompressionCallerCase item,
        CallerObservation? observation)
    {
        if (observation is null)
            return;
        var input = item switch
        {
            DiskSegmentCase disk => disk.LogicalInputBytes,
            LiveBackupCase backup => backup.InputBytes,
            _ => observation.InputBytes,
        };
        var stored = item switch
        {
            DiskSegmentCase disk => disk.StoredBytes,
            LiveBackupCase backup => backup.StoredBytes,
            _ => observation.StoredBytes,
        };
        builder.Append('|').Append(item.Caller).Append(" | ").Append(item.Profile).Append(" | ")
            .Append(item.Method).Append(" | ").Append(item.Concurrency.ToString(CultureInfo.InvariantCulture)).Append(" | ")
            .Append(observation.Operation).Append(" | ").Append(observation.TimingBoundary).Append(" | ")
            .Append(observation.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append(" | ")
            .Append(observation.WorkerSchedulingMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append(" | ")
            .Append(observation.WorkerJoinMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append(" | ")
            .Append(input.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
            .Append(stored.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
            .Append((stored / (double)Math.Max(input, 1)).ToString("F4", CultureInfo.InvariantCulture)).Append(" | ")
            .Append(observation.P50Microseconds.ToString("F2", CultureInfo.InvariantCulture)).Append('/')
            .Append(observation.P95Microseconds.ToString("F2", CultureInfo.InvariantCulture)).Append('/')
            .Append(observation.P99Microseconds.ToString("F2", CultureInfo.InvariantCulture)).Append(" | ")
            .Append(observation.AllocatedBytesPerOperation.ToString("F2", CultureInfo.InvariantCulture)).Append(" | ")
            .Append(observation.AllocatedBytesPerRecord.ToString("F2", CultureInfo.InvariantCulture)).Append(" | ")
            .Append(observation.ProcessCpuMillisecondsPerOperation.ToString("F4", CultureInfo.InvariantCulture)).Append(" | ")
            .Append(observation.ProcessWorkingSetBytesBefore.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
            .Append(observation.ProcessWorkingSetBytesAfter.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
            .Append(observation.ProcessWorkingSetDeltaBytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
            .Append(observation.ProcessPeakWorkingSetBytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
            .Append(observation.ThroughputOpsPerSecond.ToString("F2", CultureInfo.InvariantCulture)).AppendLine(" |");
    }
}
