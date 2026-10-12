using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraBase.E2E;

/// <summary>
/// Hosts gru, sjc and fra as local nodes for the replication benchmark (benchmark/cmd/repl-bench, run by
/// <c>make benchmark-repl-local</c>). It writes the targets file named by <c>TERRABASE_BENCH_CLUSTER_FILE</c> once every
/// region serves the database, and keeps the nodes up until the benchmark deletes that file. Without the variable it
/// skips: it is a host, not a scenario.
/// </summary>
[Category("benchmark-host")]
public sealed class BenchmarkClusterHost
{
    [Test]
    [Timeout(6 * 3600 * 1000)]
    public async Task Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file(CancellationToken cancellationToken)
    {
        var file = Environment.GetEnvironmentVariable("TERRABASE_BENCH_CLUSTER_FILE");
        Skip.When(string.IsNullOrEmpty(file), "the replication benchmark's local cluster host: run it with make benchmark-repl-local");
        await using var cluster = await Cluster.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        var regions = new JsonObject();
        var dataDirectories = new JsonObject();
        foreach (var region in cluster.Regions)
        {
            var (address, token, certificate, directory) = cluster.Endpoint(region);
            regions[region] = new JsonObject { ["url"] = address.ToString(), ["token"] = token, ["ca_pem"] = certificate };
            dataDirectories[region] = directory;
        }

        var segments = cluster.DatabasePath.Split('/');
        var targets = new JsonObject
        {
            ["kind"] = "local",
            ["project"] = segments[3],
            ["database"] = segments[5],
            ["regions"] = regions,
            ["pid"] = Environment.ProcessId,
            ["data_dirs"] = dataDirectories,
        };
        // Written aside and moved, so the benchmark never reads half a file. It holds the nodes' tokens: the benchmark
        // names a file in its own 0700 directory.
        var partial = file + ".partial";
        await File.WriteAllTextAsync(partial, targets.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        File.Move(partial, file!, overwrite: true);
        Console.WriteLine($"benchmark cluster ready: {file}");
        while (File.Exists(file))
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }
}
