using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraBase.E2E;

/// <summary>
/// Hosts gru, sjc and fra (or the regions <c>TERRABASE_BENCH_REGIONS</c> names, for example <c>gru</c> while nothing
/// replicates between R2a-1 and R4a) as local nodes for the replication benchmark (benchmark/cmd/repl-bench, run by
/// <c>make benchmark-repl-local</c>). It writes the targets file named by <c>TERRABASE_BENCH_TARGETS</c> once every
/// region serves the database, and keeps the nodes up until the benchmark deletes that file. Without the variable it
/// skips: it is a host, not a scenario. <c>TERRABASE_BENCH_LINK_DELAYS</c> (for example <c>gru-sjc=85,gru-fra=94,sjc-fra=69</c>)
/// gives each named pair of regions a one-way delay in milliseconds on its peer links, as the real routes have.
/// </summary>
[Category("benchmark-host")]
public sealed class BenchmarkClusterHost
{
    [Test]
    [Scenario(Nodes.InProcess)]
    [Timeout(6 * 3600 * 1000)]
    public async Task Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file(CancellationToken cancellationToken)
    {
        var file = Environment.GetEnvironmentVariable("TERRABASE_BENCH_TARGETS");
        Skip.When(string.IsNullOrEmpty(file), "the replication benchmark's local cluster host: run it with make benchmark-repl-local");
        var chosen = Environment.GetEnvironmentVariable("TERRABASE_BENCH_REGIONS") is { Length: > 0 } list
            ? list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [Cluster.Gru, Cluster.Sjc, Cluster.Fra];
        // With bucket credentials (BUCKET_NAME, the AWS_* variables of a private test bucket and the bucket's
        // TERRABASE_MASTER_KEY, shared by every node and run as in production) the nodes back up and mirror shapes to that
        // bucket: shape-bench measures free-read tiers T1 and T2 against it.
        await using var cluster = Environment.GetEnvironmentVariable("BUCKET_NAME") is { Length: > 0 } bucket
            ? await Cluster.StartBucketAsync(chosen, settings =>
            {
                settings["backup-endpoint"] = Required("AWS_ENDPOINT_URL_S3");
                settings["backup-region"] = Required("AWS_REGION");
                settings["backup-bucket"] = bucket.StartsWith("ts-", StringComparison.Ordinal)
                    ? bucket : throw new InvalidOperationException("BUCKET_NAME must name a private ts-* test bucket, never a production one");
                settings["backup-access-key-id"] = Required("AWS_ACCESS_KEY_ID");
                settings["backup-secret-key"] = Required("AWS_SECRET_ACCESS_KEY");
                settings["master-key"] = Required("TERRABASE_MASTER_KEY");
                if (new Uri(settings["backup-endpoint"]).Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase))
                {
                    settings["cloudflare-api-token"] = Required("CLOUDFLARE_API_TOKEN");
                }
            })
            : await Cluster.StartLocalAsync(chosen);
        foreach (var pair in (Environment.GetEnvironmentVariable("TERRABASE_BENCH_LINK_DELAYS") ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var (regionPair, milliseconds) = (pair.Split('=')[0].Split('-'), double.Parse(pair.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture));
            cluster.Delay(regionPair[0], regionPair[1], TimeSpan.FromMilliseconds(milliseconds));
        }

        var regions = new JsonObject();
        var dataDirectories = new JsonObject();
        foreach (var region in cluster.Regions)
        {
            var (address, token, directory) = cluster.Endpoint(region);
            regions[region] = new JsonObject { ["url"] = address.ToString(), ["token"] = token, ["metrics"] = cluster.MetricsAddress(region).ToString() };
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

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"the bucket variable {name} is missing");
}
