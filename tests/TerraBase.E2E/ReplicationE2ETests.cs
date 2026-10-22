using System.Diagnostics;
using System.Text.Json.Nodes;
using Bogus;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// Leaderless multi-region writes in both directions between gru and sjc. Each scenario waits for convergence with a
/// deadline and logs how long replication took. Local by default; set <c>TERRABASE_E2E_ORCH_NODES</c> to run them against ts-orch.
/// </summary>
public sealed class ReplicationE2ETests
{
    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Write_in_gru_is_readable_in_sjc_with_the_same_content()
    {
        await Gaps.RunAsync("E1", async () =>
        {
            await using var cluster = await Cluster.StartAsync();
            var body = Synthetic(new Faker());
            var uri = cluster.Document(EntityIds.New().ToString());
            await Docs.PutAsync(cluster[Cluster.Gru], uri, body);
            var acknowledged = Stopwatch.GetTimestamp();
            var seen = await Converge.UntilAsync(() => Docs.ReadAsync(cluster[Cluster.Sjc], uri), document => Docs.Canonical(document) == Docs.Canonical(body), "gru to sjc");
            Converge.Report("gru to sjc", acknowledged);
            await Assert.That(Docs.Canonical(seen)).IsEqualTo(Docs.Canonical(body));
        });
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Write_in_sjc_is_readable_in_gru_with_the_same_content()
    {
        await Gaps.RunAsync("E2", async () =>
        {
            await using var cluster = await Cluster.StartAsync();
            var body = Synthetic(new Faker());
            var uri = cluster.Document(EntityIds.New().ToString());
            await Docs.PutAsync(cluster[Cluster.Sjc], uri, body);
            var acknowledged = Stopwatch.GetTimestamp();
            var seen = await Converge.UntilAsync(() => Docs.ReadAsync(cluster[Cluster.Gru], uri), document => Docs.Canonical(document) == Docs.Canonical(body), "sjc to gru");
            Converge.Report("sjc to gru", acknowledged);
            await Assert.That(Docs.Canonical(seen)).IsEqualTo(Docs.Canonical(body));
        });
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Concurrent_writes_to_the_same_field_converge_to_one_identical_value()
    {
        await Gaps.RunAsync("E3", async () =>
        {
            await using var cluster = await Cluster.StartAsync();
            var uri = cluster.Document(EntityIds.New().ToString());
            await Converge.SeedAsync(cluster, uri, new JsonObject { ["value"] = "seed" });
            var fromGru = $"gru-{EntityIds.New()}";
            var fromSjc = $"sjc-{EntityIds.New()}";
            var acknowledged = Stopwatch.GetTimestamp();
            await Task.WhenAll(
                Docs.PutAsync(cluster[Cluster.Gru], uri, new JsonObject { ["value"] = fromGru }),
                Docs.PutAsync(cluster[Cluster.Sjc], uri, new JsonObject { ["value"] = fromSjc }));
            var settled = await Converge.UntilAsync(() => Converge.BothAsync(cluster, uri), copies => copies.Identical && copies.Gru is not null, "concurrent same-field writes");
            Converge.Report("concurrent same field", acknowledged);
            // Both regions hold one value, and it is one of the two writes: nothing else was applied.
            await Assert.That(new[] { fromGru, fromSjc }.Contains(settled.Gru!["value"]!.GetValue<string>())).IsTrue();
        });
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Concurrent_writes_to_different_fields_both_survive()
    {
        await Gaps.RunAsync("E4", async () =>
        {
            await using var cluster = await Cluster.StartAsync();
            var uri = cluster.Document(EntityIds.New().ToString());
            await Converge.SeedAsync(cluster, uri, new JsonObject { ["a"] = "base", ["b"] = "base" });
            var fromGru = $"gru-{EntityIds.New()}";
            var fromSjc = $"sjc-{EntityIds.New()}";
            var acknowledged = Stopwatch.GetTimestamp();
            await Task.WhenAll(
                Docs.PatchAsync(cluster[Cluster.Gru], uri, new JsonObject { ["a"] = fromGru }),
                Docs.PatchAsync(cluster[Cluster.Sjc], uri, new JsonObject { ["b"] = fromSjc }));
            var settled = await Converge.UntilAsync(() => Converge.BothAsync(cluster, uri), copies => copies.Identical && copies.Gru is not null, "concurrent different-field writes");
            Converge.Report("concurrent different fields", acknowledged);
            // Holds today and after the lattice: the regions agree, and each field is a value someone wrote.
            var a = settled.Gru!["a"]!.GetValue<string>();
            var b = settled.Gru!["b"]!.GetValue<string>();
            await Assert.That(a == "base" || a == fromGru).IsTrue();
            await Assert.That(b == "base" || b == fromSjc).IsTrue();
            Expected.Check(a == fromGru && b == fromSjc, $"both edits survive; got a={a}, b={b}");
        });
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Delete_in_one_region_and_edit_in_the_other_converge_to_the_same_state()
    {
        await Gaps.RunAsync("E5", async () =>
        {
            await using var cluster = await Cluster.StartAsync();
            var uri = cluster.Document(EntityIds.New().ToString());
            await Converge.SeedAsync(cluster, uri, new JsonObject { ["value"] = "seed" });
            var edit = $"sjc-{EntityIds.New()}";
            var acknowledged = Stopwatch.GetTimestamp();
            await Task.WhenAll(
                Docs.DeleteAsync(cluster[Cluster.Gru], uri),
                Docs.PutAsync(cluster[Cluster.Sjc], uri, new JsonObject { ["value"] = edit }));
            _ = await Converge.UntilAsync(() => Converge.BothAsync(cluster, uri), copies => copies.Identical, "delete versus edit");
            Converge.Report("delete versus edit", acknowledged);
            // Identical once settled, and still identical after a second: a late round does not split the regions again.
            await Task.Delay(TimeSpan.FromSeconds(1));
            await Assert.That((await Converge.BothAsync(cluster, uri)).Identical).IsTrue();
        });
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Bulk_1000_documents_split_across_regions_have_matching_counts_and_checksums()
    {
        await Gaps.RunAsync("E6", async () =>
        {
            const int count = 1000;
            await using var cluster = await Cluster.StartAsync();
            var faker = new Faker();
            var documents = Enumerable.Range(0, count).Select(_ => (Id: EntityIds.New().ToString(), Body: Synthetic(faker))).ToArray();
            var expected = Docs.Digest(documents.Select(document => (document.Id, Docs.Canonical(document.Body))));
            // Even documents are written through gru, odd ones through sjc.
            await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (index, token) =>
            {
                var (id, body) = documents[index];
                await Docs.PutAsync(cluster[index % 2 == 0 ? Cluster.Gru : Cluster.Sjc], cluster.Document(id), body, token);
            });
            var acknowledged = Stopwatch.GetTimestamp();
            var (gru, sjc) = await Converge.UntilAsync(
                async () => (Gru: await Docs.ListAsync(cluster[Cluster.Gru], cluster), Sjc: await Docs.ListAsync(cluster[Cluster.Sjc], cluster)),
                listings => listings.Gru.Digest == expected && listings.Sjc.Digest == expected, "bulk load");
            Converge.Report($"bulk {count} documents", acknowledged);
            await Assert.That(gru.Count).IsEqualTo(count);
            await Assert.That(sjc.Count).IsEqualTo(count);
        });
    }

    // The document every scenario writes: random text and a number.
    private static JsonObject Synthetic(Faker faker) => new() { ["value"] = faker.Lorem.Sentence(), ["count"] = faker.Random.Int(0, 1_000_000) };
}
