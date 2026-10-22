using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Bogus;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// A node killed with SIGKILL right after acknowledging writes, a storage stall (the database file locked by another
/// writer, so commits cannot proceed), and the lossy link the peer traffic will run through. The single-node halves run
/// on today's write path and must pass. The replicated halves run in three regions; <see cref="Gaps"/> names any that still fail.
/// Local only: they kill processes and lock files of their own nodes.
/// </summary>
public sealed class CrashAndStallE2ETests
{
    private static readonly string[] All = [Cluster.Gru, Cluster.Sjc, Cluster.Fra];

    // Writers keep writing while the node is killed: every acknowledged write must be there after the restart, exactly.
    // An unacknowledged one may or may not be, but never as anything other than what was written.
    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill)]
    public async Task Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process()
    {
        Skip.When(Cluster.Live, "kills a local node process");
        await using var node = await NodeProcess.StartAsync();
        var database = await node.CreateTenantAsync();
        _ = await Docs.PostAsync(node.Internal, new Uri($"{database}/collections", UriKind.Relative), new JsonObject { ["name"] = "crash" });
        var faker = new Faker();
        var acknowledged = new ConcurrentDictionary<string, string>();
        var attempted = new ConcurrentDictionary<string, string>();
        using var stop = new CancellationTokenSource();
        var writers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var (id, body) = (EntityIds.New().ToString(), ThreeRegionE2ETests.Synthetic(faker));
                attempted[id] = Docs.Canonical(body);
                try
                {
                    await Docs.PutAsync(node.Internal, Document(database, id), body);
                    acknowledged[id] = Docs.Canonical(body);
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
                {
                    return; // the node is gone
                }
            }
        })).ToArray();
        _ = await Converge.UntilAsync(() => Task.FromResult(acknowledged.Count), count => count >= 500, "500 acknowledged writes");
        await node.KillAsync();
        await stop.CancelAsync();
        await Task.WhenAll(writers);
        var acked = acknowledged.ToArray();
        await node.StartAgainAsync();

        foreach (var (id, canonical) in acked)
        {
            var read = await Docs.ReadAsync(node.Internal, Document(database, id));
            await Assert.That(Docs.Canonical(read)).IsEqualTo(canonical).Because($"acknowledged write {id} after SIGKILL");
        }

        var unacknowledged = attempted.Where(pair => !acknowledged.ContainsKey(pair.Key)).ToArray();
        foreach (var (id, canonical) in unacknowledged)
        {
            var read = await Docs.ReadAsync(node.Internal, Document(database, id));
            await Assert.That(read is null || Docs.Canonical(read) == canonical).IsTrue().Because($"unacknowledged write {id} is absent or exact");
        }

        Console.WriteLine($"SIGKILL after {acked.Length} acknowledged writes ({unacknowledged.Length} in flight): all acknowledged writes survived");
    }

    // The replicated half: gru, a separate OS process, is killed right after its acknowledgements, before it can have
    // shipped them all, and every region must still end with every acknowledged write once gru is back.
    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill)]
    public async Task Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back()
    {
        Skip.When(Cluster.Live, "kills a local node process");
        await Gaps.RunAsync("crash-replicated", async () =>
        {
            await using var gru = await NodeProcess.StartAsync();
            // Region identities and peer addresses for node processes come with peer streams (R4a).
            throw new NotSupportedException($"node processes cannot form a three-region cluster before peer streams (R4a); a single node process serves alone");
        });
    }

    // A storage stall on one node: its database file is held by another writer for five seconds while its clients write.
    // A request may wait out the stall or be refused, but an acknowledged write is never lost, and writes work again after.
    [Test]
    [Scenario(Nodes.InProcess, Faults.StorageStall)]
    public async Task Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it()
    {
        Skip.When(Cluster.Live, "locks a local node's database file");
        await Gaps.RunAsync("storage-stall-single", async () =>
        {
            await using var cluster = await Cluster.StartAsync(Cluster.Gru);
            var outcome = await StallAsync(Cluster.Gru, [Cluster.Gru], TimeSpan.FromSeconds(5));
            foreach (var (id, canonical) in outcome.Acknowledged)
            {
                Expected.Check(Docs.Canonical(await Docs.ReadAsync(cluster[Cluster.Gru], cluster.Document(id))) == canonical, $"acknowledged write {id} after the stall");
            }

            Expected.Check(outcome.AfterStall > 0, "writes resume once the stall ends");
            Console.WriteLine($"storage stall: {outcome.Acknowledged.Count} acknowledged, {outcome.Refused} refused, {outcome.AfterStall} acknowledged after the stall");
        });
    }

    // The replicated half: sjc stalls for ten seconds while every region writes; afterwards every region holds exactly the
    // acknowledged writes.
    [Test]
    [Scenario(Nodes.InProcess, Faults.StorageStall)]
    public async Task A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge()
    {
        Skip.When(Cluster.Live, "locks a local node's database file");
        await Gaps.RunAsync("storage-stall", async () =>
        {
            await using var cluster = await Cluster.StartAsync(All);
            var outcome = await StallAsync(Cluster.Sjc, All, TimeSpan.FromSeconds(10));
            var expected = Docs.Digest(outcome.Acknowledged.Select(pair => (pair.Key, pair.Value)));
            _ = await Converge.ListedAsync(cluster, expected, "every region after sjc's storage stall");
        });
    }

    // Peer traffic through a lossy link (delays, a throughput cap, connection resets): every region ends with exactly the
    // writes, each applied once, however often a group was resent.
    [Test]
    [Scenario(Nodes.InProcess, Faults.NetworkFault)]
    public async Task Writes_converge_exactly_over_lossy_peer_links()
    {
        Skip.When(Cluster.Live, "ts-orch has no lossy link; the lossy link runs on the local nodes only");
        await Gaps.RunAsync("lossy-link", async () =>
        {
            await using var cluster = await Cluster.StartAsync(All);
            await using var link = cluster.RouteThrough(Cluster.Gru, Cluster.Sjc, peer => new LossyLink(peer, TimeSpan.FromMilliseconds(200), 256 * 1024, 0.05, seed: 7).Start());
            var faker = new Faker();
            var documents = Enumerable.Range(0, 1000).Select(_ => (Id: EntityIds.New().ToString(), Body: ThreeRegionE2ETests.Synthetic(faker))).ToArray();
            await Parallel.ForEachAsync(Enumerable.Range(0, documents.Length), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (index, token) =>
                await Docs.PutAsync(cluster[All[index % All.Length]], cluster.Document(documents[index].Id), documents[index].Body, token));
            _ = await Converge.ListedAsync(cluster, Docs.Digest(documents.Select(document => (document.Id, Docs.Canonical(document.Body)))), "writes over lossy links");
            Console.WriteLine($"lossy link reset {link.Resets} connections");
        });
    }

    // The lossy link itself, against an echo server: bytes arrive intact and in order through delays and the cap.
    [Test]
    [Scenario(Nodes.Harness, Faults.NetworkFault)]
    public async Task The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap()
    {
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var serving = Task.Run(async () =>
        {
            using var connection = await echo.AcceptTcpClientAsync();
            await connection.GetStream().CopyToAsync(connection.GetStream());
        });
        await using var link = new LossyLink((IPEndPoint)echo.LocalEndpoint, TimeSpan.FromMilliseconds(20), 64 * 1024, resetChance: 0, seed: 1).Start();
        using var client = new TcpClient();
        await client.ConnectAsync(link.Endpoint);
        var sent = new byte[32 * 1024];
        new Random(3).NextBytes(sent);
        var clock = Stopwatch.StartNew();
        await client.GetStream().WriteAsync(sent);
        var received = new byte[sent.Length];
        await client.GetStream().ReadExactlyAsync(received);
        await Assert.That(received.SequenceEqual(sent)).IsTrue();
        // 32 KiB each way at 64 KiB/s takes at least half a second through the cap.
        await Assert.That(clock.Elapsed).IsGreaterThan(TimeSpan.FromMilliseconds(400));
        client.Close();
        await serving.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private sealed record StallOutcome(IReadOnlyDictionary<string, string> Acknowledged, int Refused, int AfterStall);

    // The Turso engine (#98) holds the database file's write lock for its single writer, so no outside writer can stall it
    // (it used to, or got "database is locked", depending on timing). The stall needs the harness's in-process hook
    // (Cluster.StallStorageAsync, R3's #106), so until then the scenario reports itself not supported, every run.
    private static Task<StallOutcome> StallAsync(string region, string[] writing, TimeSpan stall) =>
        throw new NotSupportedException(
            $"no storage stall hook: cannot stall {region}'s database file for {stall.TotalSeconds:F0} s while {string.Join(", ", writing)} write; it needs Cluster.StallStorageAsync (#106)");

    private static Uri Document(string database, string id) =>
        new($"{database}/collections/crash/documents/by-key?id={Uri.EscapeDataString(id)}", UriKind.Relative);
}
