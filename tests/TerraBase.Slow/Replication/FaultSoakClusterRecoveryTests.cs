using System.Net;
using System.Text.Json.Nodes;

namespace TerraBase.Slow.Replication;

[Category("replication-fault-soak")]
public sealed class FaultSoakClusterRecoveryTests
{
    [Test]
    [Timeout(360_000)]
    public async Task Seed_67_qualifies_serving_after_every_replica_restarts(CancellationToken token)
    {
        const int seed = 67;
        await using var cluster = await FaultSoakCluster.StartAsync(seed, token);
        var history = new List<string>();
        var observations = new string[cluster.Regions.Length];
        var id = FaultSoakChecks.Id(seed, 700);
        var expected = new JsonObject { ["seed"] = seed, ["value"] = "full-cluster-restart" };
        var canonical = FaultSoakChecks.Canonical(expected);
        try
        {
            using (var answer = await cluster["gru"].PutAsJsonAsync(cluster.Document(id), expected, token))
            {
                var receipt = await answer.Content.ReadAsStringAsync(token);
                Record($"put gru {id} HTTP {(int)answer.StatusCode}: {receipt}");
                await FaultSoakChecks.SuccessAsync(answer, token);
                FaultSoakChecks.Check(JsonNode.Parse(receipt)?["durability"]?.GetValue<string>() == "remote_durable",
                    "the write did not receive a remote_durable acknowledgement");
            }

            await FaultSoakChecks.UntilAsync(CopiesMatchAsync, "all three replicas serve the exact acknowledged document", token);
            Record("all three replicas serve the acknowledged document; SIGKILL all");
            await Task.WhenAll(cluster.Regions.Select(cluster.KillAsync));
            // Inspect every stopped disk before starting any process: peer repair cannot supply these observations.
            var stopped = await Task.WhenAll(cluster.Regions.Select(region => cluster.ReadStoppedDocumentAsync(region, id, token)));
            for (var index = 0; index < cluster.Regions.Length; index++)
            {
                Record($"stopped {cluster.Regions[index]}: {stopped[index]}");
                FaultSoakChecks.Check(FaultSoakChecks.Canonical(stopped[index]) == canonical,
                    $"{cluster.Regions[index]} does not retain the exact acknowledged document before restart");
            }

            // This qualifies recovery beyond the documented intact-peer contract: every copy exited uncleanly.
            // It checks serving recovery, not physical loss; the stopped copies above establish retained bytes.
            foreach (var region in cluster.Regions)
            {
                Record($"restart {region}");
                await cluster.RestartAsync(region, token);
            }

            await FaultSoakChecks.UntilAsync(CopiesMatchAsync, "all three replicas serve after full-cluster restart", token,
                TimeSpan.FromSeconds(180));
            Record("all three replicas serve the exact document after restart");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"full-cluster recovery qualification seed={seed}\n{string.Join('\n', history)}\n"
                + $"last observations:\n{string.Join('\n', observations)}\n{cluster.Diagnostics}", error);
        }

        void Record(string step)
        {
            history.Add(step);
            Console.WriteLine($"R8 full-cluster seed={seed}: {step}");
        }

        async Task<bool> CopiesMatchAsync()
        {
            var matches = await Task.WhenAll(cluster.Regions.Select(async (region, index) =>
            {
                using var answer = await cluster[region].GetAsync(cluster.Document(id), token);
                var body = await answer.Content.ReadAsStringAsync(token);
                observations[index] = $"{region} HTTP {(int)answer.StatusCode}: {body}";
                return answer.StatusCode == HttpStatusCode.OK
                    && FaultSoakChecks.Canonical(JsonNode.Parse(body)?["document"]) == canonical;
            }));
            return matches.All(match => match);
        }
    }
}
