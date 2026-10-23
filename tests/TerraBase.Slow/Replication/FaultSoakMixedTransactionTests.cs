using System.Net;
using System.Text.Json.Nodes;

namespace TerraBase.Slow.Replication;

[Category("replication-fault-soak")]
public sealed class FaultSoakMixedTransactionTests
{
    private static readonly string[] Writers = ["gru", "sjc"];

    [Test]
    [Timeout(600_000)]
    public async Task Mixed_transactions_rejoin_without_duplicate_increments_or_ghost_fields(CancellationToken token)
    {
        await using var cluster = await FaultSoakCluster.StartAsync(396, token);
        var plans = Writers.Select((region, index) => (
            Region: region, By: index == 0 ? 1 : 10,
            Replace: FaultSoakChecks.Id(396, 10 + (index * 4)),
            Patch: FaultSoakChecks.Id(396, 11 + (index * 4)),
            Deleted: FaultSoakChecks.Id(396, 12 + (index * 4)),
            Recreated: FaultSoakChecks.Id(396, 13 + (index * 4)))).ToArray();
        var counter = FaultSoakChecks.Id(396, 20);
        var expected = plans.SelectMany(plan => new[] { plan.Replace, plan.Patch, plan.Deleted, plan.Recreated })
            .ToDictionary(id => id, _ => (JsonNode)new JsonObject { ["old"] = "seed", ["origin"] = "seed" }, StringComparer.Ordinal);
        expected[counter] = new JsonObject { ["n"] = 0, ["tags"] = new JsonArray("old") };
        try
        {
            foreach (var (id, body) in expected)
            {
                await ConfirmAsync(() => cluster["gru"].PutAsJsonAsync(cluster.Document(id), body, token));
            }

            await CheckCopiesAsync(cluster.Regions, "seeds on all three disks");
            await cluster.KillAsync("fra");
            // Keep a remote durable witness: the removed one-region windows cannot acknowledge under decision 0013a.
            await Task.WhenAll(plans.Select(plan =>
            {
                var collection = cluster.Collection;
                return ConfirmAsync(() => cluster[plan.Region].PostAsJsonAsync(new Uri($"{cluster.DatabasePath}/transactions", UriKind.Relative),
                    new
                    {
                        ops = new object[]
                        {
                            new { op = "put", collection, id = plan.Replace, document = new { origin = plan.Region } },
                            new { op = "patch", collection, id = plan.Patch, merge = new { origin = plan.Region } },
                            new { op = "patch", collection, id = counter, ops = new object[]
                            {
                                new { op = "inc", path = "/n", by = plan.By },
                                new { op = "add", path = "/tags", value = plan.Region },
                                new { op = "remove", path = "/tags", value = "old" },
                            } },
                            new { op = "delete", collection, id = plan.Deleted },
                            new { op = "delete", collection, id = plan.Recreated },
                        },
                    }, token));
            }));

            foreach (var (region, _, replace, patch, deleted, recreated) in plans)
            {
                expected[replace] = new JsonObject { ["origin"] = region };
                expected[patch] = new JsonObject { ["old"] = "seed", ["origin"] = region };
                expected.Remove(deleted);
                expected.Remove(recreated);
                var other = plans.Single(peer => peer.Region != region).Region;
                await CheckTombstoneAsync(other, recreated);
                await ConfirmAsync(() => cluster[other].PutAsJsonAsync(cluster.Document(recreated), new { recreated = other }, token));
                expected[recreated] = new JsonObject { ["recreated"] = other };
            }

            expected[counter] = new JsonObject { ["n"] = 11, ["tags"] = new JsonArray("gru", "sjc") };
            await CheckCopiesAsync(Writers, "both mixed transactions on the durable survivors");
            // No write after restarting either node: catch-up must work without a client kick.
            await cluster.RestartAsync("fra", token);
            await CheckCopiesAsync(cluster.Regions, "mixed transactions after fra rejoins");
            await cluster.KillAsync("gru");
            await cluster.RestartAsync("gru", token);
            await CheckCopiesAsync(cluster.Regions, "mixed transactions after their sender restarts");
            foreach (var region in cluster.Regions)
            {
                foreach (var (_, _, _, _, deleted, _) in plans)
                {
                    await CheckTombstoneAsync(region, deleted);
                }
            }
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Q-RELAND rows 18/19; seed=396\n{cluster.Diagnostics}", error);
        }

        Task ConfirmAsync(Func<Task<HttpResponseMessage>> send)
        {
            return FaultSoakChecks.UntilAsync(async () =>
            {
                using var answer = await send();
                var body = JsonNode.Parse(await answer.Content.ReadAsStringAsync(token));
                // Membership refresh can reconnect healthy streams; only these refusals guarantee no execution.
                if (answer.StatusCode == HttpStatusCode.ServiceUnavailable
                    && body?["error"]?["code"]?.GetValue<string>() is "remote_region_unavailable" or "confirmation_busy")
                {
                    return false;
                }

                await FaultSoakChecks.SuccessAsync(answer, token);
                FaultSoakChecks.Check(body?["durability"]?.GetValue<string>() == "remote_durable", "mixed workload requires a remote durable acknowledgement");
                return true;
            }, "mixed workload receives a remote durable acknowledgement", token);
        }

        Task CheckCopiesAsync(string[] regions, string phase)
        {
            return FaultSoakChecks.UntilAsync(async () =>
            {
                var copies = await Task.WhenAll(regions.Select(region => FaultSoakChecks.ListAsync(cluster[region], cluster.DatabasePath, cluster.Collection, token)));
                return copies.All(copy => copy is not null && copy.Count == expected.Count
                    && expected.All(item => copy.TryGetValue(item.Key, out var body) && body == FaultSoakChecks.Canonical(item.Value)));
            }, phase, token, TimeSpan.FromSeconds(180));
        }

        Task CheckTombstoneAsync(string region, string id)
        {
            return FaultSoakChecks.UntilAsync(async () =>
            {
                using var answer = await cluster[region].GetAsync(cluster.Document(id), token);
                if (answer.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    await FaultSoakChecks.RefusedAsync(answer, token);
                    return false;
                }

                FaultSoakChecks.Check(answer.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
                    $"unexpected tombstone read HTTP {(int)answer.StatusCode}");
                return answer.StatusCode == HttpStatusCode.NotFound && answer.Headers.ETag is not null;
            }, $"retained tombstone {id} on {region}", token);
        }
    }
}
