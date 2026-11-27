using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using TerraBase.Storage;

namespace TerraBase.Benchmarks;

internal static class Program
{
    private const int CurrentSchemaVersion = 2;

    private static readonly HashSet<string> ValidSuites = new(StringComparer.OrdinalIgnoreCase)
    {
        "all",
        "storage",
        "crdt",
        "commitlog",
        "cluster",
        "quic",
        "replication",
        "continuity",
        "postgres",
        "server",
        "resources",
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--bdn", StringComparer.OrdinalIgnoreCase))
        {
            RunBenchmarkDotNet(args);
            return 0;
        }

        var suites = new List<string>();
        var filters = new List<string>();
        var comparePaths = new List<string>();
        var compareOnlyPaths = new List<string>();
        var rounds = 3;
        var scale = 1.0;
        string? outPath = null;
        string? compareMdPath = null;
        var label = string.Empty;
        var quick = false;
        var roundsExplicit = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--suite":
                    if (index + 1 >= args.Length)
                    {
                        return UsageError("--suite requires a value");
                    }

                    foreach (var part in args[++index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!ValidSuites.Contains(part))
                        {
                            return UsageError(string.Concat("unknown suite: ", part));
                        }

                        suites.Add(part);
                    }

                    break;
                case "--filter":
                    if (index + 1 >= args.Length)
                    {
                        return UsageError("--filter requires a comma-separated list");
                    }

                    foreach (var part in args[++index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        filters.Add(part);
                    }

                    break;
                case "--rounds":
                    if (index + 1 >= args.Length
                        || !int.TryParse(args[++index], NumberStyles.Integer, CultureInfo.InvariantCulture, out rounds)
                        || rounds <= 0)
                    {
                        return UsageError("--rounds requires a positive integer");
                    }

                    roundsExplicit = true;
                    break;
                case "--scale":
                    if (index + 1 >= args.Length
                        || !double.TryParse(args[++index], NumberStyles.Float, CultureInfo.InvariantCulture, out scale)
                        || !double.IsFinite(scale)
                        || scale <= 0)
                    {
                        return UsageError("--scale requires a positive number");
                    }

                    break;
                case "--out":
                case "--json":
                    if (index + 1 >= args.Length)
                    {
                        return UsageError(string.Concat(arg, " requires a file path"));
                    }

                    outPath = args[++index];
                    break;
                case "--label":
                    if (index + 1 >= args.Length)
                    {
                        return UsageError("--label requires a value");
                    }

                    label = args[++index];
                    break;
                case "--compare":
                    if (index + 1 >= args.Length)
                    {
                        return UsageError("--compare requires a baseline JSON path");
                    }

                    if (comparePaths.Count >= 2)
                    {
                        return UsageError("--compare may be specified at most twice");
                    }

                    comparePaths.Add(args[++index]);
                    break;
                case "--compare-only":
                    while (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        compareOnlyPaths.Add(args[++index]);
                    }

                    if (compareOnlyPaths.Count is < 2 or > 3)
                    {
                        return UsageError("--compare-only requires 2 or 3 JSON report paths");
                    }

                    break;
                case "--compare-md":
                    if (index + 1 >= args.Length)
                    {
                        return UsageError("--compare-md requires a file path");
                    }

                    compareMdPath = args[++index];
                    break;
                case "--quick":
                    quick = true;
                    break;
                default:
                    return UsageError(string.Concat("unknown argument: ", arg));
            }
        }

        if (compareOnlyPaths.Count > 0)
        {
            return await RunCompareOnlyAsync(compareOnlyPaths, compareMdPath);
        }

        if (!string.IsNullOrWhiteSpace(compareMdPath) && comparePaths.Count == 0)
        {
            return UsageError("--compare-md requires --compare or --compare-only");
        }

        if (quick)
        {
            if (!roundsExplicit)
            {
                rounds = 1;
            }

            scale *= 0.25;
        }

        if (suites.Count == 0)
        {
            suites.Add("all");
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        using var ctx = new BenchRunnerContext(suites, rounds, scale, filters, cts.Token);
        var cancellationToken = cts.Token;

        var cpuModel = ResourceSampler.ReadCpuModelName();
        var gitSha = ResourceSampler.ReadGitHeadSha();
        var gcServerMode = GCSettings.IsServerGC;
        var wallStart = Stopwatch.GetTimestamp();

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"TerraBase.Benchmarks | suites={string.Join(',', suites)} | rounds={rounds} | scale={scale:F2} | sqlite={SqliteStorageEngine.NativeVersion} | dotnet={Environment.Version} | cpus={Environment.ProcessorCount} | gc={(gcServerMode ? "Server" : "Workstation")} | sha={(gitSha.Length > 12 ? gitSha[..12] : gitSha)}"));

        if (ctx.ShouldRun("storage") || ctx.ShouldRun("resources"))
        {
            await StorageBenchmarks.RunAsync(ctx, cancellationToken);
        }

        if (ctx.ShouldRun("crdt") || ctx.ShouldRun("commitlog"))
        {
            await CrdtCommitLogBenchmarks.RunAsync(ctx, cancellationToken);
        }

        if (ctx.ShouldRun("cluster") || ctx.ShouldRun("quic"))
        {
            await ClusterQuicBenchmarks.RunAsync(ctx, cancellationToken);
        }

        if (ctx.ShouldRun("replication"))
        {
            await ReplicationBenchmarks.RunAsync(ctx, cancellationToken);
        }

        if (ctx.ShouldRun("continuity"))
        {
            await ContinuityBenchmarks.RunAsync(ctx, cancellationToken);
        }

        if (ctx.ShouldRun("postgres"))
        {
            await PostgresBenchmarks.RunAsync(ctx, cancellationToken);
        }

        if (ctx.ShouldRun("server"))
        {
            await ServerLayerBenchmarks.RunAsync(ctx, cancellationToken);
        }

        var totalWallSeconds = Stopwatch.GetElapsedTime(wallStart).TotalSeconds;
        BenchRunnerContext.PrintSummaryTable(ctx.Results);

        var report = new BenchReport(
            TimeProvider.System.GetUtcNow(),
            Environment.MachineName,
            Environment.Version.ToString(),
            RuntimeInformation.OSDescription,
            Environment.ProcessorCount,
            SqliteStorageEngine.NativeVersion,
            ctx.Rounds,
            ctx.IterationsScale,
            ctx.Results,
            CurrentSchemaVersion,
            gitSha,
            label,
            cpuModel,
            gcServerMode,
            totalWallSeconds);

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            var fullPath = Path.GetFullPath(outPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(report, BenchJsonContext.Default.BenchReport);
            await File.WriteAllTextAsync(fullPath, json, cancellationToken);
            Console.WriteLine(string.Concat("Wrote report to ", fullPath));
        }

        if (comparePaths.Count > 0)
        {
            List<(string Path, BenchReport Report)> inputs = [];
            foreach (var baselinePath in comparePaths)
            {
                var (baseline, error) = UnpackReportOutcome(BenchCompare.LoadReport(baselinePath));
                if (baseline is null)
                {
                    Console.Error.WriteLine(string.Concat("TerraBase.Benchmarks: failed to load comparison report ", error));
                    return 2;
                }

                inputs.Add((baselinePath, baseline));
            }

            inputs.Add((outPath is not null ? outPath : "current", report));
            var comparison = BenchCompare.Compare(inputs);
            var markdown = BenchCompare.RenderMarkdown(comparison);
            Console.WriteLine();
            Console.WriteLine(markdown);
            if (!string.IsNullOrWhiteSpace(compareMdPath))
            {
                await WriteTextFileAsync(compareMdPath, markdown, cancellationToken);
            }
        }

        return 0;
    }

    private static async Task<int> RunCompareOnlyAsync(IReadOnlyList<string> paths, string? compareMdPath)
    {
        List<(string Path, BenchReport Report)> inputs = [];
        foreach (var path in paths)
        {
            var (report, error) = UnpackReportOutcome(BenchCompare.LoadReport(path));
            if (report is null)
            {
                Console.Error.WriteLine(string.Concat("TerraBase.Benchmarks: failed to load report ", error));
                return 2;
            }

            inputs.Add((path, report));
        }

        var comparison = BenchCompare.Compare(inputs);
        var markdown = BenchCompare.RenderMarkdown(comparison);
        Console.WriteLine(markdown);
        if (!string.IsNullOrWhiteSpace(compareMdPath))
        {
            await WriteTextFileAsync(compareMdPath, markdown, CancellationToken.None);
        }

        return 0;
    }

    private static (BenchReport? Report, string? Error) UnpackReportOutcome(LoadReportOutcome outcome) => outcome switch
    {
        BenchReport ok => (ok, null),
        ReportLoadFailure failure => (null, string.Concat(failure.Path, ": ", failure.Reason)),
    };

    private static async Task WriteTextFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        Console.WriteLine(string.Concat("Wrote comparison markdown to ", fullPath));
    }

    private static void RunBenchmarkDotNet(string[] args)
    {
        var quick = args.Contains("--quick", StringComparer.OrdinalIgnoreCase);
        var job = (quick ? Job.ShortRun : Job.Default).WithToolchain(InProcessEmitToolchain.Default);
        var config = DefaultConfig.Instance
            .AddJob(job)
            .WithArtifactsPath(Path.Combine(AppContext.BaseDirectory, "../../../../results"));
        string[] forwarded = [.. args.Where(static arg => !string.Equals(arg, "--bdn", StringComparison.OrdinalIgnoreCase) && !string.Equals(arg, "--quick", StringComparison.OrdinalIgnoreCase))];
        _ = BenchmarkSwitcher.FromAssembly(typeof(MicroBenchmarks).Assembly).Run(forwarded, config);
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(string.Concat("TerraBase.Benchmarks: ", message));
        Console.Error.WriteLine("Usage: TerraBase.Benchmarks [--suite all|storage|crdt|commitlog|cluster|quic|replication|continuity|postgres|server|resources] [--filter name1,name2] [--rounds N] [--scale S] [--quick] [--out|--json path.json] [--label name] [--compare base.json] [--compare-only a.json b.json [c.json]] [--compare-md out.md] | --bdn [--filter ...]");
        return 2;
    }
}
