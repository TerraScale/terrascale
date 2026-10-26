using System.Net;
using System.Text.Json.Nodes;
using TerraBase.Primitives;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.E2E;

/// <summary>
/// The document lattice of design C across gru and sjc (docs/terrabase/multi-region-replication.md, section 20): counters,
/// sets, the changes feed, SQL writes and garbage collection, E8 to E11, E14 and E15. What each waits on is in
/// <see cref="Gaps"/>.
/// </summary>
public sealed class LatticeE2ETests
{
    // GC collects below (min stable - 60 s) in rounds every 60 s, so 150 s covers one collection on both regions.
    private static readonly TimeSpan GcWindow = TimeSpan.FromSeconds(150);

    [Test]
    [Scenario(Nodes.Cluster)]
    public async Task E8_concurrent_inc_in_both_regions_sums_and_set_0_resets()
    {
        await Gaps.RunAsync("E8", async () =>
        {
            await using var cluster = await Cluster.StartAsync();
            var uri = cluster.Document(EntityIds.New().ToString());
            await Converge.SeedAsync(cluster, uri, new JsonObject { ["name"] = "counter" });
            await Task.WhenAll(IncAsync(cluster[Cluster.Gru], uri, 5), IncAsync(cluster[Cluster.Sjc], uri, 5));
            // Two regions can agree on a sum while increments are still in flight: settled means both hold the final sum.
            _ = await SettledAsync(cluster, uri, "five inc in each region sum to 10", copies => copies.Gru!["n"]?.ToJsonString() == "10");
            _ = await Docs.OpsAsync(cluster[Cluster.Gru], uri, Op("set", "n", 0));
            _ = await SettledAsync(cluster, uri, "set 0 resets the counter", copies => copies.Gru!["n"]?.ToJsonString() == "0");
        });
    }

    [Test]
    [Scenario(Nodes.Cluster)]
    public async Task E9_concurrent_add_and_remove_of_one_element_add_wins()
    {
        await Gaps.RunAsync("E9", async () =>
        {
            await using var cluster = await Cluster.StartAsync();
            var uri = cluster.Document(EntityIds.New().ToString());
            await Converge.SeedAsync(cluster, uri, new JsonObject { ["tags"] = new JsonArray("x") });
            await Task.WhenAll(
                Docs.OpsAsync(cluster[Cluster.Gru], uri, Op("add", "tags", "x")),
                Docs.OpsAsync(cluster[Cluster.Sjc], uri, Op("remove", "tags", "x")));
            var settled = await SettledAsync(cluster, uri, "add versus remove");
            Expected.Check(settled.Gru!["tags"]?.ToJsonString() == """["x"]""", $"the concurrent add keeps x; {settled}");
        });
    }

    // gru, the cursor's issuer, stops: sjc must serve the resume itself, from its own copy of gru's writes (the cross-region
    // proof), whether its membership no longer lists gru (no ts-replay) or the edge's replay to gru failed and fell back.
    // Local nodes only: the deploy run never stops a region; the ts-orch run with a stopped issuer is ChangesFeedE2ETests'
    // orch-outage scenario.
    [Test]
    [Category("local-only")]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task E10_feed_cursor_from_gru_resumes_in_sjc_with_no_missing_event_in_per_origin_order()
    {
        await Gaps.RunAsync("E10", async () =>
        {
            await using var cluster = await Cluster.StartLocalAsync();
            var (cursor, position) = await CursorThenWritesAsync(cluster, cluster[Cluster.Gru]);
            foreach (var id in position.Keys)
            {
                _ = await Converge.UntilAsync(() => Docs.ReadAsync(cluster[Cluster.Sjc], cluster.Document(id)), body => body is not null, "every write on sjc before gru stops");
            }

            await cluster.StopAsync(Cluster.Gru);
            using var sjc = await cluster.ViaAsync(Cluster.Sjc, Cluster.Sjc);
            await ResumedInOrderAsync(sjc, Cluster.Sjc, cluster, cursor, position);
            Expected.Check(!cluster.Replays.Any(replay => replay is { Edge: Cluster.Sjc, Target: Cluster.Gru, FellBack: false } && replay.PathAndQuery.Contains("cursor=", StringComparison.Ordinal)),
                $"sjc served the resume itself, never answered by the stopped gru; replays: {string.Join("; ", cluster.Replays)}");
        }, local: true);
    }

    // The issuer stays up: the edge replays the resume from sjc to gru, and only gru's answer counts.
    [Test]
    [Scenario(Nodes.Cluster)]
    public async Task E10_feed_cursor_from_gru_presented_in_sjc_is_replayed_to_gru_with_no_missing_event_in_per_origin_order()
    {
        await Gaps.RunAsync("E10", async () =>
        {
            await using var cluster = await Cluster.StartAsync();
            using var gru = await cluster.ViaAsync(Cluster.Gru, Cluster.Gru);
            var (cursor, position) = await CursorThenWritesAsync(cluster, gru);
            using var sjc = await cluster.ViaAsync(Cluster.Sjc, Cluster.Gru);
            await ResumedInOrderAsync(sjc, Cluster.Gru, cluster, cursor, position);
        });
    }

    // A cursor from gru's feed read through `issuer`, then ten writes after it alternating regions (each region writes its own
    // in order); returns the cursor and each write's position.
    private static async Task<(string Cursor, Dictionary<string, int> Position)> CursorThenWritesAsync(Cluster cluster, HttpClient issuer)
    {
        var first = EntityIds.New().ToString();
        await Docs.PutAsync(cluster[Cluster.Gru], cluster.Document(first), new JsonObject { ["step"] = 0 });
        var head = await ChangesAsync(issuer, Cluster.Gru, cluster, cursor: null, events => events.Any(change => Id(change) == first));
        var cursor = head.First(change => Id(change.Event) == first).Cursor;
        _ = await Converge.UntilAsync(() => Docs.ReadAsync(cluster[Cluster.Sjc], cluster.Document(first)),
            body => body?["step"]?.GetValue<int>() == 0, "the cursor's prefix on sjc before resuming");
        var writes = Enumerable.Range(1, 10).Select(step => (Id: EntityIds.New().ToString(), Region: step % 2 == 0 ? Cluster.Gru : Cluster.Sjc, Step: step)).ToArray();
        foreach (var write in writes)
        {
            await Docs.PutAsync(cluster[write.Region], cluster.Document(write.Id), new JsonObject { ["step"] = write.Step });
        }

        return (cursor, writes.Select((write, index) => (write.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index));
    }

    private static async Task ResumedInOrderAsync(HttpClient resume, string expectedRegion, Cluster cluster, string cursor, Dictionary<string, int> position)
    {
        var resumed = await ChangesAsync(resume, expectedRegion, cluster, cursor, events => position.Keys.All(id => events.Any(change => Id(change) == id)));
        var ours = resumed.Select(change => change.Event).Where(change => position.ContainsKey(Id(change))).ToArray();
        Expected.Check(position.Keys.All(id => ours.Any(change => Id(change) == id)), "every write after the cursor is in the resumed feed");
        foreach (var origin in ours.GroupBy(change => change["origin"]?.ToJsonString()))
        {
            var order = origin.Select(change => position[Id(change)]).Distinct().ToArray();
            Expected.Check(order.SequenceEqual(order.Order()), $"origin {origin.Key} in write order; got {string.Join(',', order)}");
        }
    }

    [Test]
    [Scenario(Nodes.Cluster)]
    public async Task E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add()
    {
        await Gaps.RunAsync("E11", async () =>
        {
            await using var cluster = await Cluster.StartAsync();
            var id = EntityIds.New().ToString();
            var uri = new Uri($"{cluster.DatabasePath}/collections/e11/documents/by-key?id={id}", UriKind.Relative);
            _ = await SqlAsync(cluster, Cluster.Gru, "CREATE TABLE e11 (id TEXT PRIMARY KEY, a TEXT, b TEXT, n INTEGER NOT NULL)");
            _ = await SqlAsync(cluster, Cluster.Gru, $"INSERT INTO e11 (id, a, b, n) VALUES ('{id}', 'base', 'base', 0)");
            _ = await SettledAsync(cluster, uri, "SQL table and seed row", copies => copies.Gru?["n"]?.ToJsonString() == "0");
            await Task.WhenAll(
                SqlAsync(cluster, Cluster.Gru, $"UPDATE e11 SET a = 'gru' WHERE id = '{id}'"),
                Docs.PatchAsync(cluster[Cluster.Sjc], uri, new JsonObject { ["b"] = "sjc" }));
            var both = await SettledAsync(cluster, uri, "SQL update versus patch",
                copies => copies.Gru?["a"]?.GetValue<string>() == "gru" && copies.Gru["b"]?.GetValue<string>() == "sjc");
            Expected.Check(both.Gru!["a"]?.GetValue<string>() == "gru" && both.Gru["b"]?.GetValue<string>() == "sjc", $"both edits survive; {both}");
            var increment = $"UPDATE e11 SET n = n + 1 WHERE id = '{id}'";
            await Task.WhenAll(SqlAsync(cluster, Cluster.Gru, increment), SqlAsync(cluster, Cluster.Sjc, increment));
            var added = await SettledAsync(cluster, uri, "SQL increments", copies => copies.Gru?["n"]?.ToJsonString() == "2");
            Expected.Check(added.Gru!["n"]?.ToJsonString() == "2", $"n = n + 1 in both regions gives 2; {added}");
        });
    }

    // The half of E14 with a region back after GC needs a region away for the 24 h eviction, so it is R7's
    // integration test. Here: concurrent delete and patch leave no ghost fields, GC rounds collect the tombstones of the
    // documents that stayed deleted (read from each stopped region's file: the proof that GC ran) and bring nothing back.
    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task E14_delete_then_gc_then_recreate_leaves_no_ghost_fields()
    {
        await Gaps.RunAsync("E14", async () =>
        {
            const int count = 20;
            await using var cluster = await Cluster.StartLocalAsync();
            var ids = Enumerable.Range(0, count).Select(_ => EntityIds.New().ToString()).ToArray();
            var uris = ids.Select(cluster.Document).ToArray();
            await Task.WhenAll(uris.Select(uri => Converge.SeedAsync(cluster, uri, new JsonObject { ["a"] = "a", ["b"] = "b" })));
            await Task.WhenAll(uris.Select(uri => Task.WhenAll(Docs.DeleteAsync(cluster[Cluster.Gru], uri), PatchUnlessDeletedAsync(cluster[Cluster.Sjc], uri))));
            var settled = await Task.WhenAll(uris.Select(uri => Converge.UntilAsync(() => Converge.BothAsync(cluster, uri), copies => copies.Identical, "delete versus patch")));
            // A patch after the delete brings the document back with only the patched field; one before it is deleted.
            Expected.Check(settled.All(copies => copies.Gru is null || Docs.Canonical(copies.Gru) == """{"c":"c"}"""), $"no ghost fields; {string.Join("; ", settled.AsEnumerable())}");
            await Task.Delay(GcWindow);
            var collected = await Task.WhenAll(uris.Select(uri => Converge.BothAsync(cluster, uri)));
            Expected.Check(collected.Select(copies => copies.ToString()).SequenceEqual(settled.Select(copies => copies.ToString())), "nothing changed after GC rounds");
            var deleted = ids.Zip(settled).Where(pair => pair.Second.Gru is null).Select(pair => pair.First).ToArray();
            foreach (var region in cluster.Regions)
            {
                await cluster.StopAsync(region);
                var tombstones = await TombstonesAsync(cluster.DatabaseFile(region), cluster.Collection, deleted);
                await cluster.StartAgainAsync(region);
                Expected.Check(tombstones == 0, $"GC collects the tombstones of deleted documents; {tombstones} of {deleted.Length} left on {region}");
            }

            await Task.WhenAll(uris.Select(uri => Docs.PutAsync(cluster[Cluster.Sjc], uri, new JsonObject { ["d"] = "d" })));
            foreach (var uri in uris)
            {
                var recreated = await SettledAsync(cluster, uri, "recreate");
                Expected.Check(Docs.Canonical(recreated.Gru) == """{"d":"d"}""", $"the recreated document has only its new field; {recreated}");
            }
        }, local: true);
    }

    // The storage half of E15: two refreshes of one token in gru and sjc within 1 s each add a use, and both regions'
    // feeds then show the token with two uses, which ts-auth's watcher revokes the family on. R12 adds the ts-auth half.
    [Test]
    [Scenario(Nodes.Cluster)]
    public async Task E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds()
    {
        await Gaps.RunAsync("E15", async () =>
        {
            await using var cluster = await Cluster.StartAsync();
            var token = EntityIds.New().ToString();
            var uri = cluster.Document(token);
            await Converge.SeedAsync(cluster, uri, new JsonObject { ["family"] = EntityIds.New().ToString() });
            await Task.WhenAll(
                Docs.OpsAsync(cluster[Cluster.Gru], uri, Op("add", "uses", EntityIds.New().ToString())),
                Docs.OpsAsync(cluster[Cluster.Sjc], uri, Op("add", "uses", EntityIds.New().ToString())));
            foreach (var region in new[] { Cluster.Gru, Cluster.Sjc })
            {
                _ = await ChangesAsync(cluster[region], region, cluster, cursor: null, events => events.Any(change => Id(change) == token && change["document"]?["uses"]?.AsArray().Count == 2));
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

    // The tombstones (dead state rows) of `ids` in the collection, read from a stopped region's database file.
    private static async Task<long> TombstonesAsync(string file, string collection, string[] ids)
    {
        await using var connection = new SqliteConnection(TestMasterKey.ConnectionString(file));
        await connection.OpenAsync();
        long tombstones = 0;
        foreach (var id in ids)
        {
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM _tb_state WHERE collection = $collection AND id = $id AND live = 0";
            _ = count.Parameters.AddWithValue("$collection", collection);
            _ = count.Parameters.AddWithValue("$id", id);
            tombstones += (long)(await count.ExecuteScalarAsync())!;
        }

        return tombstones;
    }

    // Relational writes use the document API credential and the current /sql/execute statement envelope.
    private static async Task<JsonNode?> SqlAsync(Cluster cluster, string region, string sql)
    {
        return await Docs.PostAsync(cluster[region], new Uri($"{cluster.DatabasePath}/sql/execute", UriKind.Relative),
            new JsonObject { ["statements"] = new JsonArray(new JsonObject { ["sql"] = sql }) });
    }

    // An op of section 13: inc takes "by", the others "value".
    private static JsonObject Op(string op, string field, JsonNode value) =>
        new() { ["op"] = op, ["path"] = "/" + field, [op == "inc" ? "by" : "value"] = value };

    private static Task<RegionCopies> SettledAsync(Cluster cluster, Uri uri, string what) =>
        SettledAsync(cluster, uri, what, _ => true);

    // Both regions hold the same live document and it is the expected one (`done`); a convergence that never gets there
    // fails with `what`.
    private static Task<RegionCopies> SettledAsync(Cluster cluster, Uri uri, string what, Func<RegionCopies, bool> done) =>
        Converge.UntilAsync(() => Converge.BothAsync(cluster, uri), copies => copies.Identical && copies.Gru is not null && done(copies), what);

    private static string Id(JsonNode change) => change["id"]?.GetValue<string>() ?? "";

    // The change events of this test's collection on one region's feed, from cursor (the start of retention when
    // null), until enough accepts them; each with the cursor after it (the SSE id).
    private static async Task<List<(string Cursor, JsonNode Event)>> ChangesAsync(HttpClient client, string expectedRegion, Cluster cluster, string? cursor, Func<IEnumerable<JsonNode>, bool> enough)
    {
        using var deadline = new CancellationTokenSource(Converge.Deadline);
        var query = $"collections={cluster.Collection}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{cluster.DatabasePath}/changes?{query}", UriKind.Relative));
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await Docs.EnsureSuccessAsync(response);
        var answered = response.Headers.TryGetValues("X-TerraBase-Region", out var values) ? string.Join(',', values) : "(missing)";
        Expected.Check(answered == expectedRegion, $"the changes feed answers from {expectedRegion}; got {answered}");
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
