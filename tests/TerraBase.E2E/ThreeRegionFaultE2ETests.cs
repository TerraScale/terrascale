using System.Diagnostics;
using System.Text.Json.Nodes;
using Bogus;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// Replication across gru, sjc and fra under faults: partitions, a region down and back, a restart in the middle of
/// replication, a replication backlog and clock skew. Each test owns its local nodes. What each scenario waits on is in
/// <see cref="Gaps"/>.
/// </summary>
public sealed class ThreeRegionFaultE2ETests
{
    private static readonly string[] All = [Cluster.Gru, Cluster.Sjc, Cluster.Fra];

    [Test]
    [Scenario(Nodes.InProcess, Faults.SimulatedPartition)]
    public async Task Regions_with_another_placed_peer_accept_writes_and_converge_after_a_direct_partition_heals()
    {
        await Gaps.RunAsync("partition-heal", async () =>
        {
            const int each = 200;
            await using var cluster = await Cluster.StartLocalAsync(All);
            var faker = new Faker();
            var sharedId = EntityIds.New().ToString();
            var shared = cluster.Document(sharedId);
            await Converge.SeedAllAsync(cluster, shared, new JsonObject { ["value"] = "seed" });
            cluster.Partition(Cluster.Fra, Cluster.Gru);
            // Both origins retain sjc as their remote durability peer; their direct stream is partitioned.
            var documents = new List<(string Id, JsonObject Body)>();
            var writes = new List<Task>();
            foreach (var region in new[] { Cluster.Fra, Cluster.Gru })
            {
                for (var i = 0; i < each; i++)
                {
                    var (id, body) = (EntityIds.New().ToString(), ThreeRegionE2ETests.Synthetic(faker));
                    documents.Add((id, body));
                    writes.Add(Docs.PutAsync(cluster[region], cluster.Document(id), body));
                }
            }

            // Both sides of the partition change the shared document.
            writes.Add(Docs.PutAsync(cluster[Cluster.Fra], shared, new JsonObject { ["value"] = "fra" }));
            writes.Add(Docs.PutAsync(cluster[Cluster.Gru], shared, new JsonObject { ["value"] = "gru" }));
            await Task.WhenAll(writes);

            // The partition holds: two seconds on, fra still lacks gru's writes and gru lacks fra's.
            await Task.Delay(TimeSpan.FromSeconds(2));
            var (fraSees, gruSees) = (await Docs.ReadAsync(cluster[Cluster.Fra], cluster.Document(documents[^1].Id)), await Docs.ReadAsync(cluster[Cluster.Gru], cluster.Document(documents[0].Id)));
            Expected.Check(fraSees is null && gruSees is null, $"the partition holds; fra sees gru's write: {fraSees is not null}, gru sees fra's: {gruSees is not null}");

            cluster.Heal();
            var healed = Stopwatch.GetTimestamp();
            var settledShared = await Converge.SettledAsync(cluster, shared, "shared document after the heal");
            var expected = Docs.Digest([.. documents.Select(document => (document.Id, Docs.Canonical(document.Body))), (sharedId, Docs.Canonical(settledShared.Value))]);
            _ = await Converge.ListedAsync(cluster, expected, "partition heal");
            Converge.Report($"partition heal, {2 * each} documents", healed);
            Console.WriteLine($"conflict outcome, partitioned same-field writes: {settledShared.Value!["value"]} won");
        }, local: true);
    }

    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task Region_down_then_back_catches_up_on_every_write_made_while_it_was_down()
    {
        await Gaps.RunAsync("region-down-rejoin", async () =>
        {
            const int count = 300;
            await using var cluster = await Cluster.StartLocalAsync(All);
            var faker = new Faker();
            await cluster.StopAsync(Cluster.Fra);
            var started = false;
            try
            {
                var documents = Enumerable.Range(0, count).Select(_ => (Id: EntityIds.New().ToString(), Body: ThreeRegionE2ETests.Synthetic(faker))).ToArray();
                await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (index, token) =>
                    await Docs.PutAsync(cluster[index % 2 == 0 ? Cluster.Gru : Cluster.Sjc], cluster.Document(documents[index].Id), documents[index].Body, token));
                await cluster.StartAgainAsync(Cluster.Fra);
                started = true;
                var back = Stopwatch.GetTimestamp();
                _ = await Converge.ListedAsync(cluster, Docs.Digest(documents.Select(document => (document.Id, Docs.Canonical(document.Body)))), "fra catch-up");
                Converge.Report($"fra catch-up on {count} documents after rejoin", back);
            }
            finally
            {
                if (!started)
                {
                    await cluster.StartAgainAsync(Cluster.Fra);
                }
            }
        }, local: true);
    }

    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task Restart_in_the_middle_of_replication_loses_no_write()
    {
        await Gaps.RunAsync("restart-mid-replication", async () =>
        {
            const int count = 800;
            await using var cluster = await Cluster.StartLocalAsync(All);
            var faker = new Faker();
            var documents = Enumerable.Range(0, count).Select(_ => (Id: EntityIds.New().ToString(), Body: ThreeRegionE2ETests.Synthetic(faker))).ToArray();
            var writes = Writes.Start([.. documents.Select(document => document.Id)], 8, (index, token) =>
                Docs.PutAsync(cluster[index % 2 == 0 ? Cluster.Gru : Cluster.Fra], cluster.Document(documents[index].Id), documents[index].Body, token));
            // sjc goes down and comes back while gru and fra are still writing and shipping to it.
            _ = await writes.UntilAcceptedAsync(count / 4, "a quarter of the writes");
            await cluster.StopAsync(Cluster.Sjc);
            await cluster.StartAgainAsync(Cluster.Sjc);
            Console.WriteLine($"sjc restarted after {writes}");
            await writes.CompletedAsync();
            var finished = Stopwatch.GetTimestamp();
            _ = await Converge.ListedAsync(cluster, Docs.Digest(documents.Select(document => (document.Id, Docs.Canonical(document.Body)))), "restart mid-replication");
            Converge.Report($"convergence after an sjc restart during {count} writes", finished);
        }, local: true);
    }

    // This covers orderly node stops. RemoteAckE2ETests separately proves immediate origin SIGKILL and survivor disk restart.
    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns()
    {
        const int count = 200;
        await Gaps.RunAsync("ack-then-stop", async () =>
        {
            await using var cluster = await Cluster.StartLocalAsync(All);
            var faker = new Faker();
            var documents = Enumerable.Range(0, count).Select(_ => (Id: EntityIds.New().ToString(), Body: ThreeRegionE2ETests.Synthetic(faker))).ToArray();
            await Parallel.ForEachAsync(documents, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (document, token) =>
                await Docs.PutAsync(cluster[Cluster.Gru], cluster.Document(document.Id), document.Body, token));
            await cluster.StopAsync(Cluster.Gru);
            await cluster.StartAgainAsync(Cluster.Gru);
            var back = Stopwatch.GetTimestamp();
            _ = await Converge.ListedAsync(cluster, Docs.Digest(documents.Select(document => (document.Id, Docs.Canonical(document.Body)))), "acknowledged writes after their region stopped");
            Converge.Report($"{count} writes acknowledged just before gru stopped", back);
        }, local: true);
    }

    // The writer restarts while a region is away, so its unsent changes must survive the restart and reach that region
    // when it returns, with no new write to trigger a send (the old suite's hinted-handoff test).
    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task A_restarted_region_replays_its_backlog_to_a_region_that_returns()
    {
        const int count = 300;
        await Gaps.RunAsync("restarted-sender-backlog", async () =>
        {
            await using var cluster = await Cluster.StartLocalAsync(All);
            var faker = new Faker();
            var documents = Enumerable.Range(0, count).Select(_ => (Id: EntityIds.New().ToString(), Body: ThreeRegionE2ETests.Synthetic(faker))).ToArray();
            await cluster.StopAsync(Cluster.Fra);
            var started = false;
            try
            {
                await Parallel.ForEachAsync(documents, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (document, token) =>
                    await Docs.PutAsync(cluster[Cluster.Gru], cluster.Document(document.Id), document.Body, token));
                await cluster.StopAsync(Cluster.Gru);
                await cluster.StartAgainAsync(Cluster.Gru);
                await cluster.StartAgainAsync(Cluster.Fra);
                started = true;
                var back = Stopwatch.GetTimestamp();
                _ = await Converge.ListedAsync(cluster, Docs.Digest(documents.Select(document => (document.Id, Docs.Canonical(document.Body)))), "backlog of a restarted region");
                Converge.Report($"fra catch-up on {count} writes of a restarted gru", back);
            }
            finally
            {
                if (!started)
                {
                    await cluster.StartAgainAsync(Cluster.Fra);
                }
            }
        }, local: true);
    }

    // fra is cut off while gru and sjc take 3000 writes, then the backlog drains.
    [Test]
    [Scenario(Nodes.InProcess, Faults.SimulatedPartition)]
    public async Task Replication_backlog_of_3000_writes_drains_after_the_region_returns()
    {
        await Gaps.RunAsync("backlog", async () =>
        {
            const int count = 3000;
            await using var cluster = await Cluster.StartLocalAsync(All);
            var faker = new Faker();
            var documents = Enumerable.Range(0, count).Select(_ => (Id: EntityIds.New().ToString(), Body: ThreeRegionE2ETests.Synthetic(faker))).ToArray();
            cluster.Partition(Cluster.Fra, Cluster.Gru);
            cluster.Partition(Cluster.Fra, Cluster.Sjc);
            await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 24 }, async (index, token) =>
                await Docs.PutAsync(cluster[index % 2 == 0 ? Cluster.Gru : Cluster.Sjc], cluster.Document(documents[index].Id), documents[index].Body, token));
            cluster.Heal();
            var healed = Stopwatch.GetTimestamp();
            _ = await Converge.ListedAsync(cluster, Docs.Digest(documents.Select(document => (document.Id, Docs.Canonical(document.Body)))), "backlog drain");
            var seconds = Stopwatch.GetElapsedTime(healed).TotalSeconds;
            Console.WriteLine($"backlog of {count} writes drained to fra in {seconds:F1} s ({count / seconds:F0} documents/s)");
        }, local: true);
    }

    // A causally later write must win however the two writers' clocks disagree: the stamp of a write is above every
    // stamp the document holds (design section 3), so a clock behind cannot lose a write made after seeing the newer one.
    [Test]
    [Scenario(Nodes.InProcess, Faults.ClockSkew)]
    [Arguments(Cluster.Sjc, 2000)]
    [Arguments(Cluster.Fra, -2000)]
    public async Task Causally_later_write_wins_everywhere_despite_clock_skew(string skewed, int skewMilliseconds)
    {
        await Gaps.RunAsync("skew-causal", async () =>
        {
            await using var cluster = await Cluster.StartLocalAsync(All, new Dictionary<string, TimeSpan> { [skewed] = TimeSpan.FromMilliseconds(skewMilliseconds) });
            // The skewed region writes first and a normal one overwrites, then the reverse.
            foreach (var (first, second) in new[] { (skewed, Cluster.Gru), (Cluster.Gru, skewed) })
            {
                var uri = cluster.Document(EntityIds.New().ToString());
                await Docs.PutAsync(cluster[first], uri, new JsonObject { ["value"] = first });
                _ = await Converge.UntilAsync(() => Docs.ReadAsync(cluster[second], uri), document => document?["value"]?.GetValue<string>() == first, $"{first}'s write on {second}");
                await Docs.PutAsync(cluster[second], uri, new JsonObject { ["value"] = second });
                var settled = await Converge.SettledAsync(cluster, uri, "causal overwrite under skew");
                Expected.Check(settled.Value!["value"]!.GetValue<string>() == second, $"{second}'s later write wins with {skewed} at {skewMilliseconds:+0;-0} ms; {settled}");
            }
        }, local: true);
    }

    // The competing writers are partitioned directly; fra durably confirms both. Additive markers detect any repair
    // that imports the competing write before healing, so a causal overwrite cannot masquerade as a concurrent winner.
    [Test]
    [Scenario(Nodes.InProcess, Faults.ClockSkew | Faults.SimulatedPartition)]
    public async Task Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins()
    {
        await Gaps.RunAsync("skew-concurrent", async () =>
        {
            await using var cluster = await Cluster.StartLocalAsync(All, new Dictionary<string, TimeSpan> { [Cluster.Sjc] = TimeSpan.FromSeconds(2) });
            var uri = cluster.Document(EntityIds.New().ToString());
            await Converge.SeedAllAsync(cluster, uri, new JsonObject { ["value"] = "seed" });
            foreach (var region in new[] { Cluster.Gru, Cluster.Sjc })
            {
                _ = await Converge.UntilAsync(() => Docs.GetJsonAsync(cluster[region], new Uri("/health/replication", UriKind.Relative)),
                    health => health?["peers"]?.AsArray().Any(peer => peer?["region"]?.GetValue<string>() == Cluster.Fra
                        && peer["status"]?.GetValue<string>() == "ok") == true, $"{region} streams to fra before its direct partition");
            }

            cluster.Partition(Cluster.Gru, Cluster.Sjc);
            string[] writers = [Cluster.Gru, Cluster.Sjc];
            await Task.WhenAll(writers.Select(region => Docs.PatchAsync(cluster[region], uri,
                new JsonObject { ["value"] = region, [$"{region}-write"] = true })));
            foreach (var region in writers)
            {
                var other = region == Cluster.Gru ? Cluster.Sjc : Cluster.Gru;
                var local = await Docs.ReadAsync(cluster[region], uri);
                Expected.Check(local?["value"]?.GetValue<string>() == region
                    && local[$"{region}-write"]?.GetValue<bool>() == true && local[$"{other}-write"] is null,
                    $"{region} holds only its own competing write before heal");
            }

            cluster.Heal();
            var settled = await Converge.SettledAsync(cluster, uri, "concurrent writes under skew");
            Console.WriteLine($"conflict outcome, concurrent writes with sjc +2 s: {settled.Value!["value"]} won");
            Expected.Check(settled.Value["value"]!.GetValue<string>() == Cluster.Sjc, $"sjc, 2 s ahead, wins; {settled}");
            Expected.Check(writers.All(region => settled.Value[$"{region}-write"]?.GetValue<bool>() == true),
                $"both competing writes survive the heal; {settled}");
        }, local: true);
    }
}
