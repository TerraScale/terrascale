using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;

namespace TerraBase.Fast.Documents;

public sealed class QueryApiTests
{
    private static readonly string[] CursorSortPaths = ["/a", "/b", "/c", "/d"];
    private static readonly string[] SiblingProjectionPaths = ["/payload", "/payload~1a/a"];
    private static readonly string[] PathOnly = ["/a"];
    private static readonly int[] OneValue = [1];

    [Test]
    public async Task Query_errors_answer_their_code_and_message()
    {
        await using var api = await StartAsync();
        // Two documents sorted by a value past 64 KiB: the first page's cursor would have to carry it.
        var wide = new string('w', (64 * 1024) + 1);
        await api.PutAsync(Ids["A"], new { s = wide });
        await api.PutAsync(Ids["B"], new { s = wide + "w" });
        var notNested = new { eq = new object[] { "/a", 1 } };
        object deep = notNested;
        for (var i = 0; i < 8; i++)
        {
            deep = new { not = deep };
        }

        const string Json = "application/json";
        var before = await api.ReadUsageAsync();
        (ApiResponse Response, string Code, string Message)[] cases =
        [
            (await api.QueryAsync(new { explain = 1 }), "invalid_query", "explain is true or false"),
            (await api.QueryAsync(new { limit = 0 }), "invalid_query", "limit must be between 1 and 1000"),
            (await api.QueryAsync(new { sort = Enumerable.Range(0, 5).Select(i => new { path = $"/f{i}" }).ToArray() }), "invalid_query", "sort has at most 4 keys"),
            (await api.QueryAsync(new { project = Enumerable.Range(0, 65).Select(i => $"/f{i}").ToArray() }), "invalid_query", "project has at most 64 paths"),
            (await api.QueryAsync(new { sort = new[] { new { path = "/a", order = "up" } } }), "invalid_query", "sort order is asc or desc"),
            (await api.QueryAsync(new { filter = deep }), "invalid_query", "a filter nests at most 8 levels and has at most 256 nodes"),
            (await api.QueryAsync(new { filter = new { } }), "invalid_query", "a filter is an object with exactly one operator"),
            (await api.QueryAsync(new { filter = new { and = Array.Empty<object>() } }), "invalid_query", "and takes 1 to 64 filters"),
            (await api.QueryAsync(new { filter = new { eq = PathOnly } }), "invalid_query", "a scalar filter takes [path, value]"),
            (await api.QueryAsync(new { filter = new { lt = new object?[] { "/a", null } } }), "invalid_query", "ordered comparisons reject null"),
            (await api.QueryAsync(new { filter = new { @in = new object[] { "/a", Array.Empty<object>() } } }), "invalid_query", "in takes [path, [1 to 256 values]]"),
            (await api.QueryAsync(new { filter = new { prefix = new object[] { "/a", 1 } } }), "invalid_query", "prefix takes [path, string]"),
            (await api.QueryAsync(new { filter = new { near = new object[] { "/a", 1 } } }), "invalid_query", "unknown filter operator"),
            (await api.QueryAsync(new { filter = new { eq = new object[] { new { lower = "/a" }, "x" } } }), "invalid_query", "lower is not supported yet"),
            (await api.QueryAsync(new { filter = new { eq = new object[] { "a", 1 } } }), "invalid_query", "a path is \"$id\" or a JSON Pointer of 1 to 16 segments"),
            (await api.QueryAsync(new { filter = new { eq = new object[] { string.Concat(Enumerable.Repeat("/a", 17)), 1 } } }), "invalid_query", "a path has at most 16 segments"),
            (await api.QueryAsync(new { filter = new { eq = new object[] { "/a", OneValue } } }), "invalid_query", "values are JSON scalars"),
            (await api.SendRawAsync(HttpMethod.Post, api.Query, """{"filter":{"eq":["/a",1e400]}}""", Json),
                "invalid_query", "numbers have at most 128 significant digits and a magnitude of 0 or 1e-307 to below 1e308"),
            (await api.QueryAsync(new { cursor = "not-a-cursor" }), "invalid_cursor", "the cursor does not belong to this query"),
            (await api.SendAsync(HttpMethod.Get, api.Documents + "?cursor=not-a-cursor"), "invalid_cursor", "the cursor does not belong to this query"),
            (await api.QueryAsync(new { sort = new[] { new { path = "/s" } }, limit = 1 }), "sort_requires_index", "cursor sort values are at most 64 KiB of UTF-8"),
        ];

        foreach (var (response, code, message) in cases)
        {
            await Assert.That((response.Status, response.Code, response.Message)).IsEqualTo((HttpStatusCode.BadRequest, code, message));
        }
        await Assert.That(await api.ReadUsageAsync()).IsEqualTo(before);
    }

    [Test]
    public async Task Encoded_cursor_size_is_bounded_with_distinct_sort_keys()
    {
        await using var api = await StartAsync();
        var value = new string('\0', 40 * 1024);
        foreach (var id in new[] { Ids["A"], Ids["B"] })
        {
            (await api.PutAsync(id, new { a = value, b = value, c = value, d = value })).Status.ShouldBe(HttpStatusCode.Created);
        }

        var sort = CursorSortPaths.Select(path => new { path }).ToArray();
        var before = await api.ReadUsageAsync();
        var tooLarge = await api.QueryAsync(new { sort, limit = 1 });
        await Assert.That((tooLarge.Status, tooLarge.Code)).IsEqualTo((HttpStatusCode.BadRequest, "sort_requires_index"));
        await Assert.That(tooLarge.Json.GetProperty("error").GetProperty("message").GetString()).IsEqualTo("encoded cursors are at most 1 MiB");
        var incomingTooLarge = await api.QueryAsync(new { cursor = new string('a', (1024 * 1024) + 1) });
        await Assert.That((incomingTooLarge.Status, incomingTooLarge.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_cursor"));
        await Assert.That(await api.ReadUsageAsync()).IsEqualTo(before);
    }

    [Test]
    public async Task Repeated_large_string_sort_keys_cannot_emit_an_unusable_cursor()
    {
        await using var api = await StartAsync();
        var sort = Enumerable.Range(0, 4).Select(_ => new { path = "/s" }).ToArray();
        foreach (var id in new[] { Ids["A"], Ids["B"] })
        {
            (await api.PutAsync(id, new { s = new string('x', 900 * 1024) })).Status.ShouldBe(HttpStatusCode.Created);
        }

        // An irrelevant large field must not reject an index-backed $id continuation.
        var idSort = new[] { new { path = "$id" }, new { path = "/s" } };
        var idFirst = await api.QueryAsync(new { sort = idSort, limit = 1 });
        await Assert.That(idFirst.Status).IsEqualTo(HttpStatusCode.OK).Because($"HTTP {(int)idFirst.Status}, error {idFirst.Code}: {idFirst.Message}");
        var idCursor = idFirst.Json.GetProperty("next_cursor").GetString();
        using var idPosition = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(idCursor!));
        await Assert.That(idPosition.RootElement.GetArrayLength()).IsEqualTo(4);
        var idNext = await api.QueryAsync(new { sort = idSort, limit = 1, cursor = idCursor });
        await Assert.That(idNext.Status).IsEqualTo(HttpStatusCode.OK).Because($"HTTP {(int)idNext.Status}, error {idNext.Code}: {idNext.Message}");
        await Assert.That(string.Join(",", idFirst.Ids.Concat(idNext.Ids))).IsEqualTo(Ids["A"] + "," + Ids["B"]);

        var beforeRejected = await api.ReadUsageAsync();
        var rejected = await api.QueryAsync(new { sort, limit = 1 });
        await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((HttpStatusCode.BadRequest, "sort_requires_index"));
        await Assert.That(await api.ReadUsageAsync()).IsEqualTo(beforeRejected);
        // Worst-case escaping still produces a continuation accepted by the next request.
        foreach (var id in new[] { Ids["A"], Ids["B"] })
        {
            (await api.PutAsync(id, new { s = new string('\0', 64 * 1024) })).Status.ShouldBe(HttpStatusCode.OK);
        }

        var first = await api.QueryAsync(new { sort, limit = 1 });
        await Assert.That(first.Status).IsEqualTo(HttpStatusCode.OK).Because($"HTTP {(int)first.Status}, error {first.Code}: {first.Message}");
        using var position = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(first.Json.GetProperty("next_cursor").GetString()!));
        await Assert.That(position.RootElement.GetArrayLength()).IsEqualTo(4);
        await Assert.That(first.Json.GetProperty("next_cursor").GetString()!.Length).IsLessThanOrEqualTo(1024 * 1024);
        await Assert.That(position.RootElement[3].GetString()!.Length).IsEqualTo(64 * 1024);
        var next = await api.QueryAsync(new { sort, limit = 1, cursor = first.Json.GetProperty("next_cursor").GetString() });
        await Assert.That(next.Status).IsEqualTo(HttpStatusCode.OK).Because($"HTTP {(int)next.Status}, error {next.Code}: {next.Message}");
        await Assert.That(string.Join(",", first.Ids.Concat(next.Ids))).IsEqualTo(Ids["A"] + "," + Ids["B"]);
        await Assert.That(next.Json.GetProperty("next_cursor").ValueKind).IsEqualTo(JsonValueKind.Null);
        var filter = new
        {
            or = new object[] { new { exists = "/s" }, new { eq = new object[] { "/unused", new string('x', 15 * 1024 * 1024 / 4) } } },
        };
        var beforeContinuation = await api.ReadUsageAsync();
        var tooLargeToResume = await api.QueryAsync(new { filter, sort, limit = 1 });
        await Assert.That((tooLargeToResume.Status, tooLargeToResume.Code, tooLargeToResume.Message))
            .IsEqualTo((HttpStatusCode.BadRequest, "sort_requires_index", "the query and cursor exceed the continuation request budget"));
        await Assert.That(await api.ReadUsageAsync()).IsEqualTo(beforeContinuation);
    }

    [Test]
    public async Task Sort_budget_limits_bytes_even_when_only_one_result_is_requested()
    {
        await using var api = await StartAsync();
        var payload = new string('x', 1000000);
        for (var i = 0; i < 9; i++)
        {
            (await api.PutAsync(DocumentApi.NewId(), new { n = i, payload })).Status.ShouldBe(HttpStatusCode.Created);
        }

        var sorted = await api.QueryAsync(new { sort = new[] { new { path = "/n" } }, limit = 1 });
        var sparseSorted = await api.QueryAsync(new { filter = new { eq = new object[] { "/n", 0 } }, sort = new[] { new { path = "/n" } }, limit = 1 });
        var listed = await api.QueryAsync(new { limit = 1 });
        await Assert.That((sorted.Status, sorted.Code)).IsEqualTo((HttpStatusCode.BadRequest, "sort_requires_index"));
        await Assert.That((sparseSorted.Status, sparseSorted.Code)).IsEqualTo((HttpStatusCode.BadRequest, "sort_requires_index"));
        await Assert.That(listed.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(listed.Ids.Count).IsEqualTo(1);
        foreach (var order in new[] { "asc", "desc" })
        {
            var sort = new[] { new { path = "$id", order }, new { path = "/payload", order } };
            var page = await api.QueryAsync(new { sort, limit = 1000 });
            var next = await api.QueryAsync(new { sort, limit = 1000, cursor = page.Json.GetProperty("next_cursor").GetString() });
            await Assert.That(page.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(page.Ids.Count).IsEqualTo(8);
            await Assert.That(next.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(next.Ids.Count).IsEqualTo(1);
            await Assert.That(page.Ids.Concat(next.Ids).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(9);
        }
    }

    [Test]
    public async Task Cursors_reject_invalid_rank_value_pairs_even_with_the_correct_digest()
    {
        await using var api = await SeedAsync();
        var first = await api.QueryAsync(new { sort = new[] { new { path = "/n" } }, limit = 1 });
        var cursor = first.Json.GetProperty("next_cursor").GetString()!;
        using var decoded = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(cursor));
        var digest = decoded.RootElement[0].GetString();
        var id = decoded.RootElement[1].GetString();
        var malformed = new object?[][]
        {
            [null, null], [-1, null], [7, null], [1.5, null], ["4", 1],
            [0, "x"], [1, false], [2, 0], [3, true], [4, null], [4, "1"], [5, 1], [6, "x"],
        };
        foreach (var pair in malformed)
        {
            var forged = System.Buffers.Text.Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new object?[] { digest, id, pair[0], pair[1] }));
            var result = await api.QueryAsync(new { sort = new[] { new { path = "/n" } }, cursor = forged });
            await Assert.That((result.Status, result.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_cursor"));
        }

        var nonFinite = System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes($$"""["{{digest}}","{{id}}",4,1e400]"""));
        var infinite = await api.QueryAsync(new { sort = new[] { new { path = "/n" } }, cursor = nonFinite });
        await Assert.That((infinite.Status, infinite.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_cursor"));
        var resumed = await api.QueryAsync(new { sort = new[] { new { path = "/n" } }, cursor });
        await Assert.That(resumed.Ids.Count).IsEqualTo(4);
    }

    // Ids sort A < B < C < D < E.
    private static readonly Dictionary<string, string> Ids = new()
    {
        ["A"] = "0000000000E00800000000000A",
        ["B"] = "0000000000E00800000000000B",
        ["C"] = "0000000000E00800000000000C",
        ["D"] = "0000000000E00800000000000D",
        ["E"] = "0000000000E00800000000000E",
    };

    // Mixed types on purpose: C.n is a string, C.tags a string, C.nested a scalar, E has no n.
    private static readonly Dictionary<string, string> Seed = new()
    {
        ["A"] = """{"n":1,"s":"apple","b":true,"tags":["x","y"],"nested":{"k":"v1","depth":{"x":1}},"nul":null}""",
        ["B"] = """{"n":2.5,"s":"banana","b":false,"tags":["y"],"nested":{"k":"v2"}}""",
        ["C"] = """{"n":"10","s":"cherry","tags":"x","nested":"flat"}""",
        ["D"] = """{"n":10,"s":"apricot","nested":{"k":"v1"},"a/b":{"~c":true}}""",
        ["E"] = """{"s":"Apple","0":"zero"}""",
    };

    // (filter, expected letters in id order): one case per operator and path shape.
    private static readonly (string Filter, string Expected)[] FilterCases =
    [
        ("""{"eq":["/n",1]}""", "A"),
        ("""{"eq":["/n",1.0]}""", "A"),
        ("""{"eq":["/n",1e1]}""", "D"),
        ("""{"eq":["/s","apple"]}""", "A"),
        ("""{"eq":["/b",false]}""", "B"),
        ("""{"eq":["/nul",null]}""", "A"),
        ("""{"eq":["/nested/k","v1"]}""", "AD"),
        ("""{"eq":["/nested/depth/x",1]}""", "A"),
        ("""{"eq":["/tags","x"]}""", "C"),
        ("""{"eq":["/0","zero"]}""", "E"),
        ("""{"eq":["/a~1b/~0c",true]}""", "D"),
        ("""{"eq":["$id","0000000000E00800000000000B"]}""", "B"),
        ("""{"ne":["/n",1]}""", "BCDE"),
        ("""{"gt":["/n",2]}""", "BD"),
        ("""{"gte":["/n",2.5]}""", "BD"),
        ("""{"lt":["/n",10]}""", "AB"),
        ("""{"lte":["/n",10]}""", "ABD"),
        ("""{"gt":["/s","b"]}""", "BC"),
        ("""{"lt":["/s","apple"]}""", "E"),
        ("""{"gt":["/b",false]}""", "A"),
        ("""{"gt":["/n","1"]}""", "C"),
        ("""{"in":["/s",["apple","cherry","zzz"]]}""", "AC"),
        ("""{"in":["/n",[1,"10"]]}""", "AC"),
        ("""{"in":["$id",["0000000000E00800000000000A","0000000000E00800000000000E"]]}""", "AE"),
        ("""{"nin":["/s",["apple","banana"]]}""", "CDE"),
        ("""{"exists":"/nul"}""", "A"),
        ("""{"exists":"/n"}""", "ABCD"),
        ("""{"not":{"exists":"/n"}}""", "E"),
        ("""{"prefix":["/s","ap"]}""", "AD"),
        ("""{"contains":["/tags","x"]}""", "A"),
        ("""{"contains":["/tags","y"]}""", "AB"),
        ("""{"and":[{"gte":["/n",1]},{"prefix":["/s","a"]}]}""", "AD"),
        ("""{"or":[{"eq":["/s","banana"]},{"eq":["/b",true]}]}""", "AB"),
        ("""{"not":{"or":[{"lt":["/n",5]},{"eq":["/s","Apple"]}]}}""", "CD"),
    ];

    [Test]
    public async Task Filters_match_each_operator()
    {
        await using var api = await SeedAsync();
        var mismatches = new List<string>();
        foreach (var (filter, expected) in FilterCases)
        {
            var result = await api.SendRawAsync(HttpMethod.Post, api.Query, $$"""{"filter":{{filter}}}""", "application/json");
            var actual = result.Status == HttpStatusCode.OK ? Letters(result) : result.Status.ToString();
            if (actual != expected)
            {
                mismatches.Add($"{filter} -> {actual}, expected {expected}; HTTP {(int)result.Status}, error {result.Code}: {result.Message}");
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task Sorting_orders_by_type_then_value_then_id()
    {
        await using var api = await SeedAsync();

        var ascending = await api.SendRawAsync(HttpMethod.Post, api.Query, """{"sort":[{"path":"/n"}]}""", "application/json");
        var descending = await api.SendRawAsync(HttpMethod.Post, api.Query, """{"sort":[{"path":"/n","order":"desc"}]}""", "application/json");
        var twoKeys = await api.SendRawAsync(
            HttpMethod.Post, api.Query, """{"sort":[{"path":"/nested/k","order":"desc"},{"path":"/s"}]}""", "application/json");
        var byId = await api.SendRawAsync(HttpMethod.Post, api.Query, """{"sort":[{"path":"$id","order":"desc"}]}""", "application/json");

        await Assert.That(ascending.Status).IsEqualTo(HttpStatusCode.OK).Because($"HTTP {(int)ascending.Status}, error {ascending.Code}: {ascending.Message}");
        await Assert.That(descending.Status).IsEqualTo(HttpStatusCode.OK).Because($"HTTP {(int)descending.Status}, error {descending.Code}: {descending.Message}");
        await Assert.That(twoKeys.Status).IsEqualTo(HttpStatusCode.OK).Because($"HTTP {(int)twoKeys.Status}, error {twoKeys.Code}: {twoKeys.Message}");
        await Assert.That(byId.Status).IsEqualTo(HttpStatusCode.OK).Because($"HTTP {(int)byId.Status}, error {byId.Code}: {byId.Message}");

        // missing < numbers < strings
        await Assert.That(Letters(ascending)).IsEqualTo("EABDC");
        await Assert.That(Letters(descending)).IsEqualTo("CDBAE");
        // v2, then v1 (apple < apricot), then non-strings (C's scalar parent and E's missing) by s.
        await Assert.That(Letters(twoKeys)).IsEqualTo("BADEC");
        await Assert.That(Letters(byId)).IsEqualTo("EDCBA");
    }

    [Test]
    public async Task Projection_keeps_listed_members_with_their_parents()
    {
        await using var api = await SeedAsync();

        var result = await api.SendRawAsync(
            HttpMethod.Post, api.Query,
            """{"filter":{"in":["$id",["0000000000E00800000000000A","0000000000E00800000000000C"]]},"project":["/s","/nested/depth/x","/missing","$id"]}""",
            "application/json");
        var documents = result.Json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("document").GetRawText()).ToList();

        await Assert.That(documents).IsEquivalentTo(["""{"s":"apple","nested":{"depth":{"x":1}}}""", """{"s":"cherry"}"""]);
    }

    [Test]
    public async Task Overlapping_projections_preserve_case_distinct_members_in_either_order()
    {
        await using var api = await StartAsync();
        var id = DocumentApi.NewId();
        var created = await api.SendRawAsync(
            HttpMethod.Put, api.Document(id),
            """{"obj":{"a":1,"A":2,"child":{"x":3,"X":4}},"Obj":{"a":5,"A":6},"arr":[{"a":7,"A":8}],"omitted":true}""",
            "application/json");
        created.Status.ShouldBe(HttpStatusCode.Created);

        string[] paths = ["/obj", "/obj/a", "/obj/child/x", "/Obj", "/Obj/A", "/arr"];
        foreach (var project in new[] { paths, [.. paths.Reverse()] })
        {
            var result = await api.QueryAsync(new { project });
            await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(result.Ids).IsEquivalentTo([id]);
            var document = result.Json.GetProperty("items")[0].GetProperty("document");
            await Assert.That(document.EnumerateObject().Count()).IsEqualTo(3);
            await Assert.That(document.GetProperty("obj").GetRawText()).IsEqualTo("""{"a":1,"A":2,"child":{"x":3,"X":4}}""");
            await Assert.That(document.GetProperty("Obj").GetRawText()).IsEqualTo("""{"a":5,"A":6}""");
            await Assert.That(document.GetProperty("arr").GetRawText()).IsEqualTo("""[{"a":7,"A":8}]""");
        }
    }

    [Test]
    public async Task Projection_selects_case_distinct_members_separately()
    {
        await using var api = await StartAsync();
        var id = DocumentApi.NewId();
        (await api.SendRawAsync(
            HttpMethod.Put, api.Document(id), """{"a":1,"A":2,"obj":{"a":3,"A":4}}""", "application/json"))
            .Status.ShouldBe(HttpStatusCode.Created);

        (string[] Project, string Expected)[] cases =
        [
            (["/a"], """{"a":1}"""),
            (["/A"], """{"A":2}"""),
            (["/obj/a"], """{"obj":{"a":3}}"""),
            (["/obj/A"], """{"obj":{"A":4}}"""),
            (["/a", "/A", "/obj/a", "/obj/A"], """{"a":1,"A":2,"obj":{"a":3,"A":4}}"""),
        ];
        foreach (var (project, expected) in cases)
        {
            var result = await api.QueryAsync(new { project });
            await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(result.Ids).IsEquivalentTo([id]);
            await Assert.That(result.Json.GetProperty("items")[0].GetProperty("document").GetRawText()).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Repeated_large_projections_reduce_to_covering_decoded_paths()
    {
        await using var api = await StartAsync();
        var value = new string('x', 900 * 1024);
        var ids = new[] { DocumentApi.NewId(), DocumentApi.NewId() };
        foreach (var id in ids)
        {
            (await api.PutAsync(id, new Dictionary<string, object>
            {
                ["payload/a"] = new Dictionary<string, object> { ["value"] = value, ["a"] = 1, ["A"] = 2 },
                ["payload"] = new { a = 3 },
                ["omitted"] = true,
            })).Status.ShouldBe(HttpStatusCode.Created);
        }

        string[] selections = ["/payload~1a/value", "/payload~1a", "/payload~1a/a", "/payload~1a/A"];
        var repeated = Enumerable.Range(0, 64).Select(i => selections[i % selections.Length]).ToArray();
        foreach (var project in new[] { repeated, [.. repeated.Reverse()] })
        {
            var response = await api.QueryAsync(new { project });
            await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Ids).IsEquivalentTo(ids);
            foreach (var item in response.Json.GetProperty("items").EnumerateArray())
            {
                var document = item.GetProperty("document");
                await Assert.That(document.EnumerateObject().Count()).IsEqualTo(1);
                var payload = document.GetProperty("payload/a");
                await Assert.That(payload.GetProperty("value").GetString()).IsEqualTo(value);
                await Assert.That(payload.GetProperty("a").GetInt32()).IsEqualTo(1);
                await Assert.That(payload.GetProperty("A").GetInt32()).IsEqualTo(2);
            }
        }

        // A textual prefix is not an ancestor: compare whole decoded segments.
        var siblings = await api.QueryAsync(new { project = SiblingProjectionPaths });
        await Assert.That(siblings.Status).IsEqualTo(HttpStatusCode.OK);
        var first = siblings.Json.GetProperty("items")[0].GetProperty("document");
        await Assert.That(first.GetRawText()).IsEqualTo("""{"payload":{"a":3},"payload/a":{"a":1}}""");
    }

    [Test]
    public async Task Cursors_page_through_every_match_once()
    {
        await using var api = await SeedAsync();
        foreach (var sort in new string?[] { null, """[{"path":"/n","order":"desc"}]""", """[{"path":"/nested/k"},{"path":"/b","order":"desc"}]""" })
        {
            var sortJson = sort is null ? "" : $",\"sort\":{sort}";
            var all = await api.SendRawAsync(HttpMethod.Post, api.Query, $$"""{"limit":1000{{sortJson}}}""", "application/json");

            var seen = new StringBuilder();
            string? cursor = null;
            var pages = 0;
            do
            {
                var cursorJson = cursor is null ? "" : $",\"cursor\":\"{cursor}\"";
                var page = await api.SendRawAsync(HttpMethod.Post, api.Query, $$"""{"limit":2{{sortJson}}{{cursorJson}}}""", "application/json");
                seen.Append(Letters(page));
                cursor = page.Json.GetProperty("next_cursor").GetString();
                pages++;
            }
            while (cursor is not null && pages < 10);

            await Assert.That(seen.ToString()).IsEqualTo(Letters(all));
            await Assert.That(seen.Length).IsEqualTo(5);
            await Assert.That(pages).IsEqualTo(3);
        }
    }

    [Test]
    public async Task Cursors_only_resume_their_own_query()
    {
        await using var api = await SeedAsync();
        var first = await api.SendRawAsync(HttpMethod.Post, api.Query, """{"limit":1,"filter":{"exists":"/s"}}""", "application/json");
        var cursor = first.Json.GetProperty("next_cursor").GetString();

        var sameQuery = await api.SendRawAsync(
            HttpMethod.Post, api.Query, $$"""{"limit":4,"filter":{"exists":"/s"},"cursor":"{{cursor}}"}""", "application/json");
        var otherFilter = await api.SendRawAsync(
            HttpMethod.Post, api.Query, $$"""{"limit":1,"filter":{"exists":"/n"},"cursor":"{{cursor}}"}""", "application/json");
        var otherSort = await api.SendRawAsync(
            HttpMethod.Post, api.Query, $$"""{"limit":1,"filter":{"exists":"/s"},"sort":[{"path":"/s"}],"cursor":"{{cursor}}"}""", "application/json");
        var listCursor = await api.SendAsync(HttpMethod.Get, $"{api.Documents}?cursor={cursor}");
        var garbage = await api.SendRawAsync(HttpMethod.Post, api.Query, """{"cursor":"%%%"}""", "application/json");

        await Assert.That(Letters(sameQuery)).IsEqualTo("BCDE");
        await Assert.That(sameQuery.Json.GetProperty("next_cursor").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That((otherFilter.Status, otherFilter.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_cursor"));
        await Assert.That(otherSort.Code).IsEqualTo("invalid_cursor");
        await Assert.That(listCursor.Code).IsEqualTo("invalid_cursor");
        await Assert.That(garbage.Code).IsEqualTo("invalid_cursor");
    }

    [Test]
    public async Task Count_reports_every_match_on_request()
    {
        await using var api = await SeedAsync();

        var counted = await api.SendRawAsync(HttpMethod.Post, api.Query, """{"filter":{"exists":"/n"},"limit":1,"count":true}""", "application/json");
        var uncounted = await api.SendRawAsync(HttpMethod.Post, api.Query, """{"filter":{"exists":"/n"}}""", "application/json");

        await Assert.That(counted.Ids.Count).IsEqualTo(1);
        await Assert.That(counted.Json.GetProperty("count").GetRawText()).IsEqualTo("""{"value":"4","exact":true}""");
        await Assert.That(uncounted.Json.TryGetProperty("count", out _)).IsFalse();
    }

    private static readonly string[] InvalidQueries =
    [
        """{"unknown":1}""",
        """{"filter":{"like":["/s","a"]}}""",
        """{"filter":{"eq":["/s","a"],"ne":["/s","b"]}}""",
        """{"filter":{"gt":["/n",null]}}""",
        """{"filter":{"eq":["s","a"]}}""",
        """{"filter":{"eq":["/s",["a"]]}}""",
        """{"filter":{"eq":[{"lower":"/s"},"a"]}}""",
        """{"filter":{"in":["/s",[]]}}""",
        """{"filter":{"and":[]}}""",
        """{"filter":{"not":{"not":{"not":{"not":{"not":{"not":{"not":{"not":{"exists":"/a"}}}}}}}}}}""",
        """{"filter":{"eq":["/a/b/c/d/e/f/g/h/i/j/k/l/m/n/o/p/q","a"]}}""",
        """{"filter":{"eq":["/n",1e400]}}""",
        """{"sort":[{"path":"/a"},{"path":"/b"},{"path":"/c"},{"path":"/d"},{"path":"/e"}]}""",
        """{"sort":[{"path":"/a","order":"up"}]}""",
        """{"limit":0}""",
        """{"limit":1001}""",
        """{"limit":"5"}""",
        """{"explain":"plan"}""",
        """{"filter":{"exists":"/a"},"filter":{"exists":"/b"}}""",
        """{"sort":[null]}""",
        """{"project":[null]}""",
        """{"filter":{"eq":["/s","\ud800"]}}""",
        """{"filter":{"exists":"/\udc00"}}""",
        """{"filter":{"eq":["/n",1e308]}}""",
        """{"filter":{"in":["/n",[1e400]]}}""",
    ];

    [Test]
    public async Task Invalid_queries_are_rejected()
    {
        await using var api = await StartAsync();
        var accepted = new List<string>();
        foreach (var query in InvalidQueries)
        {
            var result = await api.SendRawAsync(HttpMethod.Post, api.Query, query, "application/json");
            if ((result.Status, result.Code) != (HttpStatusCode.BadRequest, "invalid_query"))
            {
                accepted.Add($"{query} -> {(int)result.Status} {result.Code}");
            }
        }

        await Assert.That(accepted).IsEmpty();
    }

    [Test]
    public async Task Member_names_with_quotes_backslashes_and_nul_are_queryable()
    {
        await using var api = await StartAsync();
        var id = DocumentApi.NewId();
        await api.SendRawAsync(
            HttpMethod.Put, api.Document(id), """{"a\"b":{"c\\d":1},"s":"a\u0000b","x.y":{"[0]":true},"n\u0000m":1,"t\tb":{"l\nf":"x"}}""", "application/json");
        await api.PutAsync(DocumentApi.NewId(), new { s = "a" });

        async Task<int> CountAsync(string filter)
        {
            return (await api.SendRawAsync(HttpMethod.Post, api.Query, $$"""{"filter":{{filter}}}""", "application/json")).Ids.Count;
        }

        await Assert.That(await CountAsync("""{"eq":["/a\"b/c\\d",1]}""")).IsEqualTo(1);
        await Assert.That(await CountAsync("""{"eq":["/x.y/[0]",true]}""")).IsEqualTo(1);
        // Control characters in member names, NUL included, are ordinary names.
        await Assert.That(await CountAsync("""{"eq":["/n\u0000m",1]}""")).IsEqualTo(1);
        await Assert.That(await CountAsync("""{"eq":["/t\tb/l\nf","x"]}""")).IsEqualTo(1);
        var sorted = await api.SendRawAsync(HttpMethod.Post, api.Query, """{"sort":[{"path":"/n\u0000m","order":"desc"}]}""", "application/json");
        await Assert.That(sorted.Ids[0]).IsEqualTo(id);
        var exactNul = await api.SendRawAsync(HttpMethod.Post, api.Query, """{"filter":{"eq":["/s","a\u0000b"]}}""", "application/json");
        await Assert.That(exactNul.Ids).IsEquivalentTo([id]);
        await Assert.That(await CountAsync("""{"prefix":["/s","a\u0000"]}""")).IsEqualTo(1);
        await Assert.That(await CountAsync("""{"prefix":["/s","a\u0000c"]}""")).IsEqualTo(0);
        await Assert.That(await CountAsync("""{"prefix":["/s","a"]}""")).IsEqualTo(2);
        var longId = DocumentApi.NewId();
        var longText = new string('a', 600) + "\0tail";
        (await api.PutAsync(longId, new { s = longText, tags = new[] { longText } })).Status.ShouldBe(HttpStatusCode.Created);
        var longEquals = await api.QueryAsync(new { filter = new { eq = new object[] { "/s", longText } } });
        var longContains = await api.QueryAsync(new { filter = new { contains = new object[] { "/tags", longText } } });
        await Assert.That(longEquals.Ids).IsEquivalentTo([longId]);
        await Assert.That(longContains.Ids).IsEquivalentTo([longId]);
    }

    [Test]
    public async Task Large_membership_filters_stay_within_turso_parameter_limits()
    {
        await using var api = await SeedAsync();
        var values = string.Join(',', Enumerable.Range(0, 256));
        var group = "{\"and\":[" + string.Join(',', Enumerable.Repeat($$"""{"in":["/n",[{{values}}]]}""", 64)) + "]}";
        var filter = "{\"and\":[" + string.Join(',', Enumerable.Repeat(group, 3)) + "]}";

        var result = await api.SendRawAsync(HttpMethod.Post, api.Query, $$"""{"filter":{{filter}}}""", "application/json");

        // 49152 values in 192 in filters: A (1) and D (10) match; B (2.5) and C ("10") do not.
        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Letters(result)).IsEqualTo("AD");
    }

    [Test]
    public async Task Queries_need_an_existing_collection()
    {
        await using var api = await StartAsync();

        var result = await api.SendRawAsync(HttpMethod.Post, $"{api.Project}/databases/db/collections/nope/query", "{}", "application/json");

        await Assert.That((result.Status, result.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_collection"));
    }

    // Runner pauses must not expire functional queries; QueryQosTests covers the plan's advancing deadline.
    private static Task<DocumentApi> StartAsync() => DocumentApi.StartAsync(services: services =>
        services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()));

    private static async Task<DocumentApi> SeedAsync()
    {
        var api = await StartAsync();
        try
        {
            var ops = string.Join(',', Seed.Select(pair => $$"""{"op":"create","collection":"items","id":"{{Ids[pair.Key]}}","document":{{pair.Value}}}"""));
            (await api.SendRawAsync(HttpMethod.Post, api.Transactions, $$"""{"ops":[{{ops}}]}""", "application/json")).Status.ShouldBe(HttpStatusCode.OK);
            // A deleted document never matches.
            var deleted = DocumentApi.NewId();
            await api.PutAsync(deleted, new { n = 1, s = "apple" });
            await api.SendAsync(HttpMethod.Delete, api.Document(deleted));
            return api;
        }
        catch
        {
            await api.DisposeAsync();
            throw;
        }
    }

    private static string Letters(ApiResponse result) =>
        string.Concat(result.Ids.Select(id => Ids.Single(pair => pair.Value == id).Key));
}
