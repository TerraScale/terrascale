using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Api;
using TerraBase.Contracts;
using TerraBase.Security;
using TerraBase.Transport;

namespace TerraBase.Benchmarks;

/// <summary>
/// Measures one source and one destination TerraBase host through the real
/// authenticated MagicOnion member protocol. The source and destination have
/// separate durable storage directories and are never connected in-process.
/// </summary>
public static class MagicOnionReplicationWorkload
{
    private const string WorkloadName = "magic-onion-replication";
    private const string Service = "TerraBase.InterNode";
    private const string Method = "InvokeAsync";
    private const string Origin = "magic-onion-benchmark";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions RawJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Returns whether this workload owns the requested command.</summary>
    public static bool IsRequested(string[] args) =>
        ReadOption(args, "workload")?.Equals(WorkloadName, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Runs the real two-host MagicOnion replication workload.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        MagicOnionReplicationOptions options;
        try
        {
            options = MagicOnionReplicationOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid MagicOnion replication configuration: {exception.Message}");
            MagicOnionReplicationOptions.PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            MagicOnionReplicationOptions.PrintHelp();
            return 0;
        }

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var startedAt = DateTimeOffset.UtcNow;
        var environment = BuildEnvironmentIdentity();
        var samples = new List<MagicOnionReplicationSample>(options.Samples);
        var rawPath = Path.Combine(outputDirectory, "raw-samples.jsonl");
        await using var raw = new StreamWriter(
            rawPath,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            for (var index = 0; index < options.Samples; index++)
            {
                var sample = await RunSampleAsync(options, index + 1).ConfigureAwait(false);
                samples.Add(sample);
                await raw.WriteLineAsync(JsonSerializer.Serialize(sample, RawJsonOptions)).ConfigureAwait(false);
                await raw.FlushAsync().ConfigureAwait(false);
                Console.WriteLine(
                    $"MagicOnion sample {sample.Sample}: {sample.Operations} operations, "
                    + $"send p50 {sample.SendP50Microseconds:N0} us, "
                    + $"repair {FormatMilliseconds(sample.RepairLagMilliseconds)} ms");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failed = new MagicOnionReplicationReceipt(
                "failed",
                startedAt,
                DateTimeOffset.UtcNow,
                options,
                samples,
                rawPath,
                Environment.CommandLine,
                outputDirectory,
                environment,
                exception.ToString());
            await WriteReceiptAsync(failed, outputDirectory).ConfigureAwait(false);
            Console.Error.WriteLine($"MagicOnion replication benchmark failed: {exception.Message}");
            return 1;
        }

        var receipt = new MagicOnionReplicationReceipt(
            "complete",
            startedAt,
            DateTimeOffset.UtcNow,
            options,
            samples,
            rawPath,
            Environment.CommandLine,
            outputDirectory,
            environment,
            Error: null);
        await WriteReceiptAsync(receipt, outputDirectory).ConfigureAwait(false);
        return 0;
    }

    private static async Task<MagicOnionReplicationSample> RunSampleAsync(
        MagicOnionReplicationOptions options,
        int sampleNumber)
    {
        var database = EntityId.New();
        var tenant = EntityId.New();
        await using var peers = await MagicOnionPeerPair.StartAsync(database, tenant).ConfigureAwait(false);
        await using var sourceClient = peers.CreateSourceClient();
        await using var destinationClient = peers.CreateDestinationClient();

        var publications = CreatePublications(tenant, options.Operations);
        var sourceSeedStarted = Stopwatch.GetTimestamp();
        var sourceSeed = await sourceClient.InvokeAsync<ReplicationPushRequest, ReplicationPushResponse>(
            InterNodeRequestContext.Mutation(
                Service,
                Method,
                $"seed-{sampleNumber}",
                $"seed-digest-{sampleNumber}"),
            new ReplicationPushRequest
            {
                DatabaseUlid = database,
                Publications = publications,
                EndOfBatch = true,
            }).ConfigureAwait(false);
        var sourceSeedMilliseconds = Stopwatch.GetElapsedTime(sourceSeedStarted).TotalMilliseconds;
        if (!sourceSeed.Value.DurableAcknowledged)
            throw new InvalidOperationException("The source host did not durably acknowledge its seed batch.");

        var pulled = await PullAsync(sourceClient, database, options.Operations).ConfigureAwait(false);
        if (pulled.Count != publications.Length)
        {
            throw new InvalidOperationException(
                $"The source stream returned {pulled.Count} publications; expected {publications.Length}.");
        }

        if (options.ReverseDelivery)
            pulled.Reverse();

        var sendLatencies = new List<double>(pulled.Count * (options.DuplicateDelivery ? 2 : 1));
        var durableAckLatencies = new List<double>(sendLatencies.Capacity);
        var appliedFrontierLatencies = new List<double>(pulled.Count);
        var appliedAtMilliseconds = new Dictionary<ulong, double>();
        var runStarted = Stopwatch.GetTimestamp();
        double? firstDeferredMilliseconds = null;
        double? fullyAppliedMilliseconds = null;
        var durableAcknowledgements = 0;
        var duplicateResponses = 0;
        var deferredResponses = 0;
        ReplicationPushResponse? finalResponse = null;

        foreach (var publication in pulled)
        {
            if (options.DelayMilliseconds > 0)
                await Task.Delay(options.DelayMilliseconds).ConfigureAwait(false);

            var response = await SendAsync(
                destinationClient,
                database,
                publication,
                $"delivery-{sampleNumber}-{publication.Sequence}-0",
                sendLatencies,
                durableAckLatencies).ConfigureAwait(false);
            finalResponse = response;
            durableAcknowledgements += response.DurableAcknowledged ? 1 : 0;
            RecordApplyProgress(
                response,
                publication.Sequence,
                runStarted,
                appliedAtMilliseconds,
                appliedFrontierLatencies,
                ref fullyAppliedMilliseconds,
                ref firstDeferredMilliseconds,
                ref deferredResponses);

            if (options.DuplicateDelivery)
            {
                var duplicate = await SendAsync(
                    destinationClient,
                    database,
                    publication,
                    $"delivery-{sampleNumber}-{publication.Sequence}-1",
                    sendLatencies,
                    durableAckLatencies).ConfigureAwait(false);
                finalResponse = duplicate;
                durableAcknowledgements += duplicate.DurableAcknowledged ? 1 : 0;
                duplicateResponses += duplicate.Results.Count(result =>
                    result.Outcome == TerraBase.Contracts.ReplicationApplyOutcome.Duplicate);
                RecordApplyProgress(
                    duplicate,
                    publication.Sequence,
                    runStarted,
                    appliedAtMilliseconds,
                    appliedFrontierLatencies,
                    ref fullyAppliedMilliseconds,
                    ref firstDeferredMilliseconds,
                    ref deferredResponses);
            }
        }

        if (finalResponse is null)
            throw new InvalidOperationException("The destination did not receive a publication.");

        var destinationRuntime = await peers.DestinationEngine
            .GetDurableReplicationAsync(database)
            .ConfigureAwait(false);
        var durableFrontier = destinationRuntime.DurableFrontier.Contiguous.GetValueOrDefault(Origin);
        var appliedFrontier = destinationRuntime.AppliedFrontier.GetValueOrDefault(Origin);
        var expected = checked((ulong)options.Operations);
        if (durableFrontier != expected || appliedFrontier != expected)
        {
            throw new InvalidOperationException(
                $"The destination frontier did not converge: durable={durableFrontier}, applied={appliedFrontier}, expected={expected}.");
        }

        fullyAppliedMilliseconds ??= Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds;
        double? repairLagMilliseconds = firstDeferredMilliseconds is { } deferred
            ? fullyAppliedMilliseconds.Value - deferred
            : null;
        return new MagicOnionReplicationSample(
            sampleNumber,
            options.Operations,
            options.DelayMilliseconds,
            options.DuplicateDelivery,
            options.ReverseDelivery,
            sourceSeedMilliseconds,
            sourceSeed.Value.DurableAcknowledged,
            sendLatencies.Count,
            durableAcknowledgements,
            duplicateResponses,
            deferredResponses,
            durableFrontier,
            appliedFrontier,
            sendLatencies.Count == 0 ? null : Percentile(sendLatencies, 0.50),
            sendLatencies.Count == 0 ? null : Percentile(sendLatencies, 0.95),
            sendLatencies.Count == 0 ? null : Percentile(sendLatencies, 0.99),
            durableAckLatencies.Count == 0 ? null : Percentile(durableAckLatencies, 0.50),
            durableAckLatencies.Count == 0 ? null : Percentile(durableAckLatencies, 0.95),
            durableAckLatencies.Count == 0 ? null : Percentile(durableAckLatencies, 0.99),
            appliedFrontierLatencies.Count == 0 ? null : Percentile(appliedFrontierLatencies, 0.50),
            appliedFrontierLatencies.Count == 0 ? null : Percentile(appliedFrontierLatencies, 0.95),
            appliedFrontierLatencies.Count == 0 ? null : Percentile(appliedFrontierLatencies, 0.99),
            repairLagMilliseconds,
            appliedAtMilliseconds.Count);
    }

    private static async Task<ReplicationPushResponse> SendAsync(
        MagicOnionInterNodeClient client,
        EntityId database,
        ReplicationPublication publication,
        string requestId,
        List<double> sendLatencies,
        List<double> durableAckLatencies)
    {
        var started = Stopwatch.GetTimestamp();
        var response = await client.InvokeAsync<ReplicationPushRequest, ReplicationPushResponse>(
            InterNodeRequestContext.Mutation(Service, Method, requestId, $"{requestId}-digest"),
            new ReplicationPushRequest
            {
                DatabaseUlid = database,
                Publications = [publication],
                EndOfBatch = true,
            }).ConfigureAwait(false);
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        sendLatencies.Add(elapsedMilliseconds * 1_000.0);
        if (response.Value.DurableAcknowledged)
            durableAckLatencies.Add(elapsedMilliseconds * 1_000.0);
        return response.Value;
    }

    private static void RecordApplyProgress(
        ReplicationPushResponse response,
        ulong publicationSequence,
        long runStarted,
        IDictionary<ulong, double> appliedAtMilliseconds,
        List<double> appliedFrontierLatencies,
        ref double? fullyAppliedMilliseconds,
        ref double? firstDeferredMilliseconds,
        ref int deferredResponses)
    {
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds;
        if (response.Results.Any(result => result.Outcome == TerraBase.Contracts.ReplicationApplyOutcome.Deferred))
        {
            deferredResponses++;
            firstDeferredMilliseconds ??= elapsedMilliseconds;
        }

        var applied = response.AppliedFrontier.GetValueOrDefault(Origin);
        if (applied > 0)
        {
            appliedFrontierLatencies.Add(elapsedMilliseconds * 1_000.0);
            for (var sequence = 1UL; sequence <= applied; sequence++)
                appliedAtMilliseconds.TryAdd(sequence, elapsedMilliseconds);
            if (applied >= publicationSequence)
                fullyAppliedMilliseconds ??= elapsedMilliseconds;
        }
    }

    private static async Task<List<ReplicationPublication>> PullAsync(
        MagicOnionInterNodeClient sourceClient,
        EntityId database,
        int expectedCount)
    {
        var pulled = new List<ReplicationPublication>(expectedCount);
        await foreach (var item in sourceClient.StreamAsync<ReplicationPullRequest, ReplicationPullResponse>(
            InterNodeRequestContext.Read(Service, "StreamAsync"),
            new ReplicationPullRequest
            {
                DatabaseUlid = database,
                MaxPublications = expectedCount,
                MaxBytes = 8 * 1024 * 1024,
                IncludeTombstones = true,
            }).ConfigureAwait(false))
        {
            pulled.AddRange(item.Value.Publications);
        }

        return pulled;
    }

    private static ReplicationPublication[] CreatePublications(EntityId tenant, int operations)
    {
        var publications = new ReplicationPublication[operations];
        for (var index = 0; index < operations; index++)
        {
            var sequence = checked((ulong)index + 1);
            publications[index] = new ReplicationPublication
            {
                TenantUlid = tenant,
                Collection = "magic-onion-benchmark",
                DocumentId = $"document-{sequence:D8}",
                Origin = Origin,
                Sequence = sequence,
                Context = sequence == 1
                    ? new Dictionary<string, ulong>(StringComparer.Ordinal)
                    : new Dictionary<string, ulong>(StringComparer.Ordinal) { [Origin] = sequence - 1 },
                Hlc = checked((long)sequence),
                CanonicalBody = Encoding.UTF8.GetBytes($"{{\"sequence\":{sequence}}}"),
            };
        }

        return publications;
    }

    private static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        var ordered = values.OrderBy(static value => value).ToArray();
        var position = (ordered.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return ordered[lower];
        var weight = position - lower;
        return ordered[lower] + (ordered[upper] - ordered[lower]) * weight;
    }

    private static string FormatMilliseconds(double? value) =>
        value is { } milliseconds ? milliseconds.ToString("F3", CultureInfo.InvariantCulture) : "unsupported";

    private static async Task WriteReceiptAsync(
        MagicOnionReplicationReceipt receipt,
        string outputDirectory)
    {
        var receiptPath = Path.Combine(outputDirectory, "receipt.json");
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, JsonOptions)).ConfigureAwait(false);
        var summaryPath = Path.Combine(outputDirectory, "summary.md");
        await File.WriteAllTextAsync(summaryPath, CreateMarkdown(receipt, receiptPath)).ConfigureAwait(false);
        Console.WriteLine($"MagicOnion replication receipt: {receiptPath}");
        Console.WriteLine($"MagicOnion replication summary: {summaryPath}");
    }

    private static string CreateMarkdown(MagicOnionReplicationReceipt receipt, string receiptPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# TerraBase MagicOnion durable replication");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Status: **{receipt.Status}**  ");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Receipt: `{receiptPath}`  ");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Command: `{receipt.Command}`");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Commit: `{receipt.Environment.GitCommit ?? "unavailable"}` (dirty: `{receipt.Environment.GitDirty?.ToString() ?? "unknown"}`)");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Benchmark artifact SHA-256: `{receipt.Environment.ArtifactSha256 ?? "unavailable"}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"TerraBase artifact SHA-256: `{receipt.Environment.TerraBaseArtifactSha256 ?? "unavailable"}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Runtime: {receipt.Environment.FrameworkDescription}; {receipt.Environment.Runtime}; {receipt.Environment.OperatingSystem}; {receipt.Environment.Architecture}; build `{receipt.Environment.BuildConfiguration}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"CPU: {receipt.Environment.CpuModel}; {receipt.Environment.ProcessorCount} logical processors; available memory {receipt.Environment.AvailableMemoryBytes:N0} bytes");
        builder.AppendLine(CultureInfo.InvariantCulture, $"GC: server `{receipt.Environment.ServerGc}`, latency mode `{receipt.Environment.GcLatencyMode}`");
        builder.AppendLine();
        builder.AppendLine("Each sample uses two independent HTTPS/mTLS TerraBase hosts with separate durable directories. Source seeding and stream pull use MagicOnion; delivery to the destination uses one unary MagicOnion push per publication and optional duplicate delivery.");
        builder.AppendLine();
        builder.AppendLine("The protocol reports `DurableAcknowledged` in the push response and does not expose a separate server-side durable-ack timestamp. Durable-ack latency therefore uses the same client-observed push response boundary and is labelled accordingly.");
        builder.AppendLine();
        builder.AppendLine("| Sample | Ops | Send p50 (us) | Send p95 (us) | Send p99 (us) | Ack p50 (us) | Applied p95 (us) | Repair lag (ms) | Deferred responses | Duplicate outcomes | Final durable | Final applied |");
        builder.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var sample in receipt.Samples)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {sample.Sample} | {sample.Operations} | {Format(sample.SendP50Microseconds)} | {Format(sample.SendP95Microseconds)} | {Format(sample.SendP99Microseconds)} | {Format(sample.DurableAckP50Microseconds)} | {Format(sample.AppliedFrontierP95Microseconds)} | {FormatMilliseconds(sample.RepairLagMilliseconds)} | {sample.DeferredResponses} | {sample.DuplicateOutcomes} | {sample.FinalDurableFrontier} | {sample.FinalAppliedFrontier} |");
        }

        builder.AppendLine();
        builder.AppendLine("Unsupported claims and limits:");
        builder.AppendLine();
        builder.AppendLine("- This harness measures loopback TLS and local filesystem durability; it is not a WAN, cross-region, or object-storage benchmark.");
        builder.AppendLine("- Delivery delay is an application-controlled delay between unary RPCs, not injected network latency inside Kestrel or gRPC.");
        builder.AppendLine("- Duplicate outcomes exercise repeated publication dots with distinct idempotency keys; transport-level packet duplication is not observable from this API.");
        builder.AppendLine("- Applied-frontier latency is reported only after a response exposes a non-zero applied frontier. Repair lag is unsupported when the selected ordering produces no deferred response.");
        builder.AppendLine("- The benchmark does not claim crash recovery, partition healing, or throughput saturation; those require separate failure and capacity runs.");
        return builder.ToString();
    }

    private static string Format(double? value) =>
        value is { } number ? number.ToString("F1", CultureInfo.InvariantCulture) : "unsupported";

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith($"--{name}=", StringComparison.OrdinalIgnoreCase))
                return argument[($"--{name}=".Length)..];
            if (argument.Equals($"--{name}", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
                return args[index + 1];
        }

        return null;
    }

    private static EnvironmentIdentity BuildEnvironmentIdentity()
    {
        var artifactPath = Assembly.GetExecutingAssembly().Location;
        var terraBasePath = typeof(TerraBaseHost).Assembly.Location;
        var gitRoot = FindGitRoot();
        return new EnvironmentIdentity(
            DateTimeOffset.UtcNow,
            Environment.Version.ToString(),
            RuntimeInformation.FrameworkDescription,
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            ReadCpuModel(),
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            GCSettings.IsServerGC,
            GCSettings.LatencyMode.ToString(),
            gitRoot is null ? null : RunGit(gitRoot, "rev-parse", "HEAD"),
            gitRoot is not null && !string.IsNullOrWhiteSpace(RunGit(gitRoot, "status", "--porcelain")),
            string.IsNullOrWhiteSpace(artifactPath) ? null : artifactPath,
            HashFile(artifactPath),
            string.IsNullOrWhiteSpace(terraBasePath) ? null : terraBasePath,
            HashFile(terraBasePath));
    }

    private static string? FindGitRoot()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
            current = current.Parent;
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

    private static string? HashFile(string? path) =>
        path is { Length: > 0 } && File.Exists(path)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            : null;

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
            // The host model is optional metadata.
        }

        return "unknown";
    }

    private sealed class MagicOnionReplicationOptions
    {
        public int Samples { get; private init; } = 3;
        public int Operations { get; private init; } = 32;
        public int DelayMilliseconds { get; private init; } = 2;
        public bool DuplicateDelivery { get; private init; } = true;
        public bool ReverseDelivery { get; private init; } = true;
        public string OutputDirectory { get; private init; } = Path.Combine("bench", "receipts", "magic-onion-replication");
        public bool ShowHelp { get; private init; }

        public static MagicOnionReplicationOptions Parse(string[] args)
        {
            if (args.Any(argument => argument is "--help" or "-h"))
                return new MagicOnionReplicationOptions { ShowHelp = true };

            return new MagicOnionReplicationOptions
            {
                Samples = ParseInteger(args, "samples", 1, 100),
                Operations = ParseInteger(args, "operations", 2, 256),
                DelayMilliseconds = ParseInteger(args, "delay-ms", 0, 60_000),
                DuplicateDelivery = ParseBoolean(args, "duplicates", true),
                ReverseDelivery = ParseBoolean(args, "reorder", true),
                OutputDirectory = ReadOption(args, "output") ?? Path.Combine(
                    "bench",
                    "receipts",
                    $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-magic-onion-replication"),
            };
        }

        public static void PrintHelp()
        {
            Console.WriteLine("--workload magic-onion-replication");
            Console.WriteLine("  --samples N       independent two-host samples (default 3)");
            Console.WriteLine("  --operations N    causal publications per sample, 2..256 (default 32)");
            Console.WriteLine("  --delay-ms N      delay before each delivery RPC (default 2)");
            Console.WriteLine("  --duplicates BOOL deliver each publication twice (default true)");
            Console.WriteLine("  --reorder BOOL    reverse delivery order (default true)");
            Console.WriteLine("  --output PATH     receipt directory");
        }

        private static int ParseInteger(string[] args, string name, int minimum, int maximum)
        {
            var value = ReadOption(args, name);
            if (value is null)
                return name switch
                {
                    "samples" => 3,
                    "operations" => 32,
                    "delay-ms" => 2,
                    _ => throw new ArgumentOutOfRangeException(nameof(name)),
                };
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed < minimum
                || parsed > maximum)
            {
                throw new ArgumentException($"--{name} must be between {minimum} and {maximum}.", nameof(args));
            }

            return parsed;
        }

        private static bool ParseBoolean(string[] args, string name, bool defaultValue)
        {
            var value = ReadOption(args, name);
            if (value is null)
                return defaultValue;
            if (!bool.TryParse(value, out var parsed))
                throw new ArgumentException($"--{name} must be true or false.", nameof(args));
            return parsed;
        }
    }

    private sealed record MagicOnionReplicationSample(
        int Sample,
        int Operations,
        int DelayMilliseconds,
        bool DuplicateDelivery,
        bool ReverseDelivery,
        double SourceSeedMilliseconds,
        bool SourceSeedDurableAcknowledged,
        int SendCount,
        int DurableAcknowledgementCount,
        int DuplicateOutcomes,
        int DeferredResponses,
        ulong FinalDurableFrontier,
        ulong FinalAppliedFrontier,
        double? SendP50Microseconds,
        double? SendP95Microseconds,
        double? SendP99Microseconds,
        double? DurableAckP50Microseconds,
        double? DurableAckP95Microseconds,
        double? DurableAckP99Microseconds,
        double? AppliedFrontierP50Microseconds,
        double? AppliedFrontierP95Microseconds,
        double? AppliedFrontierP99Microseconds,
        double? RepairLagMilliseconds,
        int AppliedPublicationCount);

    private sealed record MagicOnionReplicationReceipt(
        string Status,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        MagicOnionReplicationOptions Configuration,
        IReadOnlyList<MagicOnionReplicationSample> Samples,
        string RawSamplesPath,
        string Command,
        string ReceiptDirectory,
        EnvironmentIdentity Environment,
        string? Error);

    private sealed record EnvironmentIdentity(
        DateTimeOffset StartedAtUtc,
        string Runtime,
        string FrameworkDescription,
        string BuildConfiguration,
        string OperatingSystem,
        string Architecture,
        string CpuModel,
        int ProcessorCount,
        long AvailableMemoryBytes,
        bool ServerGc,
        string GcLatencyMode,
        string? GitCommit,
        bool? GitDirty,
        string? ArtifactPath,
        string? ArtifactSha256,
        string? TerraBaseArtifactPath,
        string? TerraBaseArtifactSha256);

    private sealed class MagicOnionPeerPair : IAsyncDisposable
    {
        private readonly WebApplication source;
        private readonly WebApplication destination;
        private readonly string storageRoot;
        private readonly X509Certificate2 ca;
        private readonly X509Certificate2 sourceServer;
        private readonly X509Certificate2 destinationServer;
        private readonly X509Certificate2 sourceClient;
        private readonly X509Certificate2 destinationClient;

        private MagicOnionPeerPair(
            WebApplication source,
            WebApplication destination,
            string storageRoot,
            X509Certificate2 ca,
            X509Certificate2 sourceServer,
            X509Certificate2 destinationServer,
            X509Certificate2 sourceClient,
            X509Certificate2 destinationClient,
            Uri sourceEndpoint,
            Uri destinationEndpoint,
            EntityId database,
            EntityId tenant)
        {
            this.source = source;
            this.destination = destination;
            this.storageRoot = storageRoot;
            this.ca = ca;
            this.sourceServer = sourceServer;
            this.destinationServer = destinationServer;
            this.sourceClient = sourceClient;
            this.destinationClient = destinationClient;
            SourceEndpoint = sourceEndpoint;
            DestinationEndpoint = destinationEndpoint;
            Database = database;
            Tenant = tenant;
        }

        public Uri SourceEndpoint { get; }
        public Uri DestinationEndpoint { get; }
        public EntityId Database { get; }
        public EntityId Tenant { get; }
        public TerraBaseEngine SourceEngine => source.Services.GetRequiredService<TerraBaseEngine>();
        public TerraBaseEngine DestinationEngine => destination.Services.GetRequiredService<TerraBaseEngine>();

        public static async Task<MagicOnionPeerPair> StartAsync(EntityId database, EntityId tenant)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "terrabase-magic-onion-benchmark",
                Guid.NewGuid().ToString("N"));
            var sourcePath = Path.Combine(root, "source");
            var destinationPath = Path.Combine(root, "destination");
            var ca = CreateCertificate("terrabase-benchmark-ca", CertificateKind.CertificateAuthority);
            var sourceServer = CreateCertificate("terrabase-benchmark-source", CertificateKind.Server, ca);
            var destinationServer = CreateCertificate("terrabase-benchmark-destination", CertificateKind.Server, ca);
            var sourceClient = CreateCertificate("terrabase-benchmark-source-client", CertificateKind.Client, ca);
            var destinationClient = CreateCertificate("terrabase-benchmark-destination-client", CertificateKind.Client, ca);
            WebApplication? source = null;
            WebApplication? destination = null;
            try
            {
                source = TerraBaseHost.Create(
                    "https://127.0.0.1:0",
                    builder => ConfigureBuilder(builder, sourcePath, database, tenant),
                    CreatePeerOptions(
                        sourceServer,
                        ca,
                        "member-a",
                        destinationClient,
                        "member-b",
                        database,
                        tenant));
                destination = TerraBaseHost.Create(
                    "https://127.0.0.1:0",
                    builder => ConfigureBuilder(builder, destinationPath, database, tenant),
                    CreatePeerOptions(
                        destinationServer,
                        ca,
                        "member-b",
                        sourceClient,
                        "member-a",
                        database,
                        tenant));
                await source.StartAsync().ConfigureAwait(false);
                await destination.StartAsync().ConfigureAwait(false);
                return new MagicOnionPeerPair(
                    source,
                    destination,
                    root,
                    ca,
                    sourceServer,
                    destinationServer,
                    sourceClient,
                    destinationClient,
                    GetEndpoint(source),
                    GetEndpoint(destination),
                    database,
                    tenant);
            }
            catch
            {
                if (destination is not null)
                    await destination.DisposeAsync().ConfigureAwait(false);
                if (source is not null)
                    await source.DisposeAsync().ConfigureAwait(false);
                DisposeCertificates(ca, sourceServer, destinationServer, sourceClient, destinationClient);
                DeleteStorage(root);
                throw;
            }
        }

        public MagicOnionInterNodeClient CreateSourceClient() =>
            CreateClient(SourceEndpoint, destinationClient, destinationServer, "member-b");

        public MagicOnionInterNodeClient CreateDestinationClient() =>
            CreateClient(DestinationEndpoint, sourceClient, sourceServer, "member-a");

        public async ValueTask DisposeAsync()
        {
            await destination.StopAsync().ConfigureAwait(false);
            await source.StopAsync().ConfigureAwait(false);
            await destination.DisposeAsync().ConfigureAwait(false);
            await source.DisposeAsync().ConfigureAwait(false);
            DisposeCertificates(ca, sourceServer, destinationServer, sourceClient, destinationClient);
            DeleteStorage(storageRoot);
        }

        private MagicOnionInterNodeClient CreateClient(
            Uri endpoint,
            X509Certificate2 clientCertificate,
            X509Certificate2 serverCertificate,
            string sourceNodeId) =>
            new(new MagicOnionInterNodeClientOptions(
                new AuthenticatedTlsOptions(endpoint, clientCertificate, requireClientCertificate: true)
                {
                    CheckCertificateRevocationList = false,
                },
                sourceNodeId)
            {
                TrustedServerCertificates = [ca],
                ExpectedRemoteMemberId = sourceNodeId == "member-b" ? "member-a" : "member-b",
                ExpectedRemoteCertificateFingerprints = [Fingerprint(serverCertificate)],
                DefaultDatabaseUlid = Database,
            });

        private static PeerServerOptions CreatePeerOptions(
            X509Certificate2 server,
            X509Certificate2 authority,
            string localNode,
            X509Certificate2 peerClient,
            string peerNode,
            EntityId database,
            EntityId tenant) =>
            new(
                server,
                authority,
                localNode,
                new Dictionary<string, MagicOnionPeerPrincipal>(StringComparer.Ordinal)
                {
                    [Fingerprint(peerClient)] = new MagicOnionPeerPrincipal
                    {
                        MemberId = peerNode,
                        MembershipEpoch = 7,
                        AllowedDatabases = new HashSet<EntityId> { database },
                        AllowedTenants = new HashSet<EntityId> { tenant },
                    },
                })
            {
                ReplicaEpoch = 9,
                MaximumBatchRecords = 256,
                MaximumBatchBytes = 8 * 1024 * 1024,
            };

        private static void ConfigureBuilder(
            WebApplicationBuilder builder,
            string path,
            EntityId database,
            EntityId tenant)
        {
            builder.Configuration["TERRABASE_REGION"] = "GRU";
            builder.Configuration["TERRABASE_DATABASE_PATH"] = path;
            builder.Configuration["TERRABASE_DATABASE_ROUTE"] = database.ToString();
            builder.Configuration["TERRABASE_DATABASE_TENANT"] = tenant.ToString();
            builder.Configuration["TERRABASE_DATABASE_ULID"] = database.ToString();
            builder.Configuration["TERRABASE_DATABASE_BACKEND"] = "storage-engine";
            builder.Configuration["TERRABASE_DATABASE_MODE"] = "explicit";
            builder.Configuration["TERRABASE_DATABASE_HOME_REGION"] = "GRU";
            builder.Configuration["TERRABASE_DATABASE_SELECTED_REGIONS"] = "GRU";
            builder.Configuration["TERRABASE_INTERNAL_DATABASE_ID"] = database.ToString();
            builder.Configuration["TERRABASE_ALLOW_SCHEMALESS"] = "true";
        }

        private static Uri GetEndpoint(WebApplication app) =>
            new(app.Services.GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?.Addresses
                .Single()
                ?? throw new InvalidOperationException("MagicOnion did not publish a listener address."));

        private static void DeleteStorage(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }

        private static void DisposeCertificates(params X509Certificate2[] certificates)
        {
            foreach (var certificate in certificates)
                certificate.Dispose();
        }

        private static X509Certificate2 CreateCertificate(
            string name,
            CertificateKind kind,
            X509Certificate2? issuer = null)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                new X500DistinguishedName($"CN={name}"),
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
                kind == CertificateKind.CertificateAuthority,
                kind == CertificateKind.CertificateAuthority,
                0,
                true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                kind == CertificateKind.CertificateAuthority
                    ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign
                    : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
            var usages = new OidCollection();
            if (kind == CertificateKind.Server)
                usages.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            else if (kind == CertificateKind.Client)
                usages.Add(new Oid("1.3.6.1.5.5.7.3.2"));
            if (usages.Count > 0)
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
            if (kind == CertificateKind.Server)
            {
                var names = new SubjectAlternativeNameBuilder();
                names.AddDnsName("localhost");
                names.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
            }

            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = issuer?.NotAfter.AddMinutes(-1) ?? DateTimeOffset.UtcNow.AddHours(1);
            if (issuer is null)
                return request.CreateSelfSigned(notBefore, notAfter);

            using var issued = request.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
            return issued.CopyWithPrivateKey(key);
        }

        private static string Fingerprint(X509Certificate2 certificate) =>
            certificate.GetCertHashString(HashAlgorithmName.SHA256)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToUpperInvariant();

        private enum CertificateKind
        {
            CertificateAuthority,
            Server,
            Client,
        }
    }
}
