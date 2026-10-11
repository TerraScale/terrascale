using System.Net;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// The document lattice of design C across gru and sjc (docs/terrabase/multi-region-replication.md, section 20): counters,
/// sets, the changes feed, SQL writes and garbage collection, E8 to E11, E14 and E15. Each is an expected failure until
/// its slice lands; the gap names the slice.
/// </summary>
public sealed class LatticeE2ETests
{
    private const string Counters = "counters and sets (R5)";
    private const string Feed = "the changes feed (R8)";
    private const string SqlWrites = "SQL writes (R9)";
    private const string Gc = "the per-field lattice (R4a) and garbage collection (R7)";
    private const string AuthUses = "counters and sets (R5) and the changes feed (R8)";

    // GC collects below (min stable - 60 s) in rounds every 60 s, so 150 s covers one collection on both regions.
    private static readonly TimeSpan GcWindow = TimeSpan.FromSeconds(150);

    [Test]
    public async Task E8_concurrent_inc_in_both_regions_sums_and_set_0_resets_expected_to_fail()
    {
        await using var cluster = await Cluster.StartAsync();
        var uri = cluster.Document(EntityIds.New().ToString());
        await Converge.SeedAsync(cluster, uri, new JsonObject { ["name"] = "counter" });
        await Expected.FailureAsync(Counters, async () =>
        {
            await Task.WhenAll(IncAsync(cluster[Cluster.Gru], uri, 5), IncAsync(cluster[Cluster.Sjc], uri, 5));
            var summed = await SettledAsync(cluster, uri, "concurrent inc");
            Expected.Check(summed.Gru!["n"]?.ToJsonString() == "10", $"five inc in each region sum to 10; {summed}");
            _ = await Docs.OpsAsync(cluster[Cluster.Gru], uri, Op("set", "n", 0));
            var reset = await SettledAsync(cluster, uri, "set 0");
            Expected.Check(reset.Gru!["n"]?.ToJsonString() == "0", $"set 0 resets the counter; {reset}");
        });
    }

    [Test]
    public async Task E9_concurrent_add_and_remove_of_one_element_add_wins_expected_to_fail()
    {
        await using var cluster = await Cluster.StartAsync();
        var uri = cluster.Document(EntityIds.New().ToString());
        await Converge.SeedAsync(cluster, uri, new JsonObject { ["tags"] = new JsonArray("x") });
        await Expected.FailureAsync(Counters, async () =>
        {
            await Task.WhenAll(
                Docs.OpsAsync(cluster[Cluster.Gru], uri, Op("add", "tags", "x")),
                Docs.OpsAsync(cluster[Cluster.Sjc], uri, Op("remove", "tags", "x")));
            var settled = await SettledAsync(cluster, uri, "add versus remove");
            Expected.Check(settled.Gru!["tags"]?.ToJsonString() == """["x"]""", $"the concurrent add keeps x; {settled}");
        });
    }

    [Test]
    public async Task E10_feed_cursor_from_gru_resumes_in_sjc_with_no_missing_event_in_per_origin_order_expected_to_fail()
    {
        await using var cluster = await Cluster.StartAsync();
        await Expected.FailureAsync(Feed, async () =>
        {
            var first = EntityIds.New().ToString();
            await Docs.PutAsync(cluster[Cluster.Gru], cluster.Document(first), new JsonObject { ["step"] = 0 });
            var head = await ChangesAsync(cluster[Cluster.Gru], cluster, cursor: null, events => events.Any(change => Id(change) == first));
            var cursor = head.First(change => Id(change.Event) == first).Cursor;
            // Ten writes after the cursor, alternating regions; each region writes its own in order.
            var writes = Enumerable.Range(1, 10).Select(step => (Id: EntityIds.New().ToString(), Region: step % 2 == 0 ? Cluster.Gru : Cluster.Sjc, Step: step)).ToArray();
            foreach (var write in writes)
            {
                await Docs.PutAsync(cluster[write.Region], cluster.Document(write.Id), new JsonObject { ["step"] = write.Step });
            }

            var position = writes.Select((write, index) => (write.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
            var resumed = await ChangesAsync(cluster[Cluster.Sjc], cluster, cursor, events => position.Keys.All(id => events.Any(change => Id(change) == id)));
            var ours = resumed.Select(change => change.Event).Where(change => position.ContainsKey(Id(change))).ToArray();
            Expected.Check(position.Keys.All(id => ours.Any(change => Id(change) == id)), "every write after the cursor is in sjc's feed");
            foreach (var origin in ours.GroupBy(change => change["origin"]?.ToJsonString()))
            {
                var order = origin.Select(change => position[Id(change)]).Distinct().ToArray();
                Expected.Check(order.SequenceEqual(order.Order()), $"origin {origin.Key} in write order; got {string.Join(',', order)}");
            }
        });
    }

    [Test]
    public async Task E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add_expected_to_fail()
    {
        await using var cluster = await Cluster.StartAsync();
        var id = EntityIds.New().ToString();
        var uri = cluster.Document(id);
        await Converge.SeedAsync(cluster, uri, new JsonObject { ["a"] = "base", ["b"] = "base", ["n"] = 0 });
        await Expected.FailureAsync(SqlWrites, async () =>
        {
            await Task.WhenAll(
                SqlAsync(cluster[Cluster.Gru], cluster, $"""UPDATE "{cluster.Collection}" SET a = 'gru' WHERE _id = '{id}'"""),
                Docs.PatchAsync(cluster[Cluster.Sjc], uri, new JsonObject { ["b"] = "sjc" }));
            var both = await SettledAsync(cluster, uri, "SQL update versus patch");
            Expected.Check(both.Gru!["a"]?.GetValue<string>() == "gru" && both.Gru["b"]?.GetValue<string>() == "sjc", $"both edits survive; {both}");
            var increment = $"""UPDATE "{cluster.Collection}" SET n = n + 1 WHERE _id = '{id}'""";
            await Task.WhenAll(SqlAsync(cluster[Cluster.Gru], cluster, increment), SqlAsync(cluster[Cluster.Sjc], cluster, increment));
            var added = await SettledAsync(cluster, uri, "SQL increments");
            Expected.Check(added.Gru!["n"]?.ToJsonString() == "2", $"n = n + 1 in both regions gives 2; {added}");
        });
    }

    // The half of E14 with a region back after GC needs a region away for the 24 h eviction, so it is R7's
    // integration test. Here: concurrent delete and patch leave no ghost fields, and GC rounds bring nothing back.
    [Test]
    public async Task E14_delete_then_gc_then_recreate_leaves_no_ghost_fields_expected_to_fail()
    {
        const int count = 20;
        await using var cluster = await Cluster.StartAsync();
        var uris = Enumerable.Range(0, count).Select(_ => cluster.Document(EntityIds.New().ToString())).ToArray();
        await Task.WhenAll(uris.Select(uri => Converge.SeedAsync(cluster, uri, new JsonObject { ["a"] = "a", ["b"] = "b" })));
        await Expected.FailureAsync(Gc, async () =>
        {
            await Task.WhenAll(uris.Select(uri => Task.WhenAll(Docs.DeleteAsync(cluster[Cluster.Gru], uri), PatchUnlessDeletedAsync(cluster[Cluster.Sjc], uri))));
            var settled = await Task.WhenAll(uris.Select(uri => Converge.UntilAsync(() => Converge.BothAsync(cluster, uri), copies => copies.Identical, "delete versus patch")));
            // A patch after the delete brings the document back with only the patched field; one before it is deleted.
            Expected.Check(settled.All(copies => copies.Gru is null || Docs.Canonical(copies.Gru) == """{"c":"c"}"""), $"no ghost fields; {string.Join("; ", settled.AsEnumerable())}");
            await Task.Delay(GcWindow);
            var collected = await Task.WhenAll(uris.Select(uri => Converge.BothAsync(cluster, uri)));
            Expected.Check(collected.Select(copies => copies.ToString()).SequenceEqual(settled.Select(copies => copies.ToString())), "nothing changed after GC rounds");
            await Task.WhenAll(uris.Select(uri => Docs.PutAsync(cluster[Cluster.Sjc], uri, new JsonObject { ["d"] = "d" })));
            foreach (var uri in uris)
            {
                var recreated = await SettledAsync(cluster, uri, "recreate");
                Expected.Check(Docs.Canonical(recreated.Gru) == """{"d":"d"}""", $"the recreated document has only its new field; {recreated}");
            }
        });
    }

    // The storage half of E15: two refreshes of one token in gru and sjc within 1 s each add a use, and both regions'
    // feeds then show the token with two uses, which ts-auth's watcher revokes the family on. R12 adds the ts-auth half.
    [Test]
    public async Task E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds_expected_to_fail()
    {
        await using var cluster = await Cluster.StartAsync();
        var token = EntityIds.New().ToString();
        var uri = cluster.Document(token);
        await Converge.SeedAsync(cluster, uri, new JsonObject { ["family"] = EntityIds.New().ToString() });
        await Expected.FailureAsync(AuthUses, async () =>
        {
            await Task.WhenAll(
                Docs.OpsAsync(cluster[Cluster.Gru], uri, Op("add", "uses", EntityIds.New().ToString())),
                Docs.OpsAsync(cluster[Cluster.Sjc], uri, Op("add", "uses", EntityIds.New().ToString())));
            foreach (var region in new[] { Cluster.Gru, Cluster.Sjc })
            {
                _ = await ChangesAsync(cluster[region], cluster, cursor: null, events => events.Any(change => Id(change) == token && change["document"]?["uses"]?.AsArray().Count == 2));
            }
        });
    }

    private static async Task IncAsync(HttpClient client, Uri uri, int times)
    {
        for (var i = 0; i < times; i++)
        {
            _ = await Docs.OpsAsync(client, uri, Op("inc", "n", 1));
        }
    }

    // A patch that finds the document already deleted (404) is the delete winning, not a failure.
    private static async Task PatchUnlessDeletedAsync(HttpClient client, Uri uri)
    {
        try
        {
            await Docs.PatchAsync(client, uri, new JsonObject { ["c"] = "c" });
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static Task<JsonNode?> SqlAsync(HttpClient client, Cluster cluster, string sql) =>
        Docs.PostAsync(client, new Uri($"{cluster.DatabasePath}/sql", UriKind.Relative), new JsonObject { ["sql"] = sql });

    // An op of section 13: inc takes "by", the others "value".
    private static JsonObject Op(string op, string field, JsonNode value) =>
        new() { ["op"] = op, ["field"] = field, [op == "inc" ? "by" : "value"] = value };

    private static Task<RegionCopies> SettledAsync(Cluster cluster, Uri uri, string what) =>
        Converge.UntilAsync(() => Converge.BothAsync(cluster, uri), copies => copies.Identical && copies.Gru is not null, what);

    private static string Id(JsonNode change) => change["id"]?.GetValue<string>() ?? "";

    // The change events of this test's collection on one region's feed, from cursor (the start of retention when
    // null), until enough accepts them; each with the cursor after it (the SSE id).
    private static async Task<List<(string Cursor, JsonNode Event)>> ChangesAsync(HttpClient client, Cluster cluster, string? cursor, Func<IEnumerable<JsonNode>, bool> enough)
    {
        using var deadline = new CancellationTokenSource(Converge.Deadline);
        var query = $"collections={cluster.Collection}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{cluster.DatabasePath}/changes?{query}", UriKind.Relative));
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await Docs.EnsureSuccessAsync(response);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var events = new List<(string Cursor, JsonNode Event)>();
        string? id = null, name = null, data = null;
        while (!enough(events.Select(change => change.Event)) && await reader.ReadLineAsync(deadline.Token) is { } line)
        {
            if (line.Length == 0)
            {
                if (name == "change" && id is not null && data is not null)
                {
                    events.Add((id, JsonNode.Parse(data)!));
                }

                (id, name, data) = (null, null, null);
            }
            else if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                id = line[3..].Trim();
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data += line[5..].Trim();
            }
        }

        return events;
    }
}
