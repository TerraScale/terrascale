using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using TerraScale.Endpoints.Benchmarks;

// `serve` hosts every scenario on Kestrel for an external load generator (see bench/README.md).
if (args is ["serve", ..])
{
    var app = BenchmarkApp.Build(args[1..], kestrel: true);
    app.Run();
    return;
}

// In-process: avoids a child build per benchmark. `--quick` gives rough numbers in seconds.
var quick = args.Contains("--quick");
var job = (quick ? Job.ShortRun : Job.Default).WithToolchain(InProcessEmitToolchain.Default);
var config = DefaultConfig.Instance
    .AddJob(job)
    .WithArtifactsPath(Path.Combine(AppContext.BaseDirectory, "../../../../results"));
args = [.. args.Where(arg => arg != "--quick")];

BenchmarkSwitcher.FromAssembly(typeof(HandlerBenchmarks).Assembly).Run(args, config);
