using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase;
using TerraBase.Benchmarks;
using TerraBase.Reactive;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions RawJsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (CompressionWorkload.IsRequested(args))
            {
                return await CompressionWorkload.RunAsync(args).ConfigureAwait(false);
            }

            var requestedWorkload = RequestedWorkload(args);
            if (requestedWorkload.Equals("larger-memory", StringComparison.OrdinalIgnoreCase))
            {
                return await RunLargerMemoryAsync(args).ConfigureAwait(false);
            }

            if (requestedWorkload.Equals("subscriptions", StringComparison.OrdinalIgnoreCase))
            {
                return await RunSubscriptionsAsync(args).ConfigureAwait(false);
            }

            var options = BenchmarkOptions.Parse(args);
            if (options.ShowHelp)
            {
                BenchmarkOptions.PrintHelp();
                return 0;
            }

            return await new BenchmarkRunner(options).RunAsync().ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid benchmark configuration: {exception.Message}");
            BenchmarkOptions.PrintHelp();
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Benchmark runner failed: {exception}");
            return 1;
        }
    }

    private static string RequestedWorkload(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--workload=", StringComparison.OrdinalIgnoreCase))
            {
                return argument["--workload=".Length..];
            }

            if (argument.Equals("--workload", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                return args[index + 1];
            }
        }

        return string.Empty;
    }

    private static async Task<int> RunLargerMemoryAsync(string[] args)
    {
        LargerMemoryOptions options;
        try
        {
            options = LargerMemoryOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid larger-memory configuration: {exception.Message}");
            LargerMemoryOptions.PrintHelp();
            return 2;
        }

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var environment = EnvironmentInfo.Create();
        if (options.ShowHelp)
        {
            LargerMemoryOptions.PrintHelp();
            return 0;
        }

        if (!string.Equals(environment.BuildConfiguration, "Release", StringComparison.OrdinalIgnoreCase))
        {
            var receipt = LargerMemoryReceipt.Invalid(options, environment, "Benchmark execution requires a Release artifact.");
            await WriteStandaloneReceiptAsync(outputDirectory, "larger-memory", receipt, LargerMemoryMarkdown.Create(receipt)).ConfigureAwait(false);
            return 2;
        }

        if (!options.MemoryCapBytesSpecified)
        {
            var receipt = LargerMemoryReceipt.Invalid(options, environment, "--memory-cap-bytes is required for the larger-memory workload so the resource budget is explicit.");
            await WriteStandaloneReceiptAsync(outputDirectory, "larger-memory", receipt, LargerMemoryMarkdown.Create(receipt)).ConfigureAwait(false);
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
            var result = await LargerThanMemoryWorkload.RunAsync(
                options.ToConfiguration(),
                outputDirectory,
                cancellation.Token).ConfigureAwait(false);
            var receipt = new LargerMemoryReceipt(
                result.Status,
                result.StartedAtUtc,
                result.FinishedAtUtc,
                options,
                environment,
                result,
                Environment.CommandLine,
                outputDirectory);
            await WriteStandaloneReceiptAsync(outputDirectory, "larger-memory", receipt, LargerMemoryMarkdown.Create(receipt)).ConfigureAwait(false);
            return result.Status == "complete" ? 0 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> RunSubscriptionsAsync(string[] args)
    {
        SubscriptionCliOptions options;
        try
        {
            options = SubscriptionCliOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid subscriptions configuration: {exception.Message}");
            SubscriptionCliOptions.PrintHelp();
            return 2;
        }

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var environment = EnvironmentInfo.Create();
        if (options.ShowHelp)
        {
            SubscriptionCliOptions.PrintHelp();
            return 0;
        }

        if (!string.Equals(environment.BuildConfiguration, "Release", StringComparison.OrdinalIgnoreCase))
        {
            var receipt = SubscriptionReceipt.Invalid(options, environment, "Benchmark execution requires a Release artifact.");
            await WriteStandaloneReceiptAsync(outputDirectory, "subscriptions", receipt, SubscriptionMarkdown.Create(receipt)).ConfigureAwait(false);
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
            await using var hub = new ReactiveSubscriptionHub(new AllowAuthenticatedReactiveAuthorizer());
            var scenario = new SubscriptionFanoutScenario<int, int>(
                hub,
                new ReactiveOperationId("benchmark.subscriptions"),
                0,
                new ReactiveAuthorization(new TenantId("benchmark-tenant"), "benchmark"),
                static (_, _, _) => ValueTask.FromResult(0),
                dependencies: null,
                commitAsync: null,
                durableCommitPathAvailable: false,
                unsupportedReason: "The CLI has no durable commit-to-reactive source callback; use the embedding API to supply one.");
            var runOptions = new SubscriptionFanoutOptions
            {
                SubscriberCounts = options.SubscriberCounts,
                WarmupPublications = options.WarmupPublications,
                MeasuredPublications = options.MeasuredPublications,
            };
            var result = await SubscriptionFanoutWorkload.RunAsync(scenario, runOptions, cancellation.Token).ConfigureAwait(false);
            var rawPath = Path.Combine(outputDirectory, "subscriptions-raw-samples.jsonl");
            await using (var raw = new StreamWriter(rawPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                foreach (var item in result.Cases)
                {
                    await raw.WriteLineAsync(JsonSerializer.Serialize(item, RawJsonOptions)).ConfigureAwait(false);
                }
            }

            var receipt = new SubscriptionReceipt(
                result.Status,
                result.StartedAtUtc,
                result.FinishedAtUtc,
                options,
                environment,
                result,
                rawPath,
                Environment.CommandLine,
                outputDirectory);
            await WriteStandaloneReceiptAsync(outputDirectory, "subscriptions", receipt, SubscriptionMarkdown.Create(receipt)).ConfigureAwait(false);
            return result.Status == "complete" ? 0 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task WriteStandaloneReceiptAsync<T>(
        string outputDirectory,
        string name,
        T receipt,
        string markdown)
    {
        var receiptPath = Path.Combine(outputDirectory, $"{name}-receipt.json");
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, JsonOptions)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, $"{name}-summary.md"), markdown).ConfigureAwait(false);
        Console.WriteLine($"Receipt: {receiptPath}");
        Console.WriteLine($"Summary: {Path.Combine(outputDirectory, $"{name}-summary.md")}");
    }

    private sealed class BenchmarkRunner(BenchmarkOptions options)
    {
        private readonly string receiptDirectory = Path.GetFullPath(options.OutputDirectory);

        public async Task<int> RunAsync()
        {
            Directory.CreateDirectory(receiptDirectory);
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler? cancelHandler = null;
            cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;

            try
            {
                var environment = EnvironmentInfo.Create();
                if (!string.Equals(environment.BuildConfiguration, "Release", StringComparison.OrdinalIgnoreCase))
                {
                    var invalid = new BenchmarkReceipt(
                        Status: "invalid",
                        StartedAtUtc: environment.StartedAtUtc,
                        FinishedAtUtc: DateTimeOffset.UtcNow,
                        Configuration: options,
                        Environment: environment,
                        Workloads: [new WorkloadResult
                        {
                            Storage = options.Storage.ToString(),
                            Workload = "runner",
                            Temperature = "—",
                            Status = "invalid",
                            Error = $"Benchmark execution requires a Release artifact; loaded '{environment.BuildConfiguration}'.",
                            StartedAtUtc = environment.StartedAtUtc,
                            FinishedAtUtc = DateTimeOffset.UtcNow,
                        }],
                        Command: Environment.CommandLine,
                        ReceiptDirectory: receiptDirectory);
                    await WriteReportsAsync(invalid, cancellation.Token).ConfigureAwait(false);
                    return 2;
                }

                var results = new List<WorkloadResult>();
                foreach (var workload in options.Workloads)
                {
                    foreach (var temperature in options.Temperatures)
                    {
                        Console.WriteLine($"Running {options.Storage} {workload} {temperature} ({options.Samples} samples)...");
                        results.Add(await RunWorkloadAsync(workload, temperature, cancellation.Token).ConfigureAwait(false));
                    }
                }

                var receipt = new BenchmarkReceipt(
                    Status: results.All(static result => result.Status == "complete") ? "complete" : "failed",
                    StartedAtUtc: environment.StartedAtUtc,
                    FinishedAtUtc: DateTimeOffset.UtcNow,
                    Configuration: options,
                    Environment: environment,
                    Workloads: results,
                    Command: Environment.CommandLine,
                    ReceiptDirectory: receiptDirectory);
                await WriteReportsAsync(receipt, cancellation.Token).ConfigureAwait(false);
                var receiptPath = Path.Combine(receiptDirectory, "receipt.json");
                var markdownPath = Path.Combine(receiptDirectory, "summary.md");
                Console.WriteLine($"Receipt: {receiptPath}");
                Console.WriteLine($"Summary: {markdownPath}");
                return receipt.Status == "complete" ? 0 : 1;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        private async Task WriteReportsAsync(BenchmarkReceipt receipt, CancellationToken cancellationToken)
        {
            var receiptPath = Path.Combine(receiptDirectory, "receipt.json");
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, JsonOptions), cancellationToken).ConfigureAwait(false);
            var markdownPath = Path.Combine(receiptDirectory, "summary.md");
            await File.WriteAllTextAsync(markdownPath, MarkdownReport.Create(receipt, receiptPath), cancellationToken).ConfigureAwait(false);
        }

        private async Task<WorkloadResult> RunWorkloadAsync(
            Workload workload,
            Temperature temperature,
            CancellationToken cancellationToken)
        {
            var rawPath = Path.Combine(receiptDirectory, $"raw-samples-{options.Storage}-{workload}-{temperature}.jsonl");
            var result = new WorkloadResult
            {
                Storage = options.Storage.ToString(),
                Workload = workload.ToString(),
                Temperature = temperature.ToString(),
                RawSamplesPath = rawPath,
                StartedAtUtc = DateTimeOffset.UtcNow,
            };

            try
            {
                if (temperature == Temperature.Cold && options.Storage == StorageKind.Memory)
                {
                    throw new InvalidOperationException("Cold temperature requires a durable storage backend; memory has no reopen boundary.");
                }

                var engine = CreateEngine(workload, temperature);
                using var payload = CreatePayload(options.PayloadSize);
                var keys = CreateKeys(options.DatasetSize);
                try
                {
                    await SeedAsync(engine, keys, payload.RootElement, cancellationToken).ConfigureAwait(false);

                    var digest = WorkloadDigest.Create(options, workload, temperature, keys, payload.RootElement);
                    result.WorkloadDigest = digest;
                    if (temperature == Temperature.Warm)
                    {
                        await RunOperationsAsync(engine, workload, keys, payload.RootElement, options.WarmupOperations, null, 0, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var coldStart = Stopwatch.GetTimestamp();
                        await engine.DisposeAsync().ConfigureAwait(false);
                        engine = CreateEngine(workload, temperature);
                        result.ColdStartMilliseconds = Stopwatch.GetElapsedTime(coldStart).TotalMilliseconds;
                    }

                    await using var raw = new StreamWriter(rawPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    for (var sampleIndex = 0; sampleIndex < options.Samples; sampleIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sample = await RunSampleAsync(
                            engine,
                            workload,
                            temperature,
                            keys,
                            payload.RootElement,
                            sampleIndex,
                            cancellationToken).ConfigureAwait(false);
                        result.Samples.Add(sample);
                        await raw.WriteLineAsync(JsonSerializer.Serialize(sample, RawJsonOptions)).ConfigureAwait(false);
                        await raw.FlushAsync(cancellationToken).ConfigureAwait(false);
                        Console.WriteLine($"  sample {sampleIndex + 1}/{options.Samples}: {sample.ThroughputOpsPerSecond.ToString("F1", CultureInfo.InvariantCulture)} ops/s");
                    }

                    result.Status = "complete";
                }
                finally
                {
                    await engine.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result.Status = "failed";
                result.Error = "Cancelled by user.";
            }
            catch (Exception exception)
            {
                result.Status = "failed";
                result.Error = exception.ToString();
            }

            result.FinishedAtUtc = DateTimeOffset.UtcNow;
            return result;
        }

        private TerraBaseEngine CreateEngine(Workload workload, Temperature temperature)
        {
            if (options.Storage == StorageKind.Memory)
            {
                return TerraBaseEngine.Open();
            }

            var path = Path.Combine(receiptDirectory, "data", options.Storage.ToString(), $"{workload}-{temperature}");
            Directory.CreateDirectory(path);
            return options.Storage switch
            {
                StorageKind.StorageEngine => TerraBaseEngine.OpenDurable(path),
                StorageKind.ZoneTree => new TerraBaseEngine(new ZoneTreeStorageProvider(path)),
                _ => throw new ArgumentOutOfRangeException(),
            };
        }

        private static async Task SeedAsync(
            TerraBaseEngine engine,
            IReadOnlyList<DocumentKey> keys,
            JsonElement body,
            CancellationToken cancellationToken)
        {
            foreach (var key in keys)
            {
                var result = await engine.Documents.UpsertAsync(key, body, DocumentPrecondition.Missing, cancellationToken).ConfigureAwait(false);
                if (result.Outcome != WriteOutcome.Created)
                {
                    throw new InvalidOperationException($"Dataset seed did not create {key.DocumentId}: {result.Outcome}.");
                }
            }
        }

        private async Task<SampleMeasurement> RunSampleAsync(
            TerraBaseEngine engine,
            Workload workload,
            Temperature temperature,
            IReadOnlyList<DocumentKey> keys,
            JsonElement body,
            int sampleIndex,
            CancellationToken cancellationToken)
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var processCpuBefore = process.TotalProcessorTime;
            var workingSetBefore = process.WorkingSet64;
            var allocationsBefore = GC.GetTotalAllocatedBytes(false);
            var storageBefore = FileMetrics.TryRead(options.Storage, receiptDirectory, workload, temperature);
            var latencies = new long[options.OperationsPerSample];
            var bytesRead = 0L;
            var logicalBytes = 0L;
            var wallStart = Stopwatch.GetTimestamp();

            await RunOperationsAsync(
                engine,
                workload,
                keys,
                body,
                options.OperationsPerSample,
                latencies,
                sampleIndex + 1,
                cancellationToken,
                bytes => Interlocked.Add(ref bytesRead, bytes),
                bytes => Interlocked.Add(ref logicalBytes, bytes)).ConfigureAwait(false);

            var elapsed = Stopwatch.GetElapsedTime(wallStart);
            process.Refresh();
            var processCpuAfter = process.TotalProcessorTime;
            var workingSetAfter = process.WorkingSet64;
            var allocationsAfter = GC.GetTotalAllocatedBytes(false);
            var storageAfter = FileMetrics.TryRead(options.Storage, receiptDirectory, workload, temperature);
            Array.Sort(latencies);

            var bytesWritten = storageBefore?.WalBytes is { } beforeWal
                && storageAfter?.WalBytes is { } afterWal
                ? Math.Max(0, afterWal - beforeWal)
                : (long?)null;
            var writeAmplification = bytesWritten is > 0 && logicalBytes > 0
                ? bytesWritten.Value / (double)logicalBytes
                : (double?)null;
            var cpuTime = processCpuAfter - processCpuBefore;
            var cpuPercent = elapsed.TotalSeconds <= 0
                ? 0
                : cpuTime.TotalSeconds / elapsed.TotalSeconds / Environment.ProcessorCount * 100;

            return new SampleMeasurement
            {
                Sample = sampleIndex + 1,
                Operations = options.OperationsPerSample,
                Concurrency = options.Concurrency,
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
                ReturnedBytes = bytesRead,
                StorageBytesBefore = storageBefore?.TotalBytes,
                StorageBytesAfter = storageAfter?.TotalBytes,
                BytesWritten = bytesWritten,
                WriteAmplification = writeAmplification,
            };
        }

        private async Task RunOperationsAsync(
            TerraBaseEngine engine,
            Workload workload,
            IReadOnlyList<DocumentKey> keys,
            JsonElement body,
            int operationCount,
            long[]? latencies,
            int randomSalt,
            CancellationToken cancellationToken,
            Action<long>? onReadBytes = null,
            Action<long>? onLogicalBytes = null)
        {
            var workers = Enumerable.Range(0, options.Concurrency)
                .Select(worker => Task.Run(async () =>
                {
                    var random = new Random(unchecked(options.Seed + randomSalt * 7919 + worker * 104729));
                    for (var index = worker; index < operationCount; index += options.Concurrency)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var key = keys[random.Next(keys.Count)];
                        var started = Stopwatch.GetTimestamp();
                        var operation = await RunOperationAsync(engine, workload, key, body, cancellationToken).ConfigureAwait(false);
                        if (latencies is not null)
                        {
                            latencies[index] = Stopwatch.GetTimestamp() - started;
                        }

                        onReadBytes?.Invoke(operation.ReadBytes);
                        onLogicalBytes?.Invoke(operation.LogicalBytes);
                    }
                }, cancellationToken))
                .ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
        }

        private static async ValueTask<OperationResult> RunOperationAsync(
            TerraBaseEngine engine,
            Workload workload,
            DocumentKey key,
            JsonElement body,
            CancellationToken cancellationToken)
        {
            switch (workload)
            {
                case Workload.PointRead:
                {
                    var document = await engine.Documents.ReadAsync(key, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"Point read missed seeded key {key.DocumentId}.");
                    return new OperationResult(
                        ReadBytes: Encoding.UTF8.GetByteCount(document.Body.GetRawText()),
                        LogicalBytes: Encoding.UTF8.GetByteCount(document.Body.GetRawText()));
                }
                case Workload.PointWrite:
                {
                    var result = await engine.Documents.UpsertAsync(key, body, DocumentPrecondition.Any, cancellationToken).ConfigureAwait(false);
                    if (result.Outcome is not (WriteOutcome.Created or WriteOutcome.Updated))
                    {
                        throw new InvalidOperationException($"Point write failed for {key.DocumentId}: {result.Outcome}.");
                    }

                    var bytes = Encoding.UTF8.GetByteCount(body.GetRawText());
                    return new OperationResult(ReadBytes: 0, LogicalBytes: bytes);
                }
                case Workload.Scan:
                {
                    long bytes = 0;
                    await foreach (var document in engine.Documents.ScanAsync(key.Tenant, key.Collection, cancellationToken).ConfigureAwait(false))
                    {
                        bytes += Encoding.UTF8.GetByteCount(document.Body.GetRawText());
                    }

                    return new OperationResult(ReadBytes: bytes, LogicalBytes: bytes);
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(workload));
            }
        }

        private IReadOnlyList<DocumentKey> CreateKeys(int count)
        {
            var tenant = new TenantId("benchmark-tenant");
            var collection = new CollectionId("benchmark-collection");
            return Enumerable.Range(0, count)
                .Select(index => new DocumentKey(tenant, collection, $"key-{index:D8}"))
                .ToArray();
        }
    }

    private sealed class BenchmarkOptions
    {
        public StorageKind Storage { get; private set; } = StorageKind.Memory;
        public IReadOnlyList<Workload> Workloads { get; private set; } = [Workload.PointRead, Workload.PointWrite, Workload.Scan];
        public IReadOnlyList<Temperature> Temperatures { get; private set; } = [Temperature.Warm];
        public int DatasetSize { get; private set; } = 1_000;
        public int PayloadSize { get; private set; } = 256;
        public int Samples { get; private set; } = 5;
        public int WarmupOperations { get; private set; } = 500;
        public int OperationsPerSample { get; private set; } = 1_000;
        public int Concurrency { get; private set; } = 1;
        public int Seed { get; private set; } = 1337;
        public string OutputDirectory { get; private set; } = Path.Combine("bench", "receipts", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture));
        public bool ShowHelp { get; private set; }

        public static BenchmarkOptions Parse(string[] args)
        {
            var options = new BenchmarkOptions();
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
                    case "storage":
                        options.Storage = ParseEnum<StorageKind>(value, name);
                        break;
                    case "workload":
                        options.Workloads = value.Equals("all", StringComparison.OrdinalIgnoreCase)
                            ? [Workload.PointRead, Workload.PointWrite, Workload.Scan]
                            : [ParseEnum<Workload>(value, name)];
                        break;
                    case "temperature":
                        options.Temperatures = value.Equals("both", StringComparison.OrdinalIgnoreCase)
                            ? [Temperature.Warm, Temperature.Cold]
                            : [ParseEnum<Temperature>(value, name)];
                        break;
                    case "dataset-size":
                        options.DatasetSize = ParsePositive(value, name);
                        break;
                    case "payload-size":
                        options.PayloadSize = ParsePositive(value, name);
                        break;
                    case "samples":
                        options.Samples = ParsePositive(value, name);
                        if (options.Samples < 5)
                        {
                            throw new ArgumentException("--samples must be at least 5 so every receipt has five post-warmup samples.");
                        }

                        break;
                    case "warmup":
                        options.WarmupOperations = ParseNonNegative(value, name);
                        break;
                    case "operations":
                        options.OperationsPerSample = ParsePositive(value, name);
                        break;
                    case "concurrency":
                        options.Concurrency = ParsePositive(value, name);
                        break;
                    case "seed":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
                        {
                            throw new ArgumentException($"--{name} must be a 32-bit integer.");
                        }

                        options.Seed = seed;
                        break;
                    case "output":
                        options.OutputDirectory = value;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '--{name}'.");
                }
            }

            return options;
        }

        public static void PrintHelp()
        {
            Console.WriteLine("TerraBase real storage benchmark runner");
            Console.WriteLine("Options:");
            Console.WriteLine("  --storage memory|storage-engine|zonetree   Storage provider (default: memory)");
            Console.WriteLine("  --workload point-read|point-write|scan|compression|all");
            Console.WriteLine("  --temperature warm|cold|both      Warmup policy (default: warm)");
            Console.WriteLine("  --dataset-size N                  Seeded documents (default: 1000)");
            Console.WriteLine("  --payload-size N                  Payload string bytes (default: 256)");
            Console.WriteLine("  --samples N                       Post-warmup samples, minimum 5 (default: 5)");
            Console.WriteLine("  --warmup N                        Warmup operations (default: 500)");
            Console.WriteLine("  --operations N                    Operations per sample (default: 1000)");
            Console.WriteLine("  --concurrency N                   Concurrent workers (default: 1)");
            Console.WriteLine("  --seed N                          Fixed workload seed (default: 1337)");
            Console.WriteLine("  --output DIR                      Receipt directory");
        }

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

        private static int ParsePositive(string value, string name) => ParseInteger(value, name, 1);

        private static int ParseNonNegative(string value, string name) => ParseInteger(value, name, 0);

        private static int ParseInteger(string value, string name, int minimum)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum)
            {
                throw new ArgumentException($"--{name} must be an integer >= {minimum}.");
            }

            return parsed;
        }

        private static T ParseEnum<T>(string value, string name)
            where T : struct, Enum => Enum.TryParse<T>(value.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var parsed)
                ? parsed
                : throw new ArgumentException($"--{name} must be one of: {string.Join(", ", Enum.GetNames<T>())}.");
    }

    private enum StorageKind
    {
        Memory,
        StorageEngine,
        ZoneTree,
    }

    private enum Temperature
    {
        Warm,
        Cold,
    }

    private enum Workload
    {
        PointRead,
        PointWrite,
        Scan,
    }

    private sealed class WorkloadResult
    {
        public string Storage { get; init; } = string.Empty;
        public string Workload { get; init; } = string.Empty;
        public string Temperature { get; init; } = string.Empty;
        public string Status { get; set; } = "failed";
        public string? Error { get; set; }
        public string RawSamplesPath { get; init; } = string.Empty;
        public string? WorkloadDigest { get; set; }
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset FinishedAtUtc { get; set; }
        public double? ColdStartMilliseconds { get; set; }
        public List<SampleMeasurement> Samples { get; } = [];
    }

    private sealed class SampleMeasurement
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

    private sealed record BenchmarkReceipt(
        string Status,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        BenchmarkOptions Configuration,
        EnvironmentInfo Environment,
        IReadOnlyList<WorkloadResult> Workloads,
        string Command,
        string ReceiptDirectory);

    private sealed record EnvironmentInfo(
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
        public static EnvironmentInfo Create()
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
            var cpuModel = ReadCpuModel();
            return new EnvironmentInfo(
                DateTimeOffset.UtcNow,
                Environment.Version.ToString(),
                RuntimeInformation.FrameworkDescription,
                buildConfiguration,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                cpuModel,
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

                process.WaitForExit();
                return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
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

    private sealed record FileMetrics(long TotalBytes, long? WalBytes)
    {
        public static FileMetrics? TryRead(StorageKind storage, string receiptDirectory, Workload workload, Temperature temperature)
        {
            if (storage == StorageKind.Memory)
            {
                return null;
            }

            var root = Path.Combine(receiptDirectory, "data", storage.ToString(), $"{workload}-{temperature}");
            if (!Directory.Exists(root))
            {
                return null;
            }

            try
            {
                var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
                var total = files.Sum(static path => new FileInfo(path).Length);
                var walPath = Path.Combine(root, "wal");
                var wal = storage == StorageKind.StorageEngine && File.Exists(walPath)
                    ? new FileInfo(walPath).Length
                    : (long?)null;
                return new FileMetrics(total, wal);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    private static class WorkloadDigest
    {
        public static string Create(BenchmarkOptions options, Workload workload, Temperature temperature, IReadOnlyList<DocumentKey> keys, JsonElement body)
        {
            var text = string.Join("\n", [
                options.Storage.ToString(),
                workload.ToString(),
                temperature.ToString(),
                options.DatasetSize.ToString(CultureInfo.InvariantCulture),
                options.PayloadSize.ToString(CultureInfo.InvariantCulture),
                options.Samples.ToString(CultureInfo.InvariantCulture),
                options.WarmupOperations.ToString(CultureInfo.InvariantCulture),
                options.OperationsPerSample.ToString(CultureInfo.InvariantCulture),
                options.Concurrency.ToString(CultureInfo.InvariantCulture),
                options.Seed.ToString(CultureInfo.InvariantCulture),
                body.GetRawText(),
                string.Join("\n", keys.Select(static key => $"{key.Tenant.Value}/{key.Collection.Value}/{key.DocumentId}")),
            ]);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }
    }

    private static class MarkdownReport
    {
        public static string Create(BenchmarkReceipt receipt, string receiptPath)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# TerraBase benchmark receipt");
            builder.AppendLine();
            builder.AppendLine($"Status: **{receipt.Status}**");
            builder.AppendLine();
            builder.AppendLine($"Receipt JSON: `{Path.GetFileName(receiptPath)}`");
            builder.AppendLine();
            builder.AppendLine("## Environment");
            builder.AppendLine();
            builder.AppendLine($"- Commit: `{receipt.Environment.GitCommit ?? "unavailable"}` (dirty: `{receipt.Environment.GitDirty?.ToString() ?? "unknown"}`)");
            builder.AppendLine($"- Artifact SHA-256: `{receipt.Environment.ArtifactSha256 ?? "unavailable"}`");
            builder.AppendLine($"- Runtime: {receipt.Environment.FrameworkDescription}; build `{receipt.Environment.BuildConfiguration}`; {receipt.Environment.OperatingSystem}; {receipt.Environment.Architecture}");
            builder.AppendLine($"- CPU: {receipt.Environment.CpuModel}; {receipt.Environment.ProcessorCount} logical processors; available memory {receipt.Environment.AvailableMemoryBytes} bytes");
            builder.AppendLine($"- GC: server `{receipt.Environment.ServerGc}`, latency mode `{receipt.Environment.GcLatencyMode}`");
            builder.AppendLine();
            builder.AppendLine("## Results");
            builder.AppendLine();
            builder.AppendLine("| Storage | Workload | Temperature | Status | Samples | Cold start ms | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Raw samples |");
            builder.AppendLine("| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
            foreach (var result in receipt.Workloads)
            {
                var samples = result.Samples;
                builder.Append('|').Append(result.Storage).Append(" | ").Append(result.Workload).Append(" | ").Append(result.Temperature).Append(" | ").Append(result.Status).Append(" | ");
                builder.Append(samples.Count.ToString(CultureInfo.InvariantCulture)).Append(" | ");
                builder.Append(Format(result.ColdStartMilliseconds)).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.ThroughputOpsPerSecond)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.P50Microseconds)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.P95Microseconds)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.P99Microseconds)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.AllocatedBytesPerOperation)))).Append(" | ");
                builder.Append(Format(Median(samples.Select(static sample => sample.ProcessCpuPercent)))).Append(" | ");
                builder.Append(FormatLong(MedianLong(samples.Select(static sample => sample.ProcessWorkingSetBytesAfter)))).Append(" | ");
                builder.Append(FormatLong(MedianNullableLong(samples.Select(static sample => sample.BytesWritten)))).Append(" | ");
                builder.Append(Format(MedianNullable(samples.Select(static sample => sample.WriteAmplification)))).Append(" | ");
                builder.Append('`').Append(Path.GetFileName(result.RawSamplesPath)).AppendLine("` |");
                if (result.Error is not null)
                {
                    builder.AppendLine();
                    builder.AppendLine($"Failure for `{result.Workload}`: `{result.Error.Replace("\n", " ", StringComparison.Ordinal)}`");
                }
            }

            builder.AppendLine();
            builder.AppendLine($"Command: `{receipt.Command}`");
            builder.AppendLine();
            builder.AppendLine("All percentile values are medians of per-sample percentiles. `Bytes written` is the observed StorageEngine WAL length increase; memory and ZoneTree storage report no WAL write measurement.");
            return builder.ToString();
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

        private static double? MedianNullable(IEnumerable<double?> values)
        {
            var array = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
            return array.Length == 0 ? null : Median(array);
        }
    }

    private readonly record struct OperationResult(long ReadBytes, long LogicalBytes);

    private static JsonDocument CreatePayload(int payloadSize)
    {
        var payload = new string('x', payloadSize);
        return JsonDocument.Parse(JsonSerializer.Serialize(new { payload }));
    }

    private static long Percentile(long[] sortedTicks, double percentile)
    {
        var index = (int)Math.Ceiling(sortedTicks.Length * percentile) - 1;
        return sortedTicks[Math.Clamp(index, 0, sortedTicks.Length - 1)];
    }

    private static double ToMicroseconds(long stopwatchTicks) => stopwatchTicks * 1_000_000d / Stopwatch.Frequency;

    private sealed class LargerMemoryOptions
    {
        public LargerThanMemoryWorkload.Backend Backend { get; private set; } = LargerThanMemoryWorkload.Backend.StorageEngine;
        public int DatasetSize { get; private set; } = 32_768;
        public int PayloadSize { get; private set; } = 2_048;
        public long MemoryCapBytes { get; private set; } = 256L * 1024 * 1024;
        public bool MemoryCapBytesSpecified { get; private set; }
        public int Samples { get; private set; } = 5;
        public int OperationsPerSample { get; private set; } = 2_000;
        public int SustainedWriteOperations { get; private set; } = 2_000;
        public int WarmupOperations { get; private set; } = 1_000;
        public int Concurrency { get; private set; } = 1;
        public int Seed { get; private set; } = 1337;
        public double ZipfExponent { get; private set; } = 1.2;
        public double ReadRatio { get; private set; } = 0.8;
        public string OutputDirectory { get; private set; } = DefaultOutput("larger-memory");
        public bool ShowHelp { get; private set; }

        public static LargerMemoryOptions Parse(string[] args)
        {
            var options = new LargerMemoryOptions();
            for (var index = 0; index < args.Length; index++)
            {
                var (name, value) = ReadOption(args, ref index);
                switch (name)
                {
                    case "help":
                        options.ShowHelp = true;
                        break;
                    case "workload":
                        if (!value.Equals("larger-memory", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ArgumentException("--workload must be larger-memory.");
                        }

                        break;
                    case "backend":
                        options.Backend = ParseEnum<LargerThanMemoryWorkload.Backend>(value, name);
                        break;
                    case "dataset-size":
                        options.DatasetSize = ParseInteger(value, name, 1);
                        break;
                    case "payload-size":
                        options.PayloadSize = ParseInteger(value, name, 1);
                        break;
                    case "memory-cap-bytes":
                        options.MemoryCapBytes = ParseLong(value, name, 1);
                        options.MemoryCapBytesSpecified = true;
                        break;
                    case "samples":
                        options.Samples = ParseInteger(value, name, 5);
                        break;
                    case "operations":
                        options.OperationsPerSample = ParseInteger(value, name, 1);
                        break;
                    case "writes":
                        options.SustainedWriteOperations = ParseInteger(value, name, 0);
                        break;
                    case "warmup":
                        options.WarmupOperations = ParseInteger(value, name, 0);
                        break;
                    case "concurrency":
                        options.Concurrency = ParseInteger(value, name, 1);
                        break;
                    case "seed":
                        options.Seed = ParseInteger(value, name, int.MinValue);
                        break;
                    case "zipf-exponent":
                        options.ZipfExponent = ParseDouble(value, name, greaterThanZero: true);
                        break;
                    case "read-ratio":
                        options.ReadRatio = ParseDouble(value, name, greaterThanZero: false);
                        if (options.ReadRatio is < 0 or > 1)
                        {
                            throw new ArgumentException("--read-ratio must be between zero and one.");
                        }

                        break;
                    case "output":
                        options.OutputDirectory = value;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '--{name}'.");
                }
            }

            return options;
        }

        public static void PrintHelp() => Console.WriteLine("""
            TerraBase larger-than-memory benchmark
              --workload larger-memory       Run this workload
              --backend storage-engine|zonetree
              --memory-cap-bytes N           Required explicit resource cap
              --dataset-size N               Seeded documents (default: 32768)
              --payload-size N               Payload string bytes (default: 2048)
              --samples N                    Post-warmup samples, minimum 5
              --operations N                 Operations per phase/sample
              --writes N                     Sustained writes per sample
              --warmup N                     Warmup operations
              --concurrency N                Concurrent workers
              --seed N                       Deterministic seed
              --zipf-exponent N              Zipf exponent
              --read-ratio N                 Read fraction from zero to one
              --output DIR                   Receipt directory
            """);

        public LargerThanMemoryWorkload.Configuration ToConfiguration() => new()
        {
            Backend = Backend,
            DatasetSize = DatasetSize,
            PayloadSize = PayloadSize,
            MemoryCapBytes = MemoryCapBytes,
            Samples = Samples,
            OperationsPerSample = OperationsPerSample,
            SustainedWriteOperations = SustainedWriteOperations,
            WarmupOperations = WarmupOperations,
            Concurrency = Concurrency,
            Seed = Seed,
            ZipfExponent = ZipfExponent,
            ReadRatio = ReadRatio,
        };
    }

    private sealed class SubscriptionCliOptions
    {
        public IReadOnlyList<int> SubscriberCounts { get; private set; } = SubscriptionFanoutWorkload.DefaultSubscriberCounts;
        public int WarmupPublications { get; private set; } = 2;
        public int MeasuredPublications { get; private set; } = 5;
        public string OutputDirectory { get; private set; } = DefaultOutput("subscriptions");
        public bool ShowHelp { get; private set; }

        public static SubscriptionCliOptions Parse(string[] args)
        {
            var options = new SubscriptionCliOptions();
            for (var index = 0; index < args.Length; index++)
            {
                var (name, value) = ReadOption(args, ref index);
                switch (name)
                {
                    case "help":
                        options.ShowHelp = true;
                        break;
                    case "workload":
                        if (!value.Equals("subscriptions", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ArgumentException("--workload must be subscriptions.");
                        }

                        break;
                    case "subscriber-counts":
                        options.SubscriberCounts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .Select(item => ParseInteger(item, name, 1))
                            .ToArray();
                        break;
                    case "warmup-publications":
                        options.WarmupPublications = ParseInteger(value, name, 0);
                        break;
                    case "publications":
                    case "measured-publications":
                        options.MeasuredPublications = ParseInteger(value, name, 1);
                        break;
                    case "output":
                        options.OutputDirectory = value;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '--{name}'.");
                }
            }

            return options;
        }

        public static void PrintHelp() => Console.WriteLine("""
            TerraBase reactive subscription fan-out benchmark
              --workload subscriptions          Run this workload
              --subscriber-counts 1,10,100,1000 Subscriber counts
              --warmup-publications N           Warmup publications
              --publications N                  Measured publications
              --output DIR                      Receipt directory
            The CLI reports unsupported until a durable commit callback is supplied through the embedding API.
            """);
    }

    private static string DefaultOutput(string name) => Path.Combine(
        "bench",
        "receipts",
        $"{name}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}");

    private static (string Name, string Value) ReadOption(string[] args, ref int index)
    {
        var argument = args[index];
        if (argument is "--help" or "-h")
        {
            return ("help", string.Empty);
        }

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

    private static long ParseLong(string value, string name, long minimum)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum)
        {
            throw new ArgumentException($"--{name} must be an integer >= {minimum}.");
        }

        return parsed;
    }

    private static double ParseDouble(string value, string name, bool greaterThanZero)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed)
            || (greaterThanZero ? parsed <= 0 : parsed < 0))
        {
            throw new ArgumentException($"--{name} must be a finite number {(greaterThanZero ? "greater than zero" : "zero or greater")}.");
        }

        return parsed;
    }

    private static T ParseEnum<T>(string value, string name)
        where T : struct, Enum => Enum.TryParse<T>(value.Replace("-", string.Empty, StringComparison.Ordinal), true, out var parsed)
            ? parsed
            : throw new ArgumentException($"--{name} must be one of: {string.Join(", ", Enum.GetNames<T>())}.");

    private sealed record LargerMemoryReceipt(
        string Status,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        LargerMemoryOptions Configuration,
        EnvironmentInfo Environment,
        LargerThanMemoryWorkload.RunResult? Result,
        string Command,
        string ReceiptDirectory)
    {
        public static LargerMemoryReceipt Invalid(LargerMemoryOptions options, EnvironmentInfo environment, string error) =>
            new("invalid", environment.StartedAtUtc, DateTimeOffset.UtcNow, options, environment, new LargerThanMemoryWorkload.RunResult
            {
                Status = "invalid",
                Error = error,
                StartedAtUtc = environment.StartedAtUtc,
                FinishedAtUtc = DateTimeOffset.UtcNow,
            }, global::System.Environment.CommandLine, Path.GetFullPath(options.OutputDirectory));
    }

    private sealed record SubscriptionReceipt(
        string Status,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        SubscriptionCliOptions Configuration,
        EnvironmentInfo Environment,
        SubscriptionFanoutRun Result,
        string RawSamplesPath,
        string Command,
        string ReceiptDirectory)
    {
        public static SubscriptionReceipt Invalid(SubscriptionCliOptions options, EnvironmentInfo environment, string error) =>
            new("invalid", environment.StartedAtUtc, DateTimeOffset.UtcNow, options, environment,
                new SubscriptionFanoutRun("invalid", "ReactiveSubscriptionHub", "CLI", false, environment.StartedAtUtc, DateTimeOffset.UtcNow, error, []),
                string.Empty, global::System.Environment.CommandLine, Path.GetFullPath(options.OutputDirectory));
    }

    private static class LargerMemoryMarkdown
    {
        public static string Create(LargerMemoryReceipt receipt)
        {
            var builder = Header("TerraBase larger-than-memory benchmark", receipt.Status, receipt.Environment, receipt.Command);
            builder.AppendLine("## Configuration");
            builder.AppendLine();
            builder.AppendLine($"Backend: `{receipt.Configuration.Backend}`; dataset: `{receipt.Configuration.DatasetSize:N0}` rows; payload: `{receipt.Configuration.PayloadSize:N0}` bytes; memory cap: `{receipt.Configuration.MemoryCapBytes:N0}` bytes.");
            builder.AppendLine();
            builder.AppendLine("## Phase results");
            builder.AppendLine();
            builder.AppendLine("| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |");
            builder.AppendLine("| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            if (receipt.Result is null || receipt.Result.Samples.Count == 0)
            {
                builder.AppendLine($"| — | — | — | {receipt.Result?.Status ?? "invalid"} | — | — | — | — | — | — | — | — | — | — |");
            }
            else
            {
                foreach (var sample in receipt.Result.Samples)
                {
                    foreach (var phase in sample.Phases)
                    {
                        builder.Append('|').Append(sample.Sample).Append(" | ").Append(phase.Name).Append(" | ").Append(phase.Kind).Append(" | complete | ")
                            .Append(phase.Operations).Append(" | ").Append(Format(phase.ThroughputOpsPerSecond)).Append(" | ")
                            .Append(Format(phase.P50Microseconds)).Append(" | ").Append(Format(phase.P95Microseconds)).Append(" | ")
                            .Append(Format(phase.P99Microseconds)).Append(" | ").Append(Format(phase.AllocatedBytesPerOperation)).Append(" | ")
                            .Append(Format(phase.ProcessCpuPercent)).Append(" | ").Append(FormatLong(phase.ProcessWorkingSetBytesAfter)).Append(" | ")
                            .Append(FormatLong(phase.WalBytesAfter)).Append(" | ").Append(Format(phase.WriteAmplification)).AppendLine(" |");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(receipt.Result?.Error))
            {
                builder.AppendLine();
                builder.AppendLine($"Error: `{receipt.Result.Error!.Replace('\n', ' ')}`");
            }

            return builder.ToString();
        }
    }

    private static class SubscriptionMarkdown
    {
        public static string Create(SubscriptionReceipt receipt)
        {
            var builder = Header("TerraBase reactive subscription fan-out benchmark", receipt.Status, receipt.Environment, receipt.Command);
            builder.AppendLine("## Results");
            builder.AppendLine();
            builder.AppendLine($"Subject: `{receipt.Result.Subject}`; boundary: `{receipt.Result.OperationBoundary}`; durable commit path: `{receipt.Result.DurableCommitPathAvailable}`.");
            builder.AppendLine();
            builder.AppendLine("| Subscribers | Status | Responsive | Delivered frames | Egress bytes | p50 µs | p95 µs | p99 µs | Allocated bytes/frame | CPU ms | RSS after bytes | Unsupported reason |");
            builder.AppendLine("| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
            foreach (var item in receipt.Result.Cases)
            {
                builder.Append('|').Append(item.Subscribers).Append(" | ").Append(item.Status).Append(" | ")
                    .Append(FormatLong(item.ResponsiveSubscribers)).Append(" | ").Append(FormatLong(item.DeliveredFrames)).Append(" | ")
                    .Append(FormatLong(item.EgressBytes)).Append(" | ").Append(Format(item.P50DeliveryLatencyMicroseconds)).Append(" | ")
                    .Append(Format(item.P95DeliveryLatencyMicroseconds)).Append(" | ").Append(Format(item.P99DeliveryLatencyMicroseconds)).Append(" | ")
                    .Append(Format(item.AllocatedBytesPerDeliveredFrame)).Append(" | ").Append(Format(item.ProcessCpuMilliseconds)).Append(" | ")
                    .Append(FormatLong(item.ProcessWorkingSetBytesAfter)).Append(" | ").Append(item.UnsupportedReason ?? "—").AppendLine(" |");
            }

            if (!string.IsNullOrWhiteSpace(receipt.Result.UnsupportedReason))
            {
                builder.AppendLine();
                builder.AppendLine($"Unsupported reason: `{receipt.Result.UnsupportedReason}`");
            }

            return builder.ToString();
        }
    }

    private static StringBuilder Header(string title, string status, EnvironmentInfo environment, string command)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Status: **{status}**");
        builder.AppendLine();
        builder.AppendLine($"- Commit: `{environment.GitCommit ?? "unavailable"}` (dirty: `{environment.GitDirty?.ToString() ?? "unknown"}`)");
        builder.AppendLine($"- Artifact SHA-256: `{environment.ArtifactSha256 ?? "unavailable"}`");
        builder.AppendLine($"- Runtime: {environment.FrameworkDescription}; build `{environment.BuildConfiguration}`; {environment.OperatingSystem}; {environment.Architecture}");
        builder.AppendLine($"- CPU: {environment.CpuModel}; {environment.ProcessorCount} logical processors; available memory {environment.AvailableMemoryBytes} bytes");
        builder.AppendLine();
        builder.AppendLine($"Command: `{command}`");
        builder.AppendLine();
        return builder;
    }

    private static string Format(double? value) => value is null ? "—" : value.Value.ToString("F2", CultureInfo.InvariantCulture);

    private static string FormatLong(long? value) => value is null ? "—" : value.Value.ToString("N0", CultureInfo.InvariantCulture);
}
