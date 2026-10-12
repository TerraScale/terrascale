using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// Multi-writer convergence with regions as separate OS processes (<see cref="RegionProcesses"/>), in all six directions.
/// Writers are made concurrent by isolation, not by racing: in each of three windows exactly one region runs and the other
/// two are killed, so no writer can see another's writes, and the windows' order on one host clock fixes the HLC order.
/// The lattice's outcome (docs/terrabase/multi-region-replication.md, section 4) is therefore known exactly, and every
/// region must hold it: bodies, field values, deletes, counter sums, set elements, counts and digests. Every scenario runs
/// twice, once as single requests and once inside one mixed transaction per window. A barrier is one sentinel write per
/// running region, read by every region and acknowledged on each origin's outbound streams (Matrix.BarrierAsync).
/// Two controls run in every test: with the last writer kept down, the scenarios it wrote must not yet hold
/// (the checks see a blocked origin), and each scenario's wrong outcome (the earlier writer winning, a ghost field, a
/// resurrected or lost document, a lost increment, remove beating a concurrent add) must differ from what every region holds.
/// </summary>
public sealed class MultiWriterMatrixTests
{
    // Writes per region in the all-up counter scenario: region k adds 10^k each time, so the sum shows each region's count.
    private const int AllUpIncrements = 5;

    private static readonly string[] All = [Cluster.Gru, Cluster.Sjc, Cluster.Fra];
    private static readonly string[] Modes = ["single", "batch"];

    [Test]
    [Arguments(Cluster.Gru, Cluster.Sjc)]
    [Arguments(Cluster.Gru, Cluster.Fra)]
    [Arguments(Cluster.Sjc, Cluster.Gru)]
    [Arguments(Cluster.Sjc, Cluster.Fra)]
    [Arguments(Cluster.Fra, Cluster.Gru)]
    [Arguments(Cluster.Fra, Cluster.Sjc)]
    public async Task Isolated_writers_converge_to_the_exact_lattice_state_in_every_region(string first, string second)
    {
        Skip.When(Cluster.FlyRequested, "runs local node processes");
        var third = All.Single(region => region != first && region != second);
        await using var regions = await RegionProcesses.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        var matrix = new Matrix(regions, $"{first}->{second}->{third}");
        var cases = Modes.SelectMany(mode => Scenarios(first, second, third).Select(scenario => new Case(mode, scenario, EntityIds.New().ToString()))).ToList();
        var recreated = Modes.Select(mode => new Case(mode, Recreate(second), EntityIds.New().ToString())).ToList();
        var counted = Modes.Select(mode => new Case(mode, AllUpCounter(), EntityIds.New().ToString())).ToList();

        foreach (var item in cases.Concat(recreated).Concat(counted))
        {
            await Docs.PutAsync(regions[first], regions.Document(item.Id), item.Scenario.Seed);
        }

        await matrix.BarrierAsync("seeded", All);

        // Three windows, one region each: first, then second, then third.
        string[] order = [first, second, third];
        for (var window = 0; window < order.Length; window++)
        {
            foreach (var region in All.Where(region => region != order[window]))
            {
                await regions.KillAsync(region);
            }

            if (window > 0)
            {
                await regions.StartAgainAsync(order[window]);
            }

            foreach (var mode in Modes)
            {
                await matrix.WriteAsync(order[window], mode, [.. cases.Where(item => item.Mode == mode && item.Scenario.Writes[window] is not null).Select(item => (item.Id, item.Scenario.Writes[window]!))]);
            }

            matrix.Mark($"window {window} written by {order[window]}");
        }

        // Control: the third writer's origin is blocked (its process is down). First and second hold each other's writes,
        // so the two-writer scenarios are exact there and every three-writer scenario must still differ.
        await regions.KillAsync(third);
        await regions.StartAgainAsync(first);
        await regions.StartAgainAsync(second);
        await matrix.BarrierAsync($"{third} blocked", [first, second]);
        await matrix.CheckAsync([first, second], cases.Where(item => !item.Scenario.ThreeWriters));
        await matrix.ControlBlockedAsync([first, second], third, cases.Where(item => item.Scenario.ThreeWriters));

        await regions.StartAgainAsync(third);
        await matrix.BarrierAsync("windows drained", All);
        await matrix.CheckAsync(All, cases);
        await matrix.ControlWrongAsync(All, cases);

        // All up: a delete that every region has seen, then a re-create of the same id in another region; and increments
        // from every region at once.
        foreach (var item in recreated)
        {
            await matrix.WriteAsync(first, item.Mode, [(item.Id, Delete())]);
            foreach (var region in All)
            {
                _ = await Converge.UntilAsync(() => Docs.ReadAsync(regions[region], regions.Document(item.Id)), document => document is null, $"{item.Mode} delete of the re-created id on {region}");
            }

            await matrix.WriteAsync(second, item.Mode, [(item.Id, Put(new JsonObject { ["r"] = second }))]);
        }

        await Task.WhenAll(All.Select((region, index) => Task.Run(async () =>
        {
            for (var i = 0; i < AllUpIncrements; i++)
            {
                foreach (var item in counted)
                {
                    await matrix.WriteAsync(region, item.Mode, [(item.Id, Ops(Op("inc", "n", (int)Math.Pow(10, index))))]);
                }
            }
        })));
        matrix.Mark("all-up writes");
        await matrix.BarrierAsync("all-up drained", All);
        var everything = cases.Concat(recreated).Concat(counted).ToList();
        await matrix.CheckAsync(All, everything);
        await matrix.ControlWrongAsync(All, [.. recreated, .. counted]);

        // Resurrection: every region restarts, its streams resend from what peers acknowledged; nothing deleted comes back.
        foreach (var region in All)
        {
            await regions.KillAsync(region);
        }

        foreach (var region in All)
        {
            await regions.StartAgainAsync(region);
        }

        await matrix.BarrierAsync("restarted", All);
        await matrix.CheckAsync(All, everything);
        await matrix.CheckListingsAsync(All, everything);
        Console.WriteLine($"matrix {matrix.Name}: PASS, {everything.Count} documents exact in gru, sjc and fra after {matrix.Elapsed} ms");
    }

    // x, y and z write in that order: y's writes have larger versions than x's, z's than both.
    private static IEnumerable<Scenario> Scenarios(string x, string y, string z)
    {
        // Same field: the larger version wins; the wrong outcome is the earlier writer winning.
        yield return new("same-field-2", Seed(), [Patch(new() { ["v"] = x }), Patch(new() { ["v"] = y }), null], Kept(("v", y)), Kept(("v", x)));
        yield return new("same-field-3", Seed(), [Patch(new() { ["v"] = x }), Patch(new() { ["v"] = y }), Patch(new() { ["v"] = z })], Kept(("v", z)), Kept(("v", y)));
        // A later PUT replaces the document: no field of the earlier PUT survives.
        yield return new("put-replaces-2", Seed(), [Put(new() { ["v"] = x, ["only"] = x }), Put(new() { ["v"] = y }), null], new() { ["v"] = y }, new() { ["only"] = x, ["v"] = y });
        // Different fields: every writer's field survives; the wrong outcome is whole-document last-writer-wins.
        yield return new("different-fields-2", Letters(), [Patch(new() { ["a"] = x }), Patch(new() { ["b"] = y }), null], new() { ["a"] = x, ["b"] = y, ["c"] = 0 }, new() { ["a"] = 0, ["b"] = y, ["c"] = 0 });
        yield return new("different-fields-3", Letters(), [Patch(new() { ["a"] = x }), Patch(new() { ["b"] = y }), Patch(new() { ["c"] = z })],
            new() { ["a"] = x, ["b"] = y, ["c"] = z }, new() { ["a"] = 0, ["b"] = 0, ["c"] = z });
        // Delete versus a concurrent edit: the later version wins at document level, and an edit after the delete brings
        // the document back with only its own fields (no ghost of the seed).
        yield return new("delete-then-patch", Seed(), [Delete(), Patch(new() { ["v"] = y }), null], new() { ["v"] = y }, Kept(("v", y)));
        yield return new("patch-then-delete", Seed(), [Patch(new() { ["v"] = x }), Delete(), null], null, Kept(("v", x)));
        yield return new("delete-then-put", Seed(), [Delete(), Put(new() { ["w"] = y }), null], new() { ["w"] = y }, null);
        yield return new("put-then-delete", Seed(), [Put(new() { ["w"] = x }), Delete(), null], null, new() { ["w"] = x });
        yield return new("patch-delete-patch-3", Seed(), [Patch(new() { ["v"] = x }), Delete(), Patch(new() { ["z"] = z })], new() { ["z"] = z }, Kept(("v", x), ("z", z)));
        yield return new("patch-patch-delete-3", Seed(), [Patch(new() { ["v"] = x }), Patch(new() { ["w"] = y }), Delete()], null, Kept(("v", x), ("w", y)));
        // A concurrent inc lands in the epoch the delete closed, so the delete wins even though the inc is later.
        yield return new("delete-then-inc", Named(null), [Delete(), Ops(Op("inc", "n", 10)), null], null, Named(10));
        // Counters: 1 + 10 + 100, so a lost or doubled increment shows in its own digit.
        yield return new("counter-3", Named(null), [Ops(Op("inc", "n", 1)), Ops(Op("inc", "n", 10)), Ops(Op("inc", "n", 100))], Named(111), Named(100));
        // Sets: x removed by the first writer and added concurrently by the second stays (add wins); r removed by the
        // third with no concurrent add goes.
        yield return new("set-3", Tags("x", "r"),
            [Ops(Op("add", "tags", x), Op("remove", "tags", "x")), Ops(Op("add", "tags", y), Op("add", "tags", "x")), Ops(Op("add", "tags", z), Op("remove", "tags", "r"))],
            Tags(x, y, z, "x"), Tags(x, y, z));
    }

    // Deleted while every region was up, then re-created in another region: only the new body.
    private static Scenario Recreate(string by) =>
        new("delete-then-recreate", Seed(), [null, null, null], new() { ["r"] = by }, new() { ["keep"] = "k", ["r"] = by, ["v"] = "seed" });

    private static Scenario AllUpCounter() =>
        new("all-up-counter", new() { ["name"] = "c" }, [null, null, null], new() { ["name"] = "c", ["n"] = 111 * AllUpIncrements }, new() { ["name"] = "c", ["n"] = (111 * AllUpIncrements) - 1 });

    private static JsonObject Seed() => new() { ["v"] = "seed", ["keep"] = "k" };

    // The seed with fields set or added.
    private static JsonObject Kept(params (string Field, JsonNode Value)[] fields)
    {
        var body = Seed();
        foreach (var (field, value) in fields)
        {
            body[field] = value;
        }

        return body;
    }

    private static JsonObject Letters() => new() { ["a"] = 0, ["b"] = 0, ["c"] = 0 };

    private static JsonObject Tags(params string[] tags) => new() { ["tags"] = new JsonArray([.. tags.Order(StringComparer.Ordinal).Select(tag => (JsonNode)tag)]) };

    private static JsonObject Named(int? n) => n is { } value ? new() { ["name"] = "c", ["n"] = value } : new() { ["name"] = "c" };

    private static Write Put(JsonObject body) => new("put", body);

    private static Write Patch(JsonObject merge) => new("patch", merge);

    private static Write Delete() => new("delete", null);

    private static Write Ops(params JsonObject[] ops) => new("ops", new JsonArray([.. ops]));

    // An op of section 13: inc takes "by", the others "value".
    private static JsonObject Op(string op, string field, JsonNode value) =>
        new() { ["op"] = op, ["path"] = "/" + field, [op == "inc" ? "by" : "value"] = value };

    /// <summary>One write of a scenario: put, patch (merge), delete, or ops (counter and set ops).</summary>
    private sealed record Write(string Kind, JsonNode? Body);

    /// <summary>
    /// A document's story: its seed, its write in each window (null: none), the state every region must hold (null:
    /// absent), and the wrong state a broken property would leave (null: absent).
    /// </summary>
    private sealed record Scenario(string Name, JsonObject Seed, Write?[] Writes, JsonObject? Expected, JsonObject? Wrong)
    {
        public bool ThreeWriters => Writes[2] is not null;
    }

    /// <summary>An origin's stream to one region: the highest change id it read, and the highest the region acknowledged.</summary>
    private readonly record struct Outbound(long Head, long Acknowledged)
    {
        public override string ToString() => $"{Head}/{Acknowledged}";
    }

    private sealed record Case(string Mode, Scenario Scenario, string Id)
    {
        public string Label => $"{Scenario.Name}/{Mode}";
    }

    /// <summary>One test's cluster, with its barriers, writes, checks and timings.</summary>
    private sealed class Matrix(RegionProcesses regions, string name)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<string, JsonObject> _sentinels = [with(StringComparer.Ordinal)];

        public string Name { get; } = name;

        public long Elapsed => _clock.ElapsedMilliseconds;

        public void Mark(string what) => Console.WriteLine($"matrix {Name}: {what} at {Elapsed} ms");

        // Single requests one by one, or the whole set as one transaction.
        public async Task WriteAsync(string region, string mode, IReadOnlyList<(string Id, Write Write)> writes)
        {
            if (writes.Count == 0)
            {
                return;
            }

            var client = regions[region];
            if (mode == "batch")
            {
                var ops = new JsonArray([.. writes.Select(write => (JsonNode)TransactionOp(write.Id, write.Write))]);
                _ = await Docs.PostAsync(client, new Uri($"{regions.DatabasePath}/transactions", UriKind.Relative), new JsonObject { ["ops"] = ops });
                return;
            }

            foreach (var (id, write) in writes)
            {
                var uri = regions.Document(id);
                switch (write.Kind)
                {
                    case "put":
                        await Docs.PutAsync(client, uri, write.Body!.DeepClone());
                        break;
                    case "patch":
                        await Docs.PatchAsync(client, uri, write.Body!.DeepClone());
                        break;
                    case "delete":
                        await Docs.DeleteAsync(client, uri);
                        break;
                    default:
                        _ = await Docs.OpsAsync(client, uri, [.. write.Body!.AsArray().Select(op => op!.DeepClone().AsObject())]);
                        break;
                }
            }
        }

        // A barrier that does not depend on the order rows are applied in (a stream ships in commit order, but anti-entropy
        // and refills do not): each region in up commits a sentinel, every region reads every sentinel, and each origin's
        // GET /replication shows, for every other region in up, a head read after the sentinel was committed (above the head
        // before it) that the region has acknowledged. The change-log head itself is not served, so "read after" is
        // "advanced past the head seen before the sentinel"; the sentinel read covers a read already in flight then.
        public async Task BarrierAsync(string phase, string[] up)
        {
            Dictionary<string, Dictionary<string, Outbound>> before = [with(StringComparer.Ordinal)];
            foreach (var origin in up)
            {
                before[origin] = await StreamsAsync(origin);
            }

            var marks = up.ToDictionary(region => region, _ => EntityIds.New().ToString(), StringComparer.Ordinal);
            foreach (var (region, id) in marks)
            {
                var body = new JsonObject { ["barrier"] = phase, ["region"] = region };
                await Docs.PutAsync(regions[region], regions.Document(id), body.DeepClone());
                _sentinels[id] = body;
            }

            foreach (var reader in up)
            {
                foreach (var (writer, id) in marks)
                {
                    _ = await Converge.UntilAsync(() => Docs.ReadAsync(regions[reader], regions.Document(id)), document => document is not null, $"{Name} {phase}: {writer}'s sentinel on {reader}");
                }
            }

            var acknowledged = new List<string>();
            foreach (var origin in up)
            {
                var receivers = up.Where(region => region != origin).ToList();
                var streams = await Converge.UntilAsync(() => StreamsAsync(origin), now => receivers.All(receiver =>
                    now.TryGetValue(receiver, out var stream) && stream.Head > before[origin].GetValueOrDefault(receiver).Head && stream.Acknowledged >= stream.Head),
                    $"{Name} {phase}: {origin}'s sentinel acknowledged by {string.Join(',', receivers)}; before {string.Join(' ', before[origin])}");
                acknowledged.AddRange(receivers.Select(receiver => $"{origin}->{receiver} {before[origin].GetValueOrDefault(receiver).Head}..{streams[receiver].Head} acked {streams[receiver].Acknowledged}"));
            }

            Mark($"barrier '{phase}' on {string.Join(',', up)}: {string.Join("; ", acknowledged)}");
        }

        // One read per document and region, after a barrier: no polling, the state must already be exact.
        public async Task CheckAsync(string[] up, IEnumerable<Case> cases)
        {
            var list = cases.ToList();
            var wrong = new List<string>();
            foreach (var region in up)
            {
                foreach (var item in list)
                {
                    var held = Docs.Canonical(await Docs.ReadAsync(regions[region], regions.Document(item.Id)));
                    var expected = Docs.Canonical(item.Scenario.Expected);
                    if (held != expected)
                    {
                        wrong.Add($"{region} {item.Label}: expected {expected}, holds {held}");
                    }
                }
            }

            Mark($"checked {list.Count} documents on {string.Join(',', up)}: {wrong.Count} wrong");
            Expected.Check(wrong.Count == 0, $"{Name}: {wrong.Count} documents differ from the lattice outcome:\n{string.Join('\n', wrong)}");
        }

        // While blocked's writes have not reached the others, each scenario it wrote must differ there.
        public async Task ControlBlockedAsync(string[] up, string blocked, IEnumerable<Case> cases)
        {
            var list = cases.ToList();
            var undetected = new List<string>();
            foreach (var region in up)
            {
                foreach (var item in list)
                {
                    if (Docs.Canonical(await Docs.ReadAsync(regions[region], regions.Document(item.Id))) == Docs.Canonical(item.Scenario.Expected))
                    {
                        undetected.Add($"{region} {item.Label}");
                    }
                }
            }

            Mark($"control: {blocked} blocked, {list.Count * up.Length - undetected.Count}/{list.Count * up.Length} checks caught it");
            Expected.Check(undetected.Count == 0, $"{Name}: with {blocked} down these already held the full outcome, so their check proves nothing: {string.Join(", ", undetected)}");
        }

        // Each scenario's wrong outcome must differ from what every region holds.
        public async Task ControlWrongAsync(string[] up, IEnumerable<Case> cases)
        {
            var list = cases.ToList();
            var undetected = new List<string>();
            foreach (var region in up)
            {
                foreach (var item in list)
                {
                    if (Docs.Canonical(await Docs.ReadAsync(regions[region], regions.Document(item.Id))) == Docs.Canonical(item.Scenario.Wrong))
                    {
                        undetected.Add($"{region} {item.Label}");
                    }
                }
            }

            Mark($"control: wrong outcomes rejected {list.Count * up.Length - undetected.Count}/{list.Count * up.Length}");
            Expected.Check(undetected.Count == 0, $"{Name}: these hold the wrong outcome: {string.Join(", ", undetected)}");
        }

        // Every region lists exactly the live documents: the scenarios that end present and the sentinels.
        public async Task CheckListingsAsync(string[] up, IEnumerable<Case> cases)
        {
            var live = cases.Where(item => item.Scenario.Expected is not null).Select(item => (item.Id, Docs.Canonical(item.Scenario.Expected)))
                .Concat(_sentinels.Select(sentinel => (sentinel.Key, Docs.Canonical(sentinel.Value)))).ToList();
            var expected = new Listing(live.Count, Docs.Digest(live));
            var listings = await Task.WhenAll(up.Select(async region => (Region: region, Listing: await ListAsync(regions[region]))));
            Mark($"listings: expected {expected}; {string.Join("; ", listings.Select(listing => $"{listing.Region} {listing.Listing}"))}");
            Expected.Check(listings.All(listing => listing.Listing == expected), $"{Name}: listings differ from {expected}: {string.Join("; ", listings.Select(listing => $"{listing.Region} {listing.Listing}"))}");
        }

        private JsonObject TransactionOp(string id, Write write)
        {
            var op = new JsonObject { ["op"] = write.Kind == "ops" ? "patch" : write.Kind, ["collection"] = Collection, ["id"] = id };
            switch (write.Kind)
            {
                case "put":
                    op["document"] = write.Body!.DeepClone();
                    break;
                case "patch":
                    op["merge"] = write.Body!.DeepClone();
                    break;
                case "ops":
                    op["ops"] = write.Body!.DeepClone();
                    break;
            }

            return op;
        }

        // The harness's collection, read off its document address (.../collections/{collection}/documents/by-key?id=).
        private string Collection => DocumentsPath.Split('/')[^2];

        // The test's project, read off /v1/projects/{project}/databases/canary.
        private string Project => regions.DatabasePath.Split('/')[3];

        private string DocumentsPath => regions.Document("x").OriginalString.Split("/by-key", 2)[0];

        // The origin's outbound stream of this test's database to each region it is connected to (GET /replication).
        private async Task<Dictionary<string, Outbound>> StreamsAsync(string origin)
        {
            var status = await Docs.GetJsonAsync(regions[origin], new Uri("/replication", UriKind.Relative)) ?? throw new InvalidOperationException($"{origin} does not serve /replication");
            Dictionary<string, Outbound> streams = [with(StringComparer.Ordinal)];
            foreach (var peer in status["peers"]!.AsArray())
            {
                foreach (var database in peer!["databases"]!.AsArray().Where(database => database!["projectId"]?.GetValue<string>() == Project))
                {
                    streams[peer["region"]!.GetValue<string>()] = new(database!["head"]!.GetValue<long>(), database["acknowledged"]!.GetValue<long>());
                }
            }

            return streams;
        }

        private async Task<Listing> ListAsync(HttpClient client)
        {
            var documents = new List<(string Id, string Canonical)>();
            string? cursor = null;
            do
            {
                var uri = new Uri($"{DocumentsPath}?limit=1000{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}", UriKind.Relative);
                using var response = await client.GetAsync(uri);
                await Docs.EnsureSuccessAsync(response);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
                {
                    documents.Add((item.GetProperty("id").GetString()!, Docs.Canonical(JsonNode.Parse(item.GetProperty("document").GetRawText()))));
                }

                cursor = json.RootElement.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            }
            while (cursor is not null);

            return new Listing(documents.Count, Docs.Digest(documents));
        }
    }
}
