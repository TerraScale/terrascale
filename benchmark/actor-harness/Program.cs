using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Actors;
using TerraBase.Tenancy;

// Transport-only diagnostic: no SQLite, persistence, protocol, HTTP or core affinity claim.
var options = args.Select(argument => argument.Split('=', 2)).ToDictionary(
    pair => pair.Length == 2 && pair[0].StartsWith("--", StringComparison.Ordinal) ? pair[0][2..] : throw new ArgumentException("use --name=value"),
    pair => pair[1], StringComparer.Ordinal);
if (options.Keys.Except(["operations", "iterations", "repeats", "keys", "actors", "output", "trial-timeout-seconds"], StringComparer.Ordinal).Any())
{
    throw new ArgumentException("unknown harness option");
}

var operations = Number("operations", 20_000, 32, 1_000_000);
var iterations = Number("iterations", 2_000, 0, 100_000);
var repeats = Number("repeats", 3, 1, 10);
var trialTimeout = TimeSpan.FromSeconds(Number("trial-timeout-seconds", 60, 1, 3_600));
var keyCount = Number("keys", 8_192, 1, 65_536);
var actorCounts = options.TryGetValue("actors", out var requested)
    ? requested.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray()
    : new[] { 1, 2, 4, 8 }.Where(count => count <= Environment.ProcessorCount).ToArray();
if (actorCounts.Length is < 1 or > 8 || actorCounts.Distinct().Count() != actorCounts.Length
    || actorCounts.Any(count => count < 1 || count > Math.Min(Environment.ProcessorCount, 32)))
{
    throw new ArgumentException("actors must be distinct counts within available processors, at most 32");
}

var database = new TenantContext("00000000-0000-0000-0000-000000000001", PlanTier.Hobby,
    "00000000-0000-0000-0000-000000000002", "00000000-0000-0000-0000-000000000003");
var keys = Enumerable.Range(0, keyCount).Select(index => new ActorKey(database, "items", index.ToString(CultureInfo.InvariantCulture))).ToArray();
var report = new StringBuilder("# TS-648 actor transport diagnostic\n\n");
// Open before running: reject a reused path before doing work, and preserve evidence after each completed trial.
await using var output = options.TryGetValue("output", out var outputPath)
    ? new StreamWriter(new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) : null;
var published = 0;
report.AppendLine("This measures the preparatory logical-actor transport, not database throughput or durability. No core affinity is applied.");
report.AppendLine($"\nUTC: {DateTimeOffset.UtcNow:O}; available processors: {Environment.ProcessorCount}; runtime: {Environment.Version}.");
report.AppendLine($"TerraBase revision: {Revision("ACTOR_SOURCE_REVISION")} (include working diff when dirty).");
report.AppendLine($"Harness revision: {Revision("ACTOR_HARNESS_REVISION")} (include working diff and untracked sources when dirty).");
report.AppendLine($"Operations/trial: {operations}; CPU iterations/operation: {iterations}; keys: {keyCount}; producers: 32; payload admission: 64 bytes.");
report.AppendLine($"Trial deadline including warmup: {trialTimeout.TotalSeconds} seconds; disposal deadline: 5 seconds.");
report.AppendLine("\n| Actors | Zipf exponent | Repeat | ops/s | p99 ms | CPU seconds | Peak RSS MiB | Callback count |\n| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
await PublishAsync();

foreach (var exponent in new[] { 0.0, 0.99, 1.2 })
{
    var cumulative = new double[keyCount];
    var total = 0.0;
    for (var index = 0; index < keyCount; index++)
    {
        total += 1.0 / Math.Pow(index + 1, exponent);
        cumulative[index] = total;
    }

    for (var index = 0; index < keyCount; index++)
    {
        cumulative[index] /= total;
    }

    cumulative[^1] = 1;
    foreach (var actorCount in actorCounts)
    {
        for (var repeat = 1; repeat <= repeats; repeat++)
        {
            try
            {
                var deadline = new CancellationTokenSource(trialTimeout);
                var deadlineToken = deadline.Token;
                var trial = Task.Run(async () =>
                {
                    var cleanupOwnsSources = false;
                    CancellationTokenSource? execution = null;
                    try
                    {
                        execution = CancellationTokenSource.CreateLinkedTokenSource(deadlineToken);
                        var executionToken = execution.Token;
                        var services = new ServiceCollection().AddTerraBaseActors(actorCount, 64, 64 * 1024);
                        string row;
                        Exception? trialFailure = null;
                        var provider = services.BuildServiceProvider();
                        try
                        {
                            var router = provider.GetRequiredService<IActorRouter>();
                            executionToken.ThrowIfCancellationRequested();
                            var state = Enumerable.Range(0, actorCount).Select(_ => new ActorState(keyCount)).ToArray();
                            await RunAsync(Math.Min(operations, 1_000), measure: false);
                            var before = state.Sum(actor => actor.Calls);
                            using var process = Process.GetCurrentProcess();
                            var cpu = process.TotalProcessorTime;
                            var watch = Stopwatch.StartNew();
                            var latency = await RunAsync(operations, measure: true);
                            watch.Stop();
                            process.Refresh();
                            var cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds;
                            var calls = state.Sum(actor => actor.Calls) - before;
                            if (calls != operations)
                            {
                                throw new InvalidOperationException("actor serialization lost a callback");
                            }

                            Array.Sort(latency);
                            var p99 = latency[(int)Math.Ceiling(latency.Length * 0.99) - 1] * 1_000.0 / Stopwatch.Frequency;
                            executionToken.ThrowIfCancellationRequested();
                            row = FormattableString.Invariant($"| {actorCount} | {exponent} | {repeat} | {operations / watch.Elapsed.TotalSeconds:F1} | {p99:F3} | {cpuSeconds:F3} | {process.PeakWorkingSet64 / 1048576.0:F1} | {calls} |");

                            async Task<long[]> RunAsync(int count, bool measure)
                            {
                                var samples = measure ? new long[count] : [];
                                await Task.WhenAll(Enumerable.Range(0, 32).Select(producer => Task.Run(async () =>
                                {
                                    // Identical ranked-key sequences for every actor count and repeat. Warmup has its own seed.
                                    var random = new Random(648 + producer + (measure ? 0 : 32));
                                    for (var ordinal = producer; ordinal < count; ordinal += 32)
                                    {
                                        executionToken.ThrowIfCancellationRequested();
                                        var sample = random.NextDouble();
                                        var key = Array.BinarySearch(cumulative, sample);
                                        if (key < 0)
                                        {
                                            key = ~key;
                                        }

                                        var started = Stopwatch.GetTimestamp();
                                        var actor = router.GetActor(keys[key]);
                                        await router.InvokeAsync(actor, 64, _ =>
                                        {
                                            var owned = state[actor];
                                            var value = owned.Values[key];
                                            for (var iteration = 0; iteration < iterations; iteration++)
                                            {
                                                value = unchecked(value * 6364136223846793005UL + 1442695040888963407UL);
                                            }

                                            owned.Values[key] = value;
                                            owned.Calls++;
                                            return ValueTask.FromResult(value);
                                        }, executionToken);
                                        if (measure)
                                        {
                                            samples[ordinal] = Stopwatch.GetTimestamp() - started;
                                        }
                                    }
                                }))).WaitAsync(executionToken);
                                return samples;
                            }
                        }
                        catch (Exception error)
                        {
                            trialFailure = error;
                            throw;
                        }
                        finally
                        {
                            // A stuck callback must not trap the diagnostic while the router drains on disposal.
                            try
                            {
                                var cleanup = Task.Run(async () =>
                                {
                                    try
                                    {
                                        try
                                        {
                                            await execution.CancelAsync();
                                        }
                                        finally
                                        {
                                            await provider.DisposeAsync();
                                        }
                                    }
                                    finally
                                    {
                                        execution.Dispose();
                                        deadline.Dispose();
                                    }
                                });
                                // Cleanup retains its sources if disposal outlives the bounded wait.
                                cleanupOwnsSources = true;
                                await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
                            }
                            catch (Exception cleanupFailure) when (trialFailure is not null)
                            {
                                throw new AggregateException("actor trial and cleanup failed", trialFailure, cleanupFailure);
                            }
                        }
                        return row;
                    }
                    finally
                    {
                        if (!cleanupOwnsSources)
                        {
                            execution?.Dispose();
                            deadline.Dispose();
                        }
                    }
                });
                string row;
                try
                {
                    row = await trial.WaitAsync(deadlineToken);
                }
                catch (OperationCanceledException cancellation) when (deadlineToken.IsCancellationRequested)
                {
                    Console.Error.WriteLine("Actor trial deadline expired; waiting for cleanup.");
                    try
                    {
                        row = await trial.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (TimeoutException cleanupDeadline)
                    {
                        if (trial.IsCompleted)
                        {
                            row = await trial;
                        }
                        else
                        {
                            throw new AggregateException("actor trial and cleanup deadlines expired", cancellation, cleanupDeadline);
                        }
                    }
                }

                report.AppendLine(row);
                await PublishAsync();
            }
            catch (Exception error)
            {
                var failures = error is AggregateException aggregate
                    ? string.Join(", ", aggregate.Flatten().InnerExceptions.Select(failure => failure.GetType().Name))
                    : error.GetType().Name;
                report.AppendLine(FormattableString.Invariant($"| {actorCount} | {exponent} | {repeat} | FAILED ({failures}) | — | — | — | — |"));
                await PublishAsync();
                throw;
            }
        }
    }
}

report.AppendLine("\nCPU and process-lifetime peak RSS include the load generator; they are not separate server measurements. Repeats run in one process.");
report.AppendLine("A Zipf hot key remains serialized on its owner. Memory replication and the SQLite flusher are not measured.");
await PublishAsync();

async Task PublishAsync()
{
    var added = report.ToString(published, report.Length - published);
    Console.Write(added);
    if (output is not null)
    {
        await output.WriteAsync(added);
        await output.FlushAsync();
    }

    published = report.Length;
}

string Revision(string name)
{
    var revision = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(revision) ? "unrecorded" : revision;
}

int Number(string name, int fallback, int minimum, int maximum)
{
    var value = options.TryGetValue(name, out var text) ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;
    return value >= minimum && value <= maximum ? value : throw new ArgumentOutOfRangeException(name);
}

internal sealed class ActorState(int keys)
{
    internal ulong[] Values { get; } = new ulong[keys];
    internal long Calls { get; set; }
}
