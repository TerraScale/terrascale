using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Bogus;

namespace TerraBase.Slow.Replication;

[Category("replication-fault-soak")]
public sealed class RandomizedReplicationFaultTests
{
    private static readonly string[] All = ["gru", "sjc", "fra"];
    public static IEnumerable<int> Seeds() => Enumerable.Range(
        int.Parse(Environment.GetEnvironmentVariable("R8_SEED") ?? "0", CultureInfo.InvariantCulture),
        int.Parse(Environment.GetEnvironmentVariable("R8_COUNT") ?? "1", CultureInfo.InvariantCulture));

    [Test]
    [MethodDataSource(nameof(Seeds))]
    [Timeout(600_000)]
    public async Task Acknowledged_writes_converge_through_seeded_faults(int seed, CancellationToken cancellationToken)
    {
        var history = new ConcurrentQueue<string>();
        try
        {
            await RunAsync(seed, history, cancellationToken);
            Console.WriteLine($"R8 PASS seed={seed} faults=partition,delay,loss,SIGKILL,restart,slow-fsync,clock-skew");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"R8 FAIL seed={seed}; replay R8_SEED={seed} R8_COUNT=1\n{string.Join('\n', history)}", error);
        }
    }

    private static async Task RunAsync(int seed, ConcurrentQueue<string> history, CancellationToken token)
    {
        var random = new Random(seed);
        var faker = new Faker { Random = new Randomizer(seed) };
        var regions = All.OrderBy(_ => random.Next()).ToArray();
        var victim = regions[random.Next(3)];
        await using var cluster = await FaultSoakCluster.StartAsync(seed, token);
        history.Enqueue($"clock-skew {cluster.ClockSkews}");
        var expected = new ConcurrentDictionary<string, JsonNode>(StringComparer.Ordinal);
        var deleted = new ConcurrentBag<string>();
        var acknowledged = new ConcurrentBag<(string Id, string Op)>();
        var attempted = new ConcurrentDictionary<string, JsonNode>(StringComparer.Ordinal);
        var catalogs = new ConcurrentBag<string>();
        var feeds = new List<FaultSoakFeed>();
        var failures = new List<Exception>();
        try
        {
            foreach (var region in regions)
            {
                feeds.Add(await FaultSoakFeed.StartAsync(() => cluster[region], $"{cluster.DatabasePath}/changes?collections={cluster.Collection}", token));
            }

            await FaultSoakChecks.SetupUniqueAsync(cluster, token);

            // Allocate all inputs before concurrent work: thread scheduling never chooses the workload or fault targets.
            var plans = Enumerable.Range(0, 4).Select(round => regions.Select(region => (
                Region: region, Writes: Enumerable.Range(0, 3).Select(index => (
                    Id: FaultSoakChecks.Id(seed, (round * 9) + (Array.IndexOf(regions, region) * 3) + index),
                    Body: (JsonNode)new JsonObject { ["seed"] = seed, ["round"] = round, ["value"] = faker.Lorem.Word() })).ToArray())).ToArray()).ToArray();
            await BatchAsync(0);
            await FaultSoakChecks.UntilAsync(() => CopiesMatchAsync(), "baseline replicated", token);

            history.Enqueue($"partition {regions[0]} {regions[1]}; delay/loss {regions[1]} {regions[2]}");
            cluster.Partition(regions[0], regions[1]);
            await cluster.LossyAsync(regions[1], regions[2], TimeSpan.FromMilliseconds(5 + random.Next(20)), 1024 * 1024, 0.5);
            await BatchAsync(1);
            var winner = await FaultSoakChecks.ClaimAsync(cluster, seed, history, token);
            await FaultSoakChecks.UntilAsync(() => Task.FromResult(cluster.NetworkResets > 0), "loss injection actually reset a TCP connection", token);
            history.Enqueue($"loss resets={cluster.NetworkResets}");

            cluster.Heal();
            // Reconcile claims after healing too: a documented 503 may have executed nothing.
            winner = winner.Concat(await FaultSoakChecks.ClaimAsync(cluster, seed, history, token)).ToHashSet(StringComparer.Ordinal);
            history.Enqueue($"SIGKILL {victim}");
            var writingDuringKill = BatchAsync(2, victim);
            await cluster.KillAsync(victim);
            // A confirmed write must already have a readable remote copy while its origin is dead.
            foreach (var (id, body) in plans[0].Single(plan => plan.Region == victim).Writes.Where(write => expected.ContainsKey(write.Id)))
            {
                await FaultSoakChecks.UntilAsync(async () =>
                    (await Task.WhenAll(regions.Where(region => region != victim).Select(region => FaultSoakChecks.ReadAsync(cluster[region], cluster.Document(id), token))))
                    .Any(copy => FaultSoakChecks.Canonical(copy) == FaultSoakChecks.Canonical(body)), $"remote copy {id} with origin dead", token);
            }

            await writingDuringKill;
            history.Enqueue($"restart {victim}");
            await cluster.RestartAsync(victim, token);
            await FaultSoakChecks.UntilAsync(CopiesMatchAsync, "victim recovers before slow disk phase", token);
            cluster.SlowDisk(regions[1], 10 + random.Next(40));
            history.Enqueue($"slow-fsync {regions[1]}; clocks derived from seed={seed}");
            try
            {
                await BatchAsync(3);
                await DeleteAsync();
            }
            finally
            {
                cluster.HealDisk(regions[1]);
            }

            await FaultSoakChecks.UntilAsync(CopiesMatchAsync, "acknowledged writes, tombstones and identical collection digests", token);
            await FaultSoakChecks.VerifyUniqueAsync(cluster, seed, winner, token);
            FaultSoakChecks.Check(!deleted.IsEmpty && !catalogs.IsEmpty, "deletes and catalog changes must acknowledge at least one operation");
            await CatalogsMatchAsync();

            // Discard unsynced WAL bytes after every process dies, before any peer can repair another disk.
            history.Enqueue("SIGKILL all; drop-unsynced-WAL");
            await Task.WhenAll(regions.Select(cluster.KillAsync));
            foreach (var region in regions)
            {
                cluster.DropUnsynced(region);
            }

            foreach (var region in regions)
            {
                await cluster.RestartAsync(region, token);
            }

            history.Enqueue("restart all");
            await FaultSoakChecks.UntilAsync(CopiesMatchAsync, "all disks after full-cluster SIGKILL", token);
            await FaultSoakChecks.VerifyUniqueAsync(cluster, seed, winner, token);
            await CatalogsMatchAsync();
            var resumedId = FaultSoakChecks.Id(seed, 900);
            var resumedBody = new JsonObject { ["afterRestart"] = seed };
            attempted[resumedId] = resumedBody;
            await FaultSoakChecks.UntilAsync(async () =>
            {
                if (!await SendAsync(regions[0], resumedId, "put", () => cluster[regions[0]].PutAsJsonAsync(cluster.Document(resumedId), resumedBody, token)))
                {
                    return false;
                }

                expected[resumedId] = resumedBody;
                return true;
            }, "post-restart write acknowledged", token);
            await FaultSoakChecks.UntilAsync(CopiesMatchAsync, "post-restart write converges", token);
            foreach (var region in regions)
            {
                foreach (var id in deleted)
                {
                    using var tombstone = await cluster[region].GetAsync(cluster.Document(id), token);
                    FaultSoakChecks.Check(tombstone.StatusCode == HttpStatusCode.NotFound && tombstone.Headers.ETag is not null,
                        $"{region} lost retained tombstone {id}");
                }
            }

            await FaultSoakChecks.UntilAsync(() => Task.FromResult(feeds.All(feed => feed.ResumedConnections > 0 && acknowledged.All(write => feed.Has(write.Id, write.Op, write.Op == "put" && expected.TryGetValue(write.Id, out var document) ? document : null)))),
                "every acknowledged put/delete on every resumed change reader", token);

            async Task CatalogsMatchAsync()
            {
                foreach (var name in catalogs)
                {
                    await FaultSoakChecks.UntilAsync(async () => (await Task.WhenAll(regions.Select(async region =>
                    {
                        using var answer = await cluster[region].GetAsync(new Uri($"{cluster.DatabasePath}/collections/{name}", UriKind.Relative), token);
                        return answer.StatusCode == HttpStatusCode.OK;
                    }))).All(value => value), $"catalog collection {name}", token);
                }
            }

            async Task DeleteAsync()
            {
                // Deletes have a known prior version; no later client can recreate these ids.
                foreach (var id in expected.Keys.Order(StringComparer.Ordinal).Take(3).ToArray())
                {
                    await FaultSoakChecks.UntilAsync(async () => (await Task.WhenAll(regions.Select(region => FaultSoakChecks.ReadAsync(cluster[region], cluster.Document(id), token))))
                        .All(copy => copy is not null), $"delete precondition {id}", token);
                    if (await SendAsync(regions[0], id, "delete", () => cluster[regions[0]].DeleteAsync(cluster.Document(id), token)))
                    {
                        expected.TryRemove(id, out _);
                        deleted.Add(id);
                    }
                }
            }

            async Task BatchAsync(int round, string? offline = null)
            {
                var before = acknowledged.Count;
                await Task.WhenAll(plans[round].Where(plan => plan.Region != offline).Select(async plan =>
                {
                    foreach (var (id, body) in plan.Writes)
                    {
                        attempted[id] = body;
                        if (await SendAsync(plan.Region, id, "put", () => cluster[plan.Region].PutAsJsonAsync(cluster.Document(id), body, token)))
                        {
                            expected[id] = body;
                        }
                    }
                }).Append(CreateCatalogAsync(round)));
                history.Enqueue($"round {round} acknowledged={acknowledged.Count - before}");
                FaultSoakChecks.Check(round != 0 || acknowledged.Count > before, "baseline acknowledged no writes");
            }

            async Task CreateCatalogAsync(int round)
            {
                var name = $"catalog-{seed}-{round}";
                using var answer = await cluster[regions.First(region => region != OfflineRegion(round))].PostAsJsonAsync(
                    new Uri($"{cluster.DatabasePath}/collections", UriKind.Relative), new { name }, token);
                history.Enqueue($"catalog {name} {(int)answer.StatusCode}");
                if (answer.IsSuccessStatusCode)
                {
                    catalogs.Add(name);
                }
                else
                {
                    await FaultSoakChecks.RefusedAsync(answer, token);
                }
            }

            string? OfflineRegion(int round)
            {
                return round == 2 ? victim : null;
            }

            async Task<bool> SendAsync(string region, string id, string operation, Func<Task<HttpResponseMessage>> send)
            {
                using var answer = await send();
                var body = await answer.Content.ReadAsStringAsync(token);
                history.Enqueue($"{operation} {region} {id} {(int)answer.StatusCode} {JsonNode.Parse(body)?["error"]?["code"]}");
                if (!answer.IsSuccessStatusCode)
                {
                    await FaultSoakChecks.RefusedAsync(answer, token);
                    if (operation == "delete" && JsonNode.Parse(body)?["error"]?["code"]?.GetValue<string>() == "write_outcome_unknown")
                    {
                        // An uncertain delete may have committed; neither presence nor absence proves a lost write.
                        expected.TryRemove(id, out _);
                    }

                    return false;
                }

                var durability = JsonNode.Parse(body)?["durability"]?.GetValue<string>();
                FaultSoakChecks.Check(durability == "remote_durable", $"{operation} {id} incorrectly acknowledged as {durability}");
                acknowledged.Add((id, operation));
                return true;
            }

            async Task<bool> CopiesMatchAsync()
            {
                var copies = await Task.WhenAll(regions.Select(region => FaultSoakChecks.ListAsync(cluster[region], cluster.DatabasePath, cluster.Collection, token)));
                if (copies.Any(copy => copy is null))
                {
                    return false;
                }

                var digest = FaultSoakChecks.Digest(copies[0]!);
                return copies.All(copy => FaultSoakChecks.Digest(copy!) == digest
                    && expected.All(write => copy!.TryGetValue(write.Key, out var body) && body == FaultSoakChecks.Canonical(write.Value))
                    && deleted.All(id => !copy!.ContainsKey(id))
                    && copy!.All(write => attempted.TryGetValue(write.Key, out var body) && write.Value == FaultSoakChecks.Canonical(body)));
            }
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        finally
        {
            foreach (var feed in feeds)
            {
                try
                {
                    await feed.DisposeAsync();
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(cluster.Diagnostics, failures);
        }
    }


}
