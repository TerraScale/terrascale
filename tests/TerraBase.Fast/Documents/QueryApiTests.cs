using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class QueryApiTests
{
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
