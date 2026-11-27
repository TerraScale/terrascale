using System.Globalization;
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
        var rounds = 3;
        var scale = 1.0;
        string? outPath = null;
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
                    if (index + 1 >= args.Length)
                    {
                        return UsageError("--out requires a file path");
                    }

                    outPath = args[++index];
                    break;
                case "--quick":
                    quick = true;
                    break;
                default:
                    return UsageError(string.Concat("unknown argument: ", arg));
            }
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

        using var ctx = new BenchRunnerContext(suites, rounds, scale, cts.Token);
        var cancellationToken = cts.Token;

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"TerraBase.Benchmarks | suites={string.Join(',', suites)} | rounds={rounds} | scale={scale:F2} | sqlite={SqliteStorageEngine.NativeVersion} | dotnet={Environment.Version} | cpus={Environment.ProcessorCount}"));

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

        BenchRunnerContext.PrintSummaryTable(ctx.Results);

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            var fullPath = Path.GetFullPath(outPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            var report = new BenchReport(
                TimeProvider.System.GetUtcNow(),
                Environment.MachineName,
                Environment.Version.ToString(),
                RuntimeInformation.OSDescription,
                Environment.ProcessorCount,
                SqliteStorageEngine.NativeVersion,
                ctx.Rounds,
                ctx.IterationsScale,
                ctx.Results);
            var json = JsonSerializer.Serialize(report, BenchJsonContext.Default.BenchReport);
            await File.WriteAllTextAsync(fullPath, json, cancellationToken);
            Console.WriteLine(string.Concat("Wrote report to ", fullPath));
        }

        return 0;
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
        Console.Error.WriteLine("Usage: TerraBase.Benchmarks [--suite all|storage|crdt|commitlog|cluster|quic|replication|continuity|postgres|server|resources] [--rounds N] [--scale S] [--quick] [--out path.json] | --bdn [--filter ...]");
        return 2;
    }
}
