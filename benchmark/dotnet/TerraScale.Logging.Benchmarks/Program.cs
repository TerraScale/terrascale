using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using TerraScale.Logging.Benchmarks;

var job = Job.Default.WithToolchain(InProcessEmitToolchain.Default);
var config = DefaultConfig.Instance
    .AddJob(job)
    .WithArtifactsPath(Path.Combine(AppContext.BaseDirectory, "../../../../results"));

BenchmarkSwitcher.FromAssembly(typeof(LoggingBenchmarks).Assembly).Run(args, config);
