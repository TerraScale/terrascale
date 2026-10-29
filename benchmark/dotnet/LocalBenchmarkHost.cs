using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Testing;

namespace TerraBase.E2E
{
    [Category("benchmark-host")]
    public sealed class LocalBenchmarkHost
    {
        [Test]
        [Scenario(Nodes.InProcess)]
        [Timeout(6 * 3600 * 1000)]
        public async Task Hosts_one_region_until_the_benchmark_deletes_its_targets_file(CancellationToken cancellationToken)
        {
            var file = Environment.GetEnvironmentVariable("TERRABASE_BENCH_TARGETS");
            Skip.When(string.IsNullOrEmpty(file), "local benchmark host: set TERRABASE_BENCH_TARGETS");
            var bucket = Environment.GetEnvironmentVariable("BUCKET_NAME");
            var localBucket = string.IsNullOrEmpty(bucket) ? new FakeS3() : null;
            await using var api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = Cluster.Gru;
                if (localBucket is not null)
                {
                    localBucket.Configure(settings);
                    return;
                }

                settings["backup-endpoint"] = Required("AWS_ENDPOINT_URL_S3");
                settings["backup-region"] = Required("AWS_REGION");
                settings["backup-bucket"] = bucket!.StartsWith("ts-", StringComparison.Ordinal)
                    ? bucket! : throw new InvalidOperationException("BUCKET_NAME must name a private ts-* test bucket");
                settings["backup-access-key-id"] = Required("AWS_ACCESS_KEY_ID");
                settings["backup-secret-key"] = Required("AWS_SECRET_ACCESS_KEY");
                settings["master-key"] = Required("TERRABASE_MASTER_KEY");
                if (new Uri(settings["backup-endpoint"]).Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase))
                {
                    settings["cloudflare-api-token"] = Required("CLOUDFLARE_API_TOKEN");
                }
            }, services => localBucket?.Route(services));
            var database = await api.CreateManagedDatabaseAsync("bench");
            await api.SetPlanAsync(api.User, database.Organization, "enterprise", cancellationToken);
            var token = api.Issuer.Mint(api.User, descriptor =>
            {
                descriptor.Claims["project_id"] = database.Project;
                descriptor.Claims["scope"] = "databases:read databases:write documents:read documents:write";
                descriptor.Expires = DateTime.UtcNow.AddHours(6);
            });
            var targets = new JsonObject
            {
                ["kind"] = "local",
                ["project"] = database.Project,
                ["database"] = database.Name,
                ["regions"] = new JsonObject
                {
                    [Cluster.Gru] = new JsonObject
                    {
                        ["url"] = api.Server.ClientAddress!.ToString(),
                        ["token"] = token,
                        ["metrics"] = api.Server.MetricsAddress.ToString(),
                    },
                },
                ["pid"] = Environment.ProcessId,
                ["data_dirs"] = new JsonObject { [Cluster.Gru] = api.Server.DataDirectory },
            };
            var partial = file + ".partial";
            await File.WriteAllTextAsync(partial, targets.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            File.Move(partial, file!, overwrite: true);
            while (File.Exists(file))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        private static string Required(string name)
        {
            return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value : throw new InvalidOperationException($"the bucket variable {name} is missing");
        }
    }
}
