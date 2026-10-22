using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// V5: Wave A's write contract between real regions, on local nodes and on ts-orch. A 2xx write is remote durable (A3):
/// another region serves it at once, and a write with no reachable peer answers a defined 503. A replicated collection drop
/// never deletes a write the receiver made after it (A7). Partitions need local nodes; RemoteAckE2ETests covers a SIGKILL
/// of the origin right after the 201 with separate processes.
/// </summary>
public sealed class WaveAE2ETests
{
    private static readonly string[] All = [Cluster.Gru, Cluster.Sjc, Cluster.Fra];

    [Test]
    [Scenario(Nodes.InProcess)]
    [Arguments(Cluster.Gru)]
    [Arguments(Cluster.Sjc)]
    [Arguments(Cluster.Fra)]
    public async Task A_201_is_remote_durable_and_another_region_serves_it_at_once(string origin)
    {
        await using var cluster = await Cluster.StartAsync(All);
        var id = EntityIds.New().ToString();
        var clock = Stopwatch.StartNew();
        using var response = await cluster[origin].PutAsJsonAsync(cluster.Document(id), new { origin });
        var acked = clock.ElapsedMilliseconds;
        var body = await Docs.BodyAsync(response);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(body?["durability"]?.GetValue<string>()).IsEqualTo("remote_durable");

        // One read per other region, no retry: at least one already holds what the 201 promised.
        var holders = new List<string>();
        foreach (var other in cluster.Regions.Where(region => region != origin))
        {
            if (await Docs.ReadAsync(cluster[other], cluster.Document(id)) is not null)
            {
                holders.Add(other);
            }
        }

        Console.WriteLine($"V5 remote-ack {cluster.Identity(origin)}: 201 remote_durable in {acked} ms; first read held by [{string.Join(',', holders)}] {clock.ElapsedMilliseconds} ms after send");
        await Assert.That(holders).IsNotEmpty().Because("a remote durable 201 means another placed region holds the write");
    }

    [Test]
    [Scenario(Nodes.InProcess, Faults.SimulatedPartition)]
    public async Task A_write_with_no_reachable_peer_answers_a_defined_503()
    {
        SkipLive();
        await using var cluster = await Cluster.StartAsync(All);
        cluster.Isolate(Cluster.Gru);
        try
        {
            var id = EntityIds.New().ToString();
            var clock = Stopwatch.StartNew();
            using var response = await cluster[Cluster.Gru].PutAsJsonAsync(cluster.Document(id), new { n = 1 });
            var text = await response.Content.ReadAsStringAsync();
            var code = response.StatusCode == HttpStatusCode.ServiceUnavailable ? JsonNode.Parse(text)?["error"]?["code"]?.GetValue<string>() : null;
            Console.WriteLine($"V5 peer-down {cluster.Identity(Cluster.Gru)}: HTTP {(int)response.StatusCode} {code ?? text} in {clock.ElapsedMilliseconds} ms");
            await Assert.That(code is "remote_region_unavailable" or "write_outcome_unknown").IsTrue().Because(text);
            if (code == "remote_region_unavailable")
            {
                await Assert.That(await Docs.ReadAsync(cluster[Cluster.Gru], cluster.Document(id))).IsNull().Because("refused before the document executed");
            }
        }
        finally
        {
            cluster.Heal();
        }
    }

    // A7: gru drops the collection while its direct link to sjc is cut; sjc then writes a new document (fra confirms both).
    // After the heal the drop removed what it covered and nothing newer, in every region.
    [Test]
    [Scenario(Nodes.InProcess, Faults.SimulatedPartition)]
    public async Task A_write_made_after_a_replicated_drop_survives_it_in_every_region()
    {
        SkipLive();
        await using var cluster = await Cluster.StartAsync(All);
        var covered = EntityIds.New().ToString();
        await Converge.SeedAllAsync(cluster, cluster.Document(covered), new JsonObject { ["before"] = "drop" });
        foreach (var region in new[] { Cluster.Gru, Cluster.Sjc })
        {
            // Both sides keep fra as their remote durability peer through the cut.
            _ = await Converge.UntilAsync(() => Docs.GetJsonAsync(cluster[region], new Uri("/health/replication", UriKind.Relative)),
                health => health?["peers"]?.AsArray().Any(peer => peer?["region"]?.GetValue<string>() == Cluster.Fra
                    && peer["status"]?.GetValue<string>() == "ok") == true, $"{region} streams to fra before the cut");
        }

        cluster.Isolate(Cluster.Gru, Cluster.Sjc);
        try
        {
            using (var dropped = await cluster[Cluster.Gru].DeleteAsync(new Uri($"{cluster.DatabasePath}/collections/{cluster.Collection}", UriKind.Relative)))
            {
                await Docs.EnsureSuccessAsync(dropped);
            }

            var later = EntityIds.New().ToString();
            var body = new JsonObject { ["after"] = "drop" };
            await Docs.PutAsync(cluster[Cluster.Sjc], cluster.Document(later), body);
            cluster.Heal();
            var healed = Stopwatch.GetTimestamp();
            var expected = Docs.Digest([(later, Docs.Canonical(body))]);
            var listings = await Converge.UntilAsync(
                async () => string.Join(' ', await Task.WhenAll(cluster.Regions.Select(async region => $"{region}={await ListedAsync(cluster, region)}"))),
                listed => listed.Split(expected).Length - 1 == cluster.Regions.Count, $"only the post-drop write ({expected[..12]}), in every region");
            Converge.Report($"V5 drop safety: converged to {listings}", healed);
        }
        finally
        {
            cluster.Heal();
        }
    }

    // A dropped collection lists as empty.
    internal static async Task<Listing> ListedAsync(Cluster cluster, string region)
    {
        try
        {
            return await Docs.ListAsync(cluster[region], cluster);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return new Listing(0, Docs.Digest([]));
        }
    }

    private static void SkipLive() =>
        Skip.When(Cluster.Live, "partitions regions: ts-orch has no partition switch, so local nodes only");
}
