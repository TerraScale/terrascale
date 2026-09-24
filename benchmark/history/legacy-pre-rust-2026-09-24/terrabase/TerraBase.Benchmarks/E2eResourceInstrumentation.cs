using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TerraBase.Backup;

namespace TerraBase.Benchmarks;

/// <summary>
/// Run-owned resource accounting for the end-to-end volume qualification.
/// The tracker deliberately measures the complete benchmark output tree and
/// any explicitly supplied RustFS or temporary-data roots, because backup
/// spools and a local RustFS data directory can live outside the source root.
/// </summary>
internal sealed class E2eResourceTracker : IAsyncDisposable
{
    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string outputRoot;
    private readonly string sourceRoot;
    private readonly string targetRoot;
    private readonly string uploadsRoot;
    private readonly string tempRoot;
    private readonly string resolvedTempPath;
    private readonly string? rustFsDataRoot;
    private readonly long? maxLocalStorageBytes;
    private readonly long minimumFreeSpaceBytes;
    private readonly long checkpointRecords;
    private readonly int sampleIntervalMilliseconds;
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly StreamWriter progressWriter;
    private readonly StreamWriter resourceWriter;
    private readonly Task sampler;
    private readonly SemaphoreSlim remoteGate = new(1, 1);
    private readonly bool requireIsolatedCgroup;
    private readonly string rustFsDataPathStatus;
    private string phase = "initializing";
    private long completed;
    private long total;
    private long nextCheckpoint;
    private BenchmarkRemoteTotals? remote;
    private IBackupObjectStore? remoteStore;
    private Exception? limitViolation;
    private Exception? samplerError;
    private long lastRemoteRefreshTicks;
    private int stopped;
    private int writersClosed;
    private int completedSuccessfully;

    private E2eResourceTracker(
        string outputRoot,
        string sourceRoot,
        string targetRoot,
        string uploadsRoot,
        string tempRoot,
        string resolvedTempPath,
        string? rustFsDataRoot,
        long? maxLocalStorageBytes,
        long minimumFreeSpaceBytes,
        long checkpointRecords,
        int sampleIntervalMilliseconds,
        bool requireIsolatedCgroup,
        string rustFsDataPathStatus,
        string progressPath,
        string resourceSamplesPath)
    {
        this.outputRoot = outputRoot;
        this.sourceRoot = sourceRoot;
        this.targetRoot = targetRoot;
        this.uploadsRoot = uploadsRoot;
        this.tempRoot = tempRoot;
        this.resolvedTempPath = resolvedTempPath;
        this.rustFsDataRoot = rustFsDataRoot;
        this.maxLocalStorageBytes = maxLocalStorageBytes;
        this.minimumFreeSpaceBytes = minimumFreeSpaceBytes;
        this.checkpointRecords = checkpointRecords;
        this.sampleIntervalMilliseconds = sampleIntervalMilliseconds;
        this.requireIsolatedCgroup = requireIsolatedCgroup;
        this.rustFsDataPathStatus = rustFsDataPathStatus;
        ProgressPath = Path.GetFullPath(progressPath);
        ResourceSamplesPath = Path.GetFullPath(resourceSamplesPath);
        EnvelopePath = Path.Combine(Path.GetDirectoryName(ResourceSamplesPath)!, "resource-envelope.json");
        progressWriter = new StreamWriter(new FileStream(
            ProgressPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous), new UTF8Encoding(false));
        resourceWriter = new StreamWriter(new FileStream(
            ResourceSamplesPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous), new UTF8Encoding(false));
        nextCheckpoint = 0;
        InitialCgroup = BenchmarkCgroupSnapshot.Capture();
        var initialCgroupValidation = ValidateCgroup(InitialCgroup);
        IsolatedCgroup = initialCgroupValidation.Valid;
        InitialOomEvents = initialCgroupValidation.OomEvents;
        InitialOomKillEvents = initialCgroupValidation.OomKillEvents;
        CgroupValidationError = initialCgroupValidation.Error;
        InitialRustFsDataBytes = rustFsDataRoot is null ? 0 : MeasureDirectory(rustFsDataRoot).Bytes;
        sampler = SampleLoopAsync();
    }

    internal string ProgressPath { get; }
    internal string ResourceSamplesPath { get; }
    internal string EnvelopePath { get; }
    internal BenchmarkCgroupSnapshot InitialCgroup { get; }
    internal bool IsolatedCgroup { get; }
    internal bool FinalCgroupValid { get; private set; }
    internal long InitialOomEvents { get; }
    internal long InitialOomKillEvents { get; }
    internal long FinalOomEvents { get; private set; } = -1;
    internal long FinalOomKillEvents { get; private set; } = -1;
    internal string? CgroupValidationError { get; private set; }
    internal long ResourceSampleCount { get; private set; }
    internal long PeakRssBytes { get; private set; }
    internal long PeakLocalStorageBytes { get; private set; }
    internal long PeakSourceBytes { get; private set; }
    internal long PeakTargetBytes { get; private set; }
    internal long PeakUploadsBytes { get; private set; }
    internal long PeakTemporaryBytes { get; private set; }
    internal long PeakRustFsDataBytes { get; private set; }
    internal long InitialRustFsDataBytes { get; }
    internal long PeakCacheBytes { get; private set; }
    internal long PeakFreeBytes { get; private set; }
    internal long MinimumObservedFreeBytes { get; private set; } = long.MaxValue;
    internal BenchmarkCgroupSnapshot? FinalCgroup { get; private set; }
    internal Exception? SamplerError => Volatile.Read(ref samplerError);
    internal Exception? LimitViolation => Volatile.Read(ref limitViolation);
    internal BenchmarkRemoteTotals? Remote => remote;

    internal static E2eResourceTracker Start(
        string outputRoot,
        string sourceRoot,
        string targetRoot,
        string uploadsRoot,
        string tempRoot,
        string resolvedTempPath,
        string? rustFsDataRoot,
        long? maxLocalStorageBytes,
        long minimumFreeSpaceBytes,
        long checkpointRecords,
        int sampleIntervalMilliseconds,
        bool requireIsolatedCgroup,
        string rustFsDataPathStatus)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointRecords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleIntervalMilliseconds);
        var tracker = new E2eResourceTracker(
            Path.GetFullPath(outputRoot),
            Path.GetFullPath(sourceRoot),
            Path.GetFullPath(targetRoot),
            Path.GetFullPath(uploadsRoot),
            Path.GetFullPath(tempRoot),
            Path.GetFullPath(resolvedTempPath),
            string.IsNullOrWhiteSpace(rustFsDataRoot) ? null : Path.GetFullPath(rustFsDataRoot),
            maxLocalStorageBytes,
            minimumFreeSpaceBytes,
            checkpointRecords,
            sampleIntervalMilliseconds,
            requireIsolatedCgroup,
            rustFsDataPathStatus,
            Path.Combine(outputRoot, "progress.jsonl"),
            Path.Combine(outputRoot, "resource-samples.jsonl"));
        return tracker;
    }

    internal void SetRemoteStore(IBackupObjectStore? store) => remoteStore = store;

    internal bool ShouldRecord(long completed, long total)
    {
        lock (gate)
            return completed >= nextCheckpoint || completed == total;
    }

    internal void SetPhase(string phase, long completed = 0, long total = 0)
    {
        lock (gate)
        {
            this.phase = phase;
            this.completed = completed;
            this.total = total;
            nextCheckpoint = completed == 0 ? 0 : completed;
        }
    }

    internal async Task RecordProgressAsync(string phase, long completed, long total)
    {
        bool write;
        lock (gate)
        {
            this.phase = phase;
            this.completed = completed;
            this.total = total;
            write = completed >= nextCheckpoint || completed == total;
            if (write)
            {
                nextCheckpoint = completed > long.MaxValue - checkpointRecords
                    ? long.MaxValue
                    : completed + checkpointRecords;
            }
        }

        if (!write)
        {
            ThrowIfExceeded();
            return;
        }

        var storage = Sample(phase, completed, total);
        await RefreshRemoteIfDueAsync(force: false).ConfigureAwait(false);
        await WriteProgressAsync(phase, completed, total, storage).ConfigureAwait(false);
        ThrowIfExceeded();
    }

    internal void ThrowIfExceeded()
    {
        if (LimitViolation is { } violation)
            throw violation;
        if (SamplerError is { } error)
            throw new IOException("The E2E resource sampler failed.", error);
    }

    internal async Task CompleteAsync()
    {
        if (Volatile.Read(ref completedSuccessfully) != 0)
            return;
        await StopSamplerAsync().ConfigureAwait(false);
        await RefreshRemoteIfDueAsync(force: true, useCancellation: false).ConfigureAwait(false);
        FinalCgroup = BenchmarkCgroupSnapshot.Capture();
        var finalCgroupValidation = ValidateCgroup(FinalCgroup);
        FinalCgroupValid = finalCgroupValidation.Valid;
        FinalOomEvents = finalCgroupValidation.OomEvents;
        FinalOomKillEvents = finalCgroupValidation.OomKillEvents;
        CgroupValidationError ??= finalCgroupValidation.Error;
        if (requireIsolatedCgroup)
        {
            if (!IsolatedCgroup || !FinalCgroupValid)
            {
                Interlocked.CompareExchange(
                    ref limitViolation,
                    new E2eCgroupValidationException(CgroupValidationError ?? "The isolated cgroup is not valid at the final sample."),
                    null);
            }
            else if (FinalOomEvents < InitialOomEvents || FinalOomKillEvents < InitialOomKillEvents)
            {
                Interlocked.CompareExchange(
                    ref limitViolation,
                    new E2eCgroupValidationException("The isolated cgroup OOM counters moved backwards."),
                    null);
            }
            else if (FinalOomEvents > InitialOomEvents || FinalOomKillEvents > InitialOomKillEvents)
            {
                Interlocked.CompareExchange(
                    ref limitViolation,
                    new E2eCgroupValidationException(
                        $"The isolated cgroup recorded OOM activity (oom {InitialOomEvents}->{FinalOomEvents}, oom_kill {InitialOomKillEvents}->{FinalOomKillEvents})."),
                    null);
            }
            else if (ParseSwapCurrentBytes(FinalCgroup) != 0)
            {
                Interlocked.CompareExchange(
                    ref limitViolation,
                    new E2eCgroupValidationException(
                        $"The isolated cgroup used swap ({ParseSwapCurrentBytes(FinalCgroup):N0} bytes)."),
                    null);
            }
        }
        var final = Sample("complete", completed, total);
        await WriteEnvelopeAsync(FinalCgroup).ConfigureAwait(false);
        await WriteProgressAsync("complete", completed, total, final).ConfigureAwait(false);
        ThrowIfExceeded();
        await CloseWritersAsync().ConfigureAwait(false);
        Volatile.Write(ref completedSuccessfully, 1);
    }

    internal async Task FailAsync(Exception exception)
    {
        try
        {
            await StopSamplerAsync().ConfigureAwait(false);
            FinalCgroup = BenchmarkCgroupSnapshot.Capture();
            var final = Sample("failed", completed, total);
            await WriteEnvelopeAsync(FinalCgroup).ConfigureAwait(false);
            await WriteProgressAsync("failed", completed, total, final).ConfigureAwait(false);
            var failure = new
            {
                status = "failed",
                phase,
                completed,
                total,
                error = exception.ToString(),
                resourceSamplerError = SamplerError?.ToString(),
                localStorageLimit = maxLocalStorageBytes,
                minimumFreeSpaceBytes,
                resources = Snapshot(),
            };
            await File.WriteAllTextAsync(
                Path.Combine(Path.GetDirectoryName(ProgressPath)!, "e2e-volume-resource-failure.json"),
                JsonSerializer.Serialize(failure, PrettyJson)).ConfigureAwait(false);
            await CloseWritersAsync().ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original workload exception. The compact run
            // receipt still records it when diagnostic persistence fails.
        }
    }

    internal E2eResourceSnapshot Snapshot()
    {
        var cgroup = FinalCgroup ?? BenchmarkCgroupSnapshot.Capture();
        var local = Sample(phase, completed, total);
        return new E2eResourceSnapshot(
            InitialCgroup,
            cgroup,
            IsolatedCgroup,
            FinalCgroupValid,
            rustFsDataPathStatus,
            maxLocalStorageBytes,
            minimumFreeSpaceBytes,
            checkpointRecords,
            sampleIntervalMilliseconds,
            outputRoot,
            sourceRoot,
            targetRoot,
            uploadsRoot,
            tempRoot,
            resolvedTempPath,
            rustFsDataRoot,
            ResourceSamplesPath,
            ProgressPath,
            local.LocalStorageBytes,
            local.SourceBytes,
            local.TargetBytes,
            local.UploadsBytes,
            local.TemporaryBytes,
            local.RustFsDataBytes,
            InitialRustFsDataBytes,
            Math.Max(0, local.RustFsDataBytes - InitialRustFsDataBytes),
            local.CacheBytes,
            local.FreeBytes,
            PeakLocalStorageBytes,
            PeakSourceBytes,
            PeakTargetBytes,
            PeakUploadsBytes,
            PeakTemporaryBytes,
            PeakRustFsDataBytes,
            PeakCacheBytes,
            maxLocalStorageBytes is long configuredCap
                ? Math.Max(0, PeakLocalStorageBytes - configuredCap)
                : 0,
            MinimumObservedFreeBytes == long.MaxValue ? local.FreeBytes : MinimumObservedFreeBytes,
            Process.GetCurrentProcess().WorkingSet64,
            PeakRssBytes,
            ResourceSampleCount,
            remote,
            SamplerError?.ToString(),
            LimitViolation?.ToString(),
            ParseSwapCurrentBytes(cgroup),
            ParseMemoryEvent(cgroup.MemoryEvents, "oom"),
            ParseMemoryEvent(cgroup.MemoryEvents, "oom_kill"),
            InitialOomEvents,
            InitialOomKillEvents,
            FinalOomEvents,
            FinalOomKillEvents,
            CgroupValidationError);
    }

    public async ValueTask DisposeAsync()
    {
        await StopSamplerAsync().ConfigureAwait(false);
        await CloseWritersAsync().ConfigureAwait(false);
    }

    private async Task StopSamplerAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 0)
            cancellation.Cancel();
        try
        {
            await sampler.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref samplerError, exception, null);
        }

    }

    private async Task CloseWritersAsync()
    {
        if (Interlocked.Exchange(ref writersClosed, 1) != 0)
            return;
        await progressWriter.FlushAsync().ConfigureAwait(false);
        await resourceWriter.FlushAsync().ConfigureAwait(false);
        progressWriter.Dispose();
        resourceWriter.Dispose();
        cancellation.Dispose();
        remoteGate.Dispose();
    }

    private async Task SampleLoopAsync()
    {
        try
        {
            while (true)
            {
                string currentPhase;
                long currentCompleted;
                long currentTotal;
                lock (gate)
                {
                    currentPhase = phase;
                    currentCompleted = completed;
                    currentTotal = total;
                }

                var storage = Sample(currentPhase, currentCompleted, currentTotal);
                await RefreshRemoteIfDueAsync(force: false).ConfigureAwait(false);
                await WriteResourceSampleAsync(currentPhase, currentCompleted, currentTotal, storage).ConfigureAwait(false);
                await Task.Delay(sampleIntervalMilliseconds, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref samplerError, exception, null);
        }
    }

    private DiskUsageSample Sample(string currentPhase, long currentCompleted, long currentTotal)
    {
        var roots = UniqueRoots(outputRoot, tempRoot, rustFsDataRoot);
        var rootMeasures = roots
            .Select(root => MeasureDirectory(
                root,
                allowMissing: !string.Equals(root, outputRoot, PathComparison)
                    && !string.Equals(root, tempRoot, PathComparison)
                    && !string.Equals(root, rustFsDataRoot, PathComparison)))
            .ToArray();
        var local = rootMeasures.Sum(static measure => measure.Bytes);
        var sourceMeasure = MeasureDirectory(sourceRoot);
        var targetMeasure = MeasureDirectory(targetRoot);
        var uploadsMeasure = MeasureDirectory(uploadsRoot);
        var temporaryMeasure = MeasureDirectory(tempRoot, allowMissing: false);
        var rustFsDataMeasure = rustFsDataRoot is null ? new(0, true) : MeasureDirectory(rustFsDataRoot, allowMissing: false);
        var cacheMeasure = MeasureDirectory(Path.Combine(sourceRoot, "ssd-cache"));
        var cache = cacheMeasure.Bytes;
        var source = sourceMeasure.Bytes;
        var target = targetMeasure.Bytes;
        var uploads = uploadsMeasure.Bytes;
        var temporary = temporaryMeasure.Bytes;
        var rustFsData = rustFsDataMeasure.Bytes;
        var freeValues = roots.Select(ReadFreeBytes).ToArray();
        var free = freeValues.Where(static value => value >= 0).DefaultIfEmpty(-1).Min();
        var process = Process.GetCurrentProcess();
        var workingSet = process.WorkingSet64;
        PeakRssBytes = Math.Max(PeakRssBytes, Math.Max(workingSet, process.PeakWorkingSet64));
        lock (gate)
        {
            PeakLocalStorageBytes = Math.Max(PeakLocalStorageBytes, local);
            PeakSourceBytes = Math.Max(PeakSourceBytes, source);
            PeakTargetBytes = Math.Max(PeakTargetBytes, target);
            PeakUploadsBytes = Math.Max(PeakUploadsBytes, uploads);
            PeakTemporaryBytes = Math.Max(PeakTemporaryBytes, temporary);
            PeakRustFsDataBytes = Math.Max(PeakRustFsDataBytes, rustFsData);
            PeakCacheBytes = Math.Max(PeakCacheBytes, cache);
            if (free >= 0)
            {
                MinimumObservedFreeBytes = Math.Min(MinimumObservedFreeBytes, free);
                PeakFreeBytes = Math.Max(PeakFreeBytes, free);
            }
            ResourceSampleCount++;
            if (rootMeasures.Any(static measure => !measure.Readable)
                || !sourceMeasure.Readable
                || !targetMeasure.Readable
                || !uploadsMeasure.Readable
                || !temporaryMeasure.Readable
                || !rustFsDataMeasure.Readable
                || !cacheMeasure.Readable
                || (maxLocalStorageBytes is not null || minimumFreeSpaceBytes > 0) && freeValues.Any(static value => value < 0))
            {
                Interlocked.CompareExchange(
                    ref limitViolation,
                    new E2eResourceAccountingException(
                        $"A configured resource root or filesystem free-space probe was unreadable during {currentPhase} at {currentCompleted:N0} records."),
                    null);
            }
            if (maxLocalStorageBytes is long limit && local > limit)
            {
                Interlocked.CompareExchange(
                    ref limitViolation,
                    new E2eLocalStorageLimitExceededException(limit, local, currentPhase, currentCompleted),
                    null);
            }
            if (minimumFreeSpaceBytes > 0 && free >= 0 && free < minimumFreeSpaceBytes)
            {
                Interlocked.CompareExchange(
                    ref limitViolation,
                    new E2eFreeSpaceLimitExceededException(minimumFreeSpaceBytes, free, currentPhase, currentCompleted),
                    null);
            }
        }

        return new DiskUsageSample(local, source, target, uploads, temporary, rustFsData, cache, free, currentTotal);
    }

    private async Task RefreshRemoteIfDueAsync(bool force, bool useCancellation = true)
    {
        var store = remoteStore;
        if (store is null)
            return;
        var now = Stopwatch.GetTimestamp();
        var due = force || now - Volatile.Read(ref lastRemoteRefreshTicks) >= Stopwatch.Frequency * 5;
        if (!due)
            return;
        Interlocked.Exchange(ref lastRemoteRefreshTicks, now);
        var token = useCancellation ? cancellation.Token : CancellationToken.None;
        var acquired = false;
        try
        {
            await remoteGate.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            long objects = 0;
            long bytes = 0;
            await foreach (var item in store.ListAsync("", token).ConfigureAwait(false))
            {
                objects++;
                bytes = checked(bytes + item.Length);
            }

            remote = new BenchmarkRemoteTotals(objects, bytes);
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && useCancellation && cancellation.IsCancellationRequested)
                return;
            Interlocked.CompareExchange(ref samplerError, exception, null);
        }
        finally
        {
            if (acquired)
                remoteGate.Release();
        }
    }

    private async Task WriteProgressAsync(string currentPhase, long currentCompleted, long currentTotal, DiskUsageSample storage)
    {
        var process = Process.GetCurrentProcess();
        var line = new
        {
            utc = DateTimeOffset.UtcNow,
            phase = currentPhase,
            completed = currentCompleted,
            total = currentTotal,
            workingSetBytes = process.WorkingSet64,
            peakRssBytes = process.PeakWorkingSet64,
            managedBytes = GC.GetTotalMemory(false),
            storage.LocalStorageBytes,
            storage.SourceBytes,
            storage.TargetBytes,
            storage.UploadsBytes,
            storage.TemporaryBytes,
            storage.RustFsDataBytes,
            storage.CacheBytes,
            storage.FreeBytes,
            rustFsObjectCount = remote?.ObjectCount,
            rustFsBytes = remote?.TotalBytes,
            cgroup = BenchmarkCgroupSnapshot.Capture(),
        };
        await progressWriter.WriteLineAsync(JsonSerializer.Serialize(line)).ConfigureAwait(false);
        await progressWriter.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteResourceSampleAsync(string currentPhase, long currentCompleted, long currentTotal, DiskUsageSample storage)
    {
        var process = Process.GetCurrentProcess();
        var line = new
        {
            utc = DateTimeOffset.UtcNow,
            phase = currentPhase,
            completed = currentCompleted,
            total = currentTotal,
            workingSetBytes = process.WorkingSet64,
            peakRssBytes = process.PeakWorkingSet64,
            managedBytes = GC.GetTotalMemory(false),
            storage.LocalStorageBytes,
            storage.SourceBytes,
            storage.TargetBytes,
            storage.UploadsBytes,
            storage.TemporaryBytes,
            storage.RustFsDataBytes,
            storage.CacheBytes,
            storage.FreeBytes,
            rustFsObjectCount = remote?.ObjectCount,
            rustFsBytes = remote?.TotalBytes,
            cgroup = BenchmarkCgroupSnapshot.Capture(),
        };
        await resourceWriter.WriteLineAsync(JsonSerializer.Serialize(line)).ConfigureAwait(false);
        await resourceWriter.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteEnvelopeAsync(BenchmarkCgroupSnapshot final)
    {
        var envelope = new
        {
            initial = InitialCgroup,
            final,
            isolatedCgroup = IsolatedCgroup,
            configuredLocalStorageBytes = maxLocalStorageBytes,
            minimumFreeSpaceBytes,
            resolvedTempPath,
            cacheBudgetBytes = PeakCacheBytes,
            sampleCount = ResourceSampleCount,
            samplerError = SamplerError?.ToString(),
            limitViolation = LimitViolation?.ToString(),
            snapshot = Snapshot(),
        };
        await File.WriteAllTextAsync(
            EnvelopePath,
            JsonSerializer.Serialize(envelope, PrettyJson)).ConfigureAwait(false);
    }

    private static CgroupValidation ValidateCgroup(BenchmarkCgroupSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Path))
            return new(false, -1, -1, "The cgroup path is unreadable.");
        if (!long.TryParse(snapshot.MemoryMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out var memoryMax)
            || memoryMax <= 0)
        {
            return new(false, -1, -1, "memory.max is missing or unlimited.");
        }

        if (!long.TryParse(snapshot.SwapMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out var swapMax)
            || swapMax != 0)
        {
            return new(false, -1, -1, "memory.swap.max is missing or not zero.");
        }

        var oom = ParseMemoryEvent(snapshot.MemoryEvents, "oom");
        var oomKill = ParseMemoryEvent(snapshot.MemoryEvents, "oom_kill");
        if (oom < 0 || oomKill < 0)
            return new(false, oom, oomKill, "memory.events is unreadable or lacks oom counters.");

        return new(true, oom, oomKill, null);
    }

    private static long ParseSwapCurrentBytes(BenchmarkCgroupSnapshot snapshot) =>
        long.TryParse(snapshot.SwapCurrent, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;

    private static long ParseMemoryEvent(string? events, string name)
    {
        if (events is null)
            return -1;
        foreach (var line in events.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && string.Equals(parts[0], name, StringComparison.Ordinal)
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return value;
        }

        return -1;
    }

    private sealed record CgroupValidation(bool Valid, long OomEvents, long OomKillEvents, string? Error);

    private static List<string> UniqueRoots(params string?[] roots)
    {
        var normalized = roots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(static root => Path.GetFullPath(root!))
            .Distinct(PathComparer)
            .OrderByDescending(static root => root.Length)
            .ToArray();
        var retained = new List<string>(normalized.Length);
        foreach (var root in normalized)
        {
            if (retained.Any(parent => IsPathInside(root, parent)))
                continue;
            retained.Add(root);
        }

        return retained;
    }

    private static bool IsPathInside(string path, string parent)
    {
        if (string.Equals(path, parent, PathComparison))
            return true;
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static DirectoryMeasure MeasureDirectory(string root, bool allowMissing = true)
    {
        if (!Directory.Exists(root))
            return new(0, allowMissing);

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "du",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                startInfo.ArgumentList.Add("-sx");
                startInfo.ArgumentList.Add("--block-size=1");
                startInfo.ArgumentList.Add(root);
                startInfo.Environment["LC_ALL"] = "C";
                var regular = RunDiskUsage(startInfo, root, allowMissing);
                if (regular.Readable || !Directory.Exists(root))
                    return regular;

                var podmanInfo = new ProcessStartInfo
                {
                    FileName = "podman",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                podmanInfo.ArgumentList.Add("unshare");
                podmanInfo.ArgumentList.Add("du");
                podmanInfo.ArgumentList.Add("-sx");
                podmanInfo.ArgumentList.Add("--block-size=1");
                podmanInfo.ArgumentList.Add(root);
                podmanInfo.Environment["LC_ALL"] = "C";
                var podmanMeasure = RunDiskUsage(podmanInfo, root, allowMissing);
                if (podmanMeasure.Readable || !Directory.Exists(root))
                    return podmanMeasure;
                Thread.Sleep(TimeSpan.FromMilliseconds(25));
                return RunDiskUsage(podmanInfo, root, allowMissing);
            }
            catch (IOException)
            {
                return Directory.Exists(root) ? new(0, false) : new(0, allowMissing);
            }
            catch (UnauthorizedAccessException)
            {
                return Directory.Exists(root) ? new(0, false) : new(0, allowMissing);
            }
        }

        long bytes = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { bytes = checked(bytes + new FileInfo(path).Length); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        catch (DirectoryNotFoundException) { return new(0, allowMissing); }
        catch (UnauthorizedAccessException) { return new(bytes, false); }
        return new(bytes, true);
    }

    internal static bool CanMeasureDirectory(string root) => MeasureDirectory(root).Readable;

    internal static long MeasurePhysicalBytes(string root)
    {
        var measure = MeasureDirectory(root, allowMissing: false);
        if (!measure.Readable)
        {
            throw new IOException($"Physical disk usage is not readable for '{root}'.");
        }

        return measure.Bytes;
    }

    private static DirectoryMeasure RunDiskUsage(ProcessStartInfo startInfo, string root, bool allowMissing)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
            return new(0, false);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = startInfo.RedirectStandardError ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);
        process.WaitForExit();
        Task.WaitAll(outputTask, errorTask);
        var output = outputTask.Result;
        if (process.ExitCode == 0
            && long.TryParse(output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var allocated))
        {
            return new(allocated, true);
        }

        return Directory.Exists(root) ? new(0, false) : new(0, allowMissing);
    }

    private static long ReadFreeBytes(string root)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace; }
        catch (IOException) { return -1; }
        catch (UnauthorizedAccessException) { return -1; }
        catch (ArgumentException) { return -1; }
    }
}

internal sealed record DiskUsageSample(
    long LocalStorageBytes,
    long SourceBytes,
    long TargetBytes,
    long UploadsBytes,
    long TemporaryBytes,
    long RustFsDataBytes,
    long CacheBytes,
    long FreeBytes,
    long TotalRecords);

internal sealed record DirectoryMeasure(long Bytes, bool Readable);

internal sealed record BenchmarkRemoteTotals(long ObjectCount, long TotalBytes);

internal sealed record E2eResourceSnapshot(
    BenchmarkCgroupSnapshot InitialCgroup,
    BenchmarkCgroupSnapshot FinalCgroup,
    bool IsolatedCgroup,
    bool FinalCgroupValid,
    string RustFsDataPathStatus,
    long? MaxLocalStorageBytes,
    long MinimumFreeSpaceBytes,
    long CheckpointRecords,
    int ResourceSampleIntervalMilliseconds,
    string OutputRoot,
    string SourceRoot,
    string TargetRoot,
    string UploadsRoot,
    string TemporaryRoot,
    string ResolvedTempPath,
    string? RustFsDataRoot,
    string ResourceSamplesPath,
    string ProgressPath,
    long LocalStorageBytes,
    long SourceBytes,
    long TargetBytes,
    long UploadsBytes,
    long TemporaryBytes,
    long RustFsDataBytes,
    long InitialRustFsDataBytes,
    long RustFsDataGrowthBytes,
    long CacheBytes,
    long FreeBytes,
    long PeakLocalStorageBytes,
    long PeakSourceBytes,
    long PeakTargetBytes,
    long PeakUploadsBytes,
    long PeakTemporaryBytes,
    long PeakRustFsDataBytes,
    long PeakCacheBytes,
    long MaximumObservedCapOvershootBytes,
    long MinimumObservedFreeBytes,
    long WorkingSetBytes,
    long PeakRssBytes,
    long ResourceSampleCount,
    BenchmarkRemoteTotals? Remote,
    string? SamplerError,
    string? LimitViolation,
    long SwapCurrentBytes,
    long OomEvents,
    long OomKillEvents,
    long InitialOomEvents,
    long InitialOomKillEvents,
    long FinalOomEvents,
    long FinalOomKillEvents,
    string? CgroupValidationError);

internal sealed class E2eLocalStorageLimitExceededException(
    long limitBytes,
    long observedBytes,
    string phase,
    long completedRecords)
    : IOException($"E2E local storage exceeded {limitBytes:N0} bytes: observed {observedBytes:N0} during {phase} at {completedRecords:N0} records.")
{
    public long LimitBytes { get; } = limitBytes;
    public long ObservedBytes { get; } = observedBytes;
    public string Phase { get; } = phase;
    public long CompletedRecords { get; } = completedRecords;
}

internal sealed class E2eFreeSpaceLimitExceededException(
    long minimumBytes,
    long observedBytes,
    string phase,
    long completedRecords)
    : IOException($"E2E free space fell below {minimumBytes:N0} bytes: observed {observedBytes:N0} during {phase} at {completedRecords:N0} records.")
{
    public long MinimumBytes { get; } = minimumBytes;
    public long ObservedBytes { get; } = observedBytes;
    public string Phase { get; } = phase;
    public long CompletedRecords { get; } = completedRecords;
}

internal sealed class E2eResourceAccountingException(string message) : IOException(message);

internal sealed class E2eCgroupValidationException(string message) : IOException(message);

internal sealed class E2eTemporaryRootScope : IDisposable
{
    private readonly string path;

    private E2eTemporaryRootScope(string path, string resolvedTempPath)
    {
        this.path = path;
        ResolvedTempPath = resolvedTempPath;
    }

    internal string Path => path;
    internal string ResolvedTempPath { get; }

    internal static E2eTemporaryRootScope Create(string outputRoot, string? requested)
    {
        var path = System.IO.Path.GetFullPath(requested ?? System.IO.Path.Combine(outputRoot, "tmp"));
        var resolvedTempPath = NormalizePath(System.IO.Path.GetTempPath());
        if (!PathsEqual(path, resolvedTempPath))
        {
            throw new InvalidDataException(
                $"The E2E temporary root does not match Path.GetTempPath(). Set TMPDIR to the run-owned path before launching the benchmark. Requested '{path}', resolved '{resolvedTempPath}'.");
        }
        if (File.Exists(path))
            throw new InvalidDataException($"The E2E temporary root is a file: '{path}'.");
        if (Directory.Exists(path)
            && Directory.EnumerateFileSystemEntries(path).Any(static entry => !IsCurrentRuntimePipe(entry)))
            throw new InvalidDataException($"The E2E temporary root must be empty and run-owned: '{path}'.");
        Directory.CreateDirectory(path);
        return new E2eTemporaryRootScope(path, resolvedTempPath);
    }

    public void Dispose()
    {
    }

    private static string NormalizePath(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(fullPath);
        return fullPath.Length > (root?.Length ?? 0)
            ? fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            : fullPath;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsCurrentRuntimePipe(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var prefix = $"clr-debug-pipe-{Environment.ProcessId}-";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            && (name.EndsWith("-in", StringComparison.Ordinal)
                || name.EndsWith("-out", StringComparison.Ordinal));
    }
}
