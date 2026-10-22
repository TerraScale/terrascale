using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Bogus;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// V2: one shape client per region (<see cref="ShapeReader"/>) keeps a materialized view of the test's collection. After
/// each fault every view equals the documents its region lists, and every region lists the same documents: under a
/// partition, a collection drop and re-create, an anti-entropy repair and clock skew. Region-failover resume (FR-23) is
/// not exercised: every resume answers 409 until it lands, and the reader restarts at -1 instead.
/// </summary>
public sealed class ShapeConvergenceE2ETests
{
    private static readonly string[] All = [Cluster.Gru, Cluster.Sjc, Cluster.Fra];

    [Test]
    [Scenario(Nodes.InProcess, Faults.SimulatedPartition)]
    public async Task Shape_clients_in_every_region_converge_after_a_partition_heals()
    {
        SkipOnFly();
        await using var cluster = await Cluster.StartAsync(All);
        var readers = Readers(cluster);
        var documents = await WriteAsync(cluster, [.. All], 10);
        _ = await ConvergedAsync(cluster, readers, documents, "seed");

        // fra and gru lose their direct link and both keep writing (sjc confirms each side); a shared document gets both edits.
        cluster.Isolate(Cluster.Fra, Cluster.Gru);
        try
        {
            var shared = EntityIds.New().ToString();
            documents.AddRange(await WriteAsync(cluster, [Cluster.Fra, Cluster.Gru], 20));
            await Docs.PutAsync(cluster[Cluster.Fra], cluster.Document(shared), new JsonObject { ["value"] = "fra" });
            await Docs.PutAsync(cluster[Cluster.Gru], cluster.Document(shared), new JsonObject { ["value"] = "gru" });
            foreach (var reader in readers.Values)
            {
                _ = await reader.CatchUpAsync();
            }

            cluster.Heal();
            var settled = await Converge.SettledAsync(cluster, cluster.Document(shared), "the shared document after the heal");
            documents.Add((shared, settled.Value!.AsObject()));
            _ = await ConvergedAsync(cluster, readers, documents, "partition heal");
        }
        finally
        {
            cluster.Heal();
        }
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Shape_clients_follow_a_collection_drop_and_re_create()
    {
        await using var cluster = await Cluster.StartAsync(All);
        var readers = Readers(cluster);
        _ = await ConvergedAsync(cluster, readers, await WriteAsync(cluster, [.. All], 10), "before the drop");
        using (var dropped = await cluster[Cluster.Gru].DeleteAsync(new Uri($"{cluster.DatabasePath}/collections/{cluster.Collection}", UriKind.Relative)))
        {
            await Docs.EnsureSuccessAsync(dropped);
        }

        _ = await ConvergedAsync(cluster, readers, [], "after the drop");
        using (var created = await cluster[Cluster.Gru].PostAsJsonAsync(new Uri($"{cluster.DatabasePath}/collections", UriKind.Relative), new { name = cluster.Collection }))
        {
            await Docs.EnsureSuccessAsync(created);
        }

        foreach (var region in All)
        {
            _ = await Converge.UntilAsync(() => Docs.StatusAsync(cluster[region], new Uri($"{cluster.DatabasePath}/collections/{cluster.Collection}", UriKind.Relative)),
                status => status == HttpStatusCode.OK, $"the re-created collection on {region}");
        }

        _ = await ConvergedAsync(cluster, readers, await WriteAsync(cluster, [.. All], 10), "after the re-create");
    }

    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop | Faults.FileTampering)]
    public async Task Shape_clients_converge_after_anti_entropy_repairs_a_lost_document()
    {
        SkipOnFly();
        await using var cluster = await Cluster.StartAsync(All);
        var readers = Readers(cluster);
        var documents = await WriteAsync(cluster, [.. All], 10);
        _ = await ConvergedAsync(cluster, readers, documents, "before the loss");
        var lost = documents[4].Id;
        var started = Stopwatch.GetTimestamp();
        await cluster.StopAsync(Cluster.Sjc);
        await RepairAndContractE2ETests.DeleteRowsAsync(cluster.DatabaseFile(Cluster.Sjc), lost);
        await cluster.StartAgainAsync(Cluster.Sjc);

        var missing = await Docs.ReadAsync(cluster[Cluster.Sjc], cluster.Document(lost)) is null;
        Console.WriteLine($"V2 sjc back after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms; lost document {lost} absent on sjc: {missing}");
        await Assert.That(missing).IsTrue().Because("sjc must really have lost the acknowledged document before the repair");
        _ = await ConvergedAsync(cluster, readers, documents, "after the anti-entropy repair", TimeSpan.FromSeconds(200));
    }

    [Test]
    [Scenario(Nodes.InProcess, Faults.ClockSkew)]
    [Arguments(Cluster.Sjc, 2000)]
    [Arguments(Cluster.Fra, -2000)]
    public async Task Shape_clients_converge_under_clock_skew(string skewed, int skewMilliseconds)
    {
        SkipOnFly();
        var skew = TimeSpan.FromMilliseconds(skewMilliseconds);
        await using var cluster = await Cluster.StartAsync(All, new Dictionary<string, TimeSpan> { [skewed] = skew });
        await SkewedAsync(cluster, skewed, skewMilliseconds);
    }

    private static async Task SkewedAsync(Cluster cluster, string skewed, int skewMilliseconds)
    {
        var readers = Readers(cluster);
        var documents = await WriteAsync(cluster, [.. All], 10);
        var shared = EntityIds.New().ToString();
        foreach (var region in new[] { skewed, Cluster.Gru, skewed })
        {
            await Docs.PutAsync(cluster[region], cluster.Document(shared), new JsonObject { ["value"] = region });
        }

        var settled = await Converge.SettledAsync(cluster, cluster.Document(shared), "the shared document under skew");
        documents.Add((shared, settled.Value!.AsObject()));
        _ = await ConvergedAsync(cluster, readers, documents, $"clock skew {skewed} {skewMilliseconds:+0;-0} ms");
    }

    private static void SkipOnFly() =>
        Skip.When(Cluster.FlyRequested, "partitions, edits a stopped node's files or skews its clock: local nodes only");

    private static Dictionary<string, ShapeReader> Readers(Cluster cluster) =>
        cluster.Regions.ToDictionary(region => region, region => new ShapeReader(cluster[region], $"{cluster.DatabasePath}/collections/{cluster.Collection}"), StringComparer.Ordinal);

    private static async Task<List<(string Id, JsonObject Body)>> WriteAsync(Cluster cluster, string[] regions, int each)
    {
        var faker = new Faker();
        var documents = regions.SelectMany(region => Enumerable.Range(0, each).Select(_ => (Region: region, Id: EntityIds.New().ToString(), Body: ThreeRegionE2ETests.Synthetic(faker)))).ToArray();
        await Task.WhenAll(documents.Select(document => Docs.PutAsync(cluster[document.Region], cluster.Document(document.Id), document.Body)));
        return [.. documents.Select(document => (document.Id, document.Body))];
    }

    // Until every region's shape view equals what the region lists, and every region lists exactly the documents.
    private static async Task<ShapeStates> ConvergedAsync(
        Cluster cluster, Dictionary<string, ShapeReader> readers, List<(string Id, JsonObject Body)> documents, string what, TimeSpan? deadline = null)
    {
        var expected = Docs.Digest(documents.Select(document => (document.Id, Docs.Canonical(document.Body))));
        var started = Stopwatch.GetTimestamp();
        var states = await Converge.UntilAsync(
            async () => new ShapeStates([.. await Task.WhenAll(cluster.Regions.Select(async region =>
            {
                var caughtUp = await readers[region].CatchUpAsync();
                return new ShapeState(region, caughtUp, readers[region].View, await WaveAE2ETests.ListedAsync(cluster, region), readers[region].Refetches, readers[region].Responses);
            }))]),
            states => states.All.All(state => state.CaughtUp && state.View == state.Listed && state.Listed.Digest == expected),
            $"shape views {what}", deadline ?? Converge.Deadline);
        Console.WriteLine($"V2 {what}: every shape view equals its region's listing ({documents.Count} documents) "
            + $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms; {states}; regions {string.Join(' ', cluster.Regions.Select(cluster.Identity))}");
        return states;
    }

    private sealed record ShapeState(string Region, bool CaughtUp, Listing View, Listing Listed, int Refetches, int Responses)
    {
        public override string ToString() =>
            $"{Region}: view {View.Count}/{View.Digest[..12]} listed {Listed.Count}/{Listed.Digest[..12]} caught-up={CaughtUp} refetches={Refetches} responses={Responses}";
    }

    private sealed record ShapeStates(ShapeState[] All)
    {
        public override string ToString() => string.Join("; ", All.Select(state => state.ToString()));
    }
}
