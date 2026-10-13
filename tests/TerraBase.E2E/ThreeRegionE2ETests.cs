using System.Diagnostics;
using System.Text.Json.Nodes;
using Bogus;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// Replication across gru, sjc and fra without faults: every pair in both directions, three-way conflicts, delete
/// versus edits, bulk loads, bursts, large documents, counters and sets. What each scenario waits on is in
/// <see cref="Gaps"/>. Local by default; set <c>TERRABASE_E2E_FLY_URL</c> to run them against Fly.
/// </summary>
public sealed class ThreeRegionE2ETests
{
    private static readonly string[] All = [Cluster.Gru, Cluster.Sjc, Cluster.Fra];

    [Test]
    [Scenario(Nodes.InProcess)]
    [Arguments(Cluster.Gru, Cluster.Sjc)]
    [Arguments(Cluster.Sjc, Cluster.Gru)]
    [Arguments(Cluster.Sjc, Cluster.Fra)]
    [Arguments(Cluster.Fra, Cluster.Sjc)]
    [Arguments(Cluster.Gru, Cluster.Fra)]
    [Arguments(Cluster.Fra, Cluster.Gru)]
    public async Task Write_in_one_region_is_readable_in_the_other_two(string from, string to)
    {
        await Gaps.RunAsync("pair", async () =>
        {
            await using var cluster = await Cluster.StartAsync(All);
            var body = Synthetic(new Faker());
            var uri = cluster.Document(EntityIds.New().ToString());
            await Docs.PutAsync(cluster[from], uri, body);
            var acknowledged = Stopwatch.GetTimestamp();
            _ = await Converge.UntilAsync(() => Docs.ReadAsync(cluster[to], uri), document => Docs.Canonical(document) == Docs.Canonical(body), $"{from} to {to}");
            Converge.Report($"{cluster.Identity(from)} to {cluster.Identity(to)}", acknowledged);
            var everywhere = await Converge.SettledAsync(cluster, uri, $"{from} to every region");
            Expected.Check(Docs.Canonical(everywhere.Value) == Docs.Canonical(body), $"every region holds the write; {everywhere}");
        });
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value()
    {
        await Gaps.RunAsync("three-way-same-field", async () =>
        {
            await using var cluster = await Cluster.StartAsync(All);
            var uri = cluster.Document(EntityIds.New().ToString());
            await Converge.SeedAllAsync(cluster, uri, new JsonObject { ["value"] = "seed" });
            var written = All.ToDictionary(region => $"{region}-{EntityIds.New()}", region => region, StringComparer.Ordinal);
            var acknowledged = Stopwatch.GetTimestamp();
            await Task.WhenAll(written.Select(write => Docs.PutAsync(cluster[write.Value], uri, new JsonObject { ["value"] = write.Key })));
            var settled = await Converge.SettledAsync(cluster, uri, "three-way same-field writes");
            Converge.Report("three-way same field", acknowledged);
            var value = settled.Value!["value"]!.GetValue<string>();
            Expected.Check(written.ContainsKey(value), $"the value is one of the three writes; {settled}");
            Console.WriteLine($"conflict outcome, three-way same field: {written[value]} won");
        });
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Concurrent_writes_to_different_fields_in_three_regions_all_survive()
    {
        await Gaps.RunAsync("three-way-different-fields", async () =>
        {
            await using var cluster = await Cluster.StartAsync(All);
            var uri = cluster.Document(EntityIds.New().ToString());
            await Converge.SeedAllAsync(cluster, uri, new JsonObject { ["gru"] = "base", ["sjc"] = "base", ["fra"] = "base" });
            var written = All.ToDictionary(region => region, region => $"{region}-{EntityIds.New()}", StringComparer.Ordinal);
            await Task.WhenAll(All.Select(region => Docs.PatchAsync(cluster[region], uri, new JsonObject { [region] = written[region] })));
            var settled = await Converge.SettledAsync(cluster, uri, "three-way different-field writes");
            var outcome = All.ToDictionary(region => region, region => settled.Value![region]?.GetValue<string>(), StringComparer.Ordinal);
            Console.WriteLine($"conflict outcome, three-way different fields: {string.Join(", ", outcome.Select(field => $"{field.Key}={(field.Value == written[field.Key] ? "kept" : "lost")}"))}");
            Expected.Check(All.All(region => outcome[region] == written[region]), $"every region's field survives; {settled}");
        });
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Delete_in_one_region_and_edits_in_the_other_two_converge_and_stay_converged()
    {
        await Gaps.RunAsync("three-way-delete-versus-edit", async () =>
        {
            await using var cluster = await Cluster.StartAsync(All);
            var uri = cluster.Document(EntityIds.New().ToString());
            await Converge.SeedAllAsync(cluster, uri, new JsonObject { ["value"] = "seed" });
            var edits = new[] { Cluster.Sjc, Cluster.Fra }.ToDictionary(region => region, region => $"{region}-{EntityIds.New()}", StringComparer.Ordinal);
            await Task.WhenAll([
                Docs.DeleteAsync(cluster[Cluster.Gru], uri),
                .. edits.Select(edit => Docs.PutAsync(cluster[edit.Key], uri, new JsonObject { ["value"] = edit.Value })),
            ]);
            var settled = await Converge.SettledAsync(cluster, uri, "delete versus two edits", absent: true);
            var value = settled.Value?["value"]?.GetValue<string>();
            Expected.Check(settled.Value is null || edits.ContainsValue(value!), $"deleted, or one of the edits; {settled}");
            Console.WriteLine($"conflict outcome, delete versus two edits: {(value is null ? "delete" : edits.Single(edit => edit.Value == value).Key)} won");
            // A late round must not split the regions again.
            await Task.Delay(TimeSpan.FromSeconds(1));
            var later = await Converge.AllAsync(cluster, uri);
            Expected.Check(later.ToString() == settled.ToString(), $"unchanged a second later; was {settled}, now {later}");
        });
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Bulk_3000_documents_written_across_three_regions_have_matching_counts_and_digests()
    {
        await Gaps.RunAsync("three-way-bulk", async () =>
        {
            const int count = 3000;
            await using var cluster = await Cluster.StartAsync(All);
            var faker = new Faker();
            var documents = Enumerable.Range(0, count).Select(_ => (Id: EntityIds.New().ToString(), Body: Synthetic(faker))).ToArray();
            var expected = Docs.Digest(documents.Select(document => (document.Id, Docs.Canonical(document.Body))));
            await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 24 }, async (index, token) =>
            {
                var (id, body) = documents[index];
                await Docs.PutAsync(cluster[All[index % All.Length]], cluster.Document(id), body, token);
            });
            var acknowledged = Stopwatch.GetTimestamp();
            var listings = await Converge.ListedAsync(cluster, expected, "three-way bulk load");
            Converge.Report($"three-way bulk {count} documents", acknowledged);
            Expected.Check(listings.Values.All(listing => listing.Count == count), $"{count} documents in every region; {string.Join(", ", listings)}");
        });
    }

    // Each region overwrites one document 40 times in a row while the others do the same. A region's own writes are
    // ordered, so the survivor must be some region's last write, never an older one.
    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Burst_of_overwrites_from_three_regions_converges_to_a_last_write()
    {
        await Gaps.RunAsync("three-way-burst", async () =>
        {
            const int writes = 40;
            await using var cluster = await Cluster.StartAsync(All);
            var uri = cluster.Document(EntityIds.New().ToString());
            await Converge.SeedAllAsync(cluster, uri, new JsonObject { ["value"] = "seed" });
            var acknowledged = Stopwatch.GetTimestamp();
            await Task.WhenAll(All.Select(async region =>
            {
                for (var i = 0; i < writes; i++)
                {
                    await Docs.PutAsync(cluster[region], uri, new JsonObject { ["value"] = $"{region}-{i}", ["region"] = region, ["step"] = i });
                }
            }));
            var settled = await Converge.SettledAsync(cluster, uri, "burst");
            Converge.Report($"burst of {writes} overwrites per region", acknowledged);
            Expected.Check(settled.Value!["step"]!.GetValue<int>() == writes - 1, $"the survivor is a region's last write; {settled}");
            Console.WriteLine($"conflict outcome, burst: {settled.Value["region"]} won");
        });
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Documents_near_the_size_limit_and_with_many_fields_replicate_intact()
    {
        await Gaps.RunAsync("large-documents", async () =>
        {
            await using var cluster = await Cluster.StartAsync(All);
            var faker = new Faker();
            // 900 KiB of text, under the 1 MiB document limit with room for the JSON around it.
            var large = new JsonObject { ["blob"] = faker.Random.String2(900 * 1024), ["n"] = 1 };
            var wide = new JsonObject();
            for (var i = 0; i < 2000; i++)
            {
                wide[$"f{i:D4}"] = faker.Random.Int();
            }

            var (largeUri, wideUri) = (cluster.Document(EntityIds.New().ToString()), cluster.Document(EntityIds.New().ToString()));
            var acknowledged = Stopwatch.GetTimestamp();
            await Task.WhenAll(Docs.PutAsync(cluster[Cluster.Fra], largeUri, large), Docs.PutAsync(cluster[Cluster.Sjc], wideUri, wide));
            var (settledLarge, settledWide) = (await Converge.SettledAsync(cluster, largeUri, "900 KiB document"), await Converge.SettledAsync(cluster, wideUri, "2000-field document"));
            Converge.Report("900 KiB and 2000-field documents", acknowledged);
            Expected.Check(Docs.Canonical(settledLarge.Value) == Docs.Canonical(large), "the 900 KiB document is intact everywhere");
            Expected.Check(Docs.Canonical(settledWide.Value) == Docs.Canonical(wide), "the 2000-field document is intact everywhere");
        });
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Concurrent_inc_in_three_regions_sums()
    {
        await Gaps.RunAsync("three-way-counters", async () =>
        {
            const int times = 7;
            await using var cluster = await Cluster.StartAsync(All);
            var uri = cluster.Document(EntityIds.New().ToString());
            await Converge.SeedAllAsync(cluster, uri, new JsonObject { ["name"] = "counter" });
            await Task.WhenAll(All.Select(async region =>
            {
                for (var i = 0; i < times; i++)
                {
                    _ = await Docs.OpsAsync(cluster[region], uri, Op("inc", "n", 1));
                }
            }));
            var settled = await Converge.SettledAsync(cluster, uri, "three-way inc");
            Expected.Check(settled.Value!["n"]?.ToJsonString() == $"{times * All.Length}", $"{times} inc in each region sum to {times * All.Length}; {settled}");
        });
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Concurrent_set_adds_in_three_regions_union_and_a_concurrent_add_beats_a_remove()
    {
        await Gaps.RunAsync("three-way-sets", async () =>
        {
            await using var cluster = await Cluster.StartAsync(All);
            var uri = cluster.Document(EntityIds.New().ToString());
            await Converge.SeedAllAsync(cluster, uri, new JsonObject { ["tags"] = new JsonArray("x") });
            await Task.WhenAll(
                Docs.OpsAsync(cluster[Cluster.Gru], uri, Op("add", "tags", "gru"), Op("remove", "tags", "x")),
                Docs.OpsAsync(cluster[Cluster.Sjc], uri, Op("add", "tags", "sjc"), Op("add", "tags", "x")),
                Docs.OpsAsync(cluster[Cluster.Fra], uri, Op("add", "tags", "fra")));
            var settled = await Converge.SettledAsync(cluster, uri, "three-way set adds");
            Expected.Check(settled.Value!["tags"]?.ToJsonString() == """["fra","gru","sjc","x"]""", $"the union, with x kept by sjc's add; {settled}");
        });
    }

    internal static JsonObject Synthetic(Faker faker) => new() { ["value"] = faker.Lorem.Sentence(), ["count"] = faker.Random.Int(0, 1_000_000) };

    private static JsonObject Op(string op, string field, JsonNode value) =>
        new() { ["op"] = op, ["path"] = "/" + field, [op == "inc" ? "by" : "value"] = value };
}
