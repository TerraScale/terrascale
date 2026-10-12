using System.Diagnostics;
using System.Text.Json.Nodes;
using Bogus;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// Replication across gru, sjc and fra under faults: partitions, a region down and back, a restart in the middle of
/// replication, a replication backlog and clock skew. Partitions and skew need the local nodes, so on Fly those tests are
/// skipped with the reason. Stopping a machine on Fly touches the shared app, so there those tests run only from
/// <c>make terrabase-e2e-fly-outage</c>. What each scenario waits on is in <see cref="Gaps"/>.
/// </summary>
[Category("fly-outage")]
public sealed class ThreeRegionFaultE2ETests
{
    private static readonly string[] All = [Cluster.Gru, Cluster.Sjc, Cluster.Fra];

    [Test]
    public async Task Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals()
    {
        Skip.When(Cluster.FlyRequested, "Fly has no way to cut 6PN between two machines; the partition runs on the local nodes only");
        const int each = 200;
        await using var cluster = await Cluster.StartAsync(All);
        var faker = new Faker();
        var sharedId = EntityIds.New().ToString();
        var shared = cluster.Document(sharedId);
        await Converge.SeedAllAsync(cluster, shared, new JsonObject { ["value"] = "seed" });
        await Gaps.RunAsync("partition-heal", async () =>
        {
            cluster.Partition(Cluster.Fra, Cluster.Gru);
            cluster.Partition(Cluster.Fra, Cluster.Sjc);
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
        });
    }

    [Test]
    public async Task Region_down_then_back_catches_up_on_every_write_made_while_it_was_down()
    {
        SkipOnFlyUnlessOutageRun();
        const int count = 300;
        await using var cluster = await Cluster.StartAsync(All);
        await Gaps.RunAsync("region-down-rejoin", async () =>
        {
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
        });
    }

    [Test]
    public async Task Restart_in_the_middle_of_replication_loses_no_write()
    {
        SkipOnFlyUnlessOutageRun();
        const int count = 800;
        await using var cluster = await Cluster.StartAsync(All);
        await Gaps.RunAsync("restart-mid-replication", async () =>
        {
            var faker = new Faker();
            var documents = Enumerable.Range(0, count).Select(_ => (Id: EntityIds.New().ToString(), Body: ThreeRegionE2ETests.Synthetic(faker))).ToArray();
            var written = 0;
            var writer = Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (index, token) =>
            {
                await Docs.PutAsync(cluster[index % 2 == 0 ? Cluster.Gru : Cluster.Fra], cluster.Document(documents[index].Id), documents[index].Body, token);
                _ = Interlocked.Increment(ref written);
            });
            // sjc goes down and comes back while gru and fra are still writing and shipping to it.
            _ = await Converge.UntilAsync(() => Task.FromResult(Volatile.Read(ref written)), done => done >= count / 4, "a quarter of the writes");
            await cluster.StopAsync(Cluster.Sjc);
            await cluster.StartAgainAsync(Cluster.Sjc);
            Console.WriteLine($"sjc restarted after {Volatile.Read(ref written)} of {count} writes");
            await writer;
            var finished = Stopwatch.GetTimestamp();
            _ = await Converge.ListedAsync(cluster, Docs.Digest(documents.Select(document => (document.Id, Docs.Canonical(document.Body)))), "restart mid-replication");
            Converge.Report($"convergence after an sjc restart during {count} writes", finished);
        });
    }

    // fra is cut off while gru and sjc take 3000 writes, then the backlog drains.
    [Test]
    public async Task Replication_backlog_of_3000_writes_drains_after_the_region_returns()
    {
        Skip.When(Cluster.FlyRequested, "the backlog builds behind a partition, which runs on the local nodes only");
        const int count = 3000;
        await using var cluster = await Cluster.StartAsync(All);
        await Gaps.RunAsync("backlog", async () =>
        {
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
        });
    }

    // A causally later write must win however the two writers' clocks disagree: the stamp of a write is above every
    // stamp the document holds (design section 3), so a clock behind cannot lose a write made after seeing the newer one.
    [Test]
    [Arguments(Cluster.Sjc, 2000)]
    [Arguments(Cluster.Fra, -2000)]
    public async Task Causally_later_write_wins_everywhere_despite_clock_skew(string skewed, int skewMilliseconds)
    {
        Skip.When(Cluster.FlyRequested, "clock skew is set on the local nodes only");
        await using var cluster = await Cluster.StartAsync(All, new Dictionary<string, TimeSpan> { [skewed] = TimeSpan.FromMilliseconds(skewMilliseconds) });
        await Gaps.RunAsync("skew-causal", async () =>
        {
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
        });
    }

    // Same-field writes with sjc's clock 2 s ahead, made while every region is cut off from the others, so none saw
    // another's write: the regions agree after the heal, and the documented outcome is that the writer ahead wins the
    // last-writer-wins choice (design section 3, skew bound). Without the partition a region could see sjc's write
    // first and stamp after it, which is a causal overwrite, not a concurrent one.
    [Test]
    public async Task Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins()
    {
        Skip.When(Cluster.FlyRequested, "clock skew and partitions are set on the local nodes only");
        await using var cluster = await Cluster.StartAsync(All, new Dictionary<string, TimeSpan> { [Cluster.Sjc] = TimeSpan.FromSeconds(2) });
        var uri = cluster.Document(EntityIds.New().ToString());
        await Converge.SeedAllAsync(cluster, uri, new JsonObject { ["value"] = "seed" });
        await Gaps.RunAsync("skew-concurrent", async () =>
        {
            cluster.Partition(Cluster.Gru, Cluster.Sjc);
            cluster.Partition(Cluster.Gru, Cluster.Fra);
            cluster.Partition(Cluster.Sjc, Cluster.Fra);
            await Task.WhenAll(All.Select(region => Docs.PutAsync(cluster[region], uri, new JsonObject { ["value"] = region })));
            cluster.Heal();
            var settled = await Converge.SettledAsync(cluster, uri, "concurrent writes under skew");
            Console.WriteLine($"conflict outcome, concurrent writes with sjc +2 s: {settled.Value!["value"]} won");
            Expected.Check(settled.Value["value"]!.GetValue<string>() == Cluster.Sjc, $"sjc, 2 s ahead, wins; {settled}");
        });
    }

    private static void SkipOnFlyUnlessOutageRun() =>
        Skip.When(Cluster.FlyRequested && Environment.GetEnvironmentVariable("TERRABASE_E2E_FLY_OUTAGE") != "1",
            "stops a Fly machine of the shared app: run it alone with make terrabase-e2e-fly-outage");
}
