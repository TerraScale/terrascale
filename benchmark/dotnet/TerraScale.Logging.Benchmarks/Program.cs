using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using TerraScale.Logging.Benchmarks;

// In-process: avoids a child build per benchmark. Do not pass --job: BenchmarkDotNet unions CLI jobs with the job above.
var job = Job.Default.WithToolchain(InProcessEmitToolchain.Default);
var config = DefaultConfig.Instance
    .AddJob(job)
    .WithArtifactsPath(Path.Combine(AppContext.BaseDirectory, "../../../../results"));

BenchmarkSwitcher.FromAssembly(typeof(LoggingBenchmarks).Assembly).Run(args, config);
