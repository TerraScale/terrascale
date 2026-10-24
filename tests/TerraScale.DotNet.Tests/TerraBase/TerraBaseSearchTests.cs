using System.Net;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>Full-text wire serialization against a fake handler; no TerraBase process runs.</summary>
public sealed class TerraBaseSearchTests
{
    [Test]
    public async Task Search_builders_raw_filters_and_score_sort_preserve_the_wire_contract()
    {
        var bodies = new List<string>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return TerraBaseIntegrationFixture.Json("""{"items":[{"id":"article","version":"tbv1-a","state":"value","document":{},"score":1.25}],"next_cursor":"opaque-score-cursor"}""");
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var query = new TerraBaseQuery { Filter = TerraBaseFilter.Search("/title", "database storage"), Sort = [TerraBaseSort.Score] };
        var page = await db.QueryAsync("articles", query);
        await Assert.That(bodies[0]).IsEqualTo("""{"filter":{"search":["/title","database storage"]},"sort":[{"path":"$score","order":"desc"}]}""");
        await Assert.That(page.Items.Single().Score).IsEqualTo(1.25);

        await db.QueryAsync("articles", query with { Cursor = page.NextCursor });
        using var resumed = JsonDocument.Parse(bodies[1]);
        await Assert.That(resumed.RootElement.GetProperty("cursor").GetString()).IsEqualTo("opaque-score-cursor");
        await db.QueryAsync("articles", new() { Filter = TerraBaseFilter.Search("/metadata/a~1b~0c", "database") });
        await Assert.That(bodies[2]).IsEqualTo("""{"filter":{"search":["/metadata/a~1b~0c","database"]}}""");
        using var raw = JsonDocument.Parse("""{"and":[{"eq":["/published",true]},{"search":["/title","database"]}]}""");
        await db.QueryAsync("articles", new() { Filter = raw.RootElement, Sort = [TerraBaseSort.Score] });
        using var sent = JsonDocument.Parse(bodies[3]);
        await Assert.That(sent.RootElement.GetProperty("filter").GetRawText()).IsEqualTo(raw.RootElement.GetRawText());
    }

    [Test]
    public async Task Invalid_search_paths_and_score_sorts_are_rejected_before_HTTP()
    {
        using var handler = new TerraBaseTestHandler((_, _) => throw new HttpRequestException("sent"));
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        foreach (var path in new[] { "$id", "$score", "title", "/bad~2", "/" + string.Join('/', Enumerable.Repeat("a", 17)), "/\ud800" })
        {
            await Assert.That(() => TerraBaseFilter.Search(path, "database")).Throws<ArgumentException>();
        }

        foreach (var terms in new[] { "", "\0", "\ud800", new string('é', 2049) })
        {
            await Assert.That(() => TerraBaseFilter.Search("/title", terms)).Throws<ArgumentException>();
        }

        await Assert.That(TerraBaseFilter.Search("/title", new string('é', 2048)).GetProperty("search")[1].GetString()).IsEqualTo(new string('é', 2048));
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("articles", new()
        {
            Filter = TerraBaseFilter.Search("/title", "database"),
            Sort = [TerraBaseSort.Score, new("$id")],
        }));
        using var multiple = JsonDocument.Parse("""{"or":[{"search":["/title","a"]},{"search":["/body","b"]}]}""");
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("articles", new() { Filter = multiple.RootElement, Sort = [TerraBaseSort.Score] }));
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("articles", new()
        {
            Filter = TerraBaseFilter.Search("/title", "database"),
            Sort = [TerraBaseSort.Near("/embedding", [1f])],
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("articles", new() { Sort = [TerraBaseSort.Score] }));
        foreach (var order in new string?[] { null, "asc" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("articles", new()
            {
                Filter = TerraBaseFilter.Search("/title", "database"),
                Sort = [new("$score", order)],
            }));
        }
    }

    [Test]
    public async Task Search_composes_with_geo_filters_but_rejects_geo_near_before_HTTP()
    {
        var bodies = new List<string>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return TerraBaseIntegrationFixture.Json("""{"items":[],"next_cursor":null}""");
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var search = TerraBaseFilter.Search("/title", "database");
        var radius = TerraBaseFilter.WithinRadius("/loc", 0, 0, 1000);
        var box = TerraBaseFilter.WithinBox("/loc", -1, -1, 1, 1);
        using var combined = JsonDocument.Parse("{\"and\":[" + search.GetRawText() + "," + radius.GetRawText() + "," + box.GetRawText() + "]}");
        await db.QueryAsync("articles", new() { Filter = combined.RootElement, Sort = [TerraBaseSort.Score] });
        await Assert.That(bodies[0]).IsEqualTo("""{"filter":{"and":[{"search":["/title","database"]},{"within":["/loc",{"lat":0,"lon":0,"meters":1000}]},{"within":["/loc",{"south":-1,"west":-1,"north":1,"east":1}]}]},"sort":[{"path":"$score","order":"desc"}]}""");
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("articles", new()
        {
            Filter = combined.RootElement,
            Sort = [TerraBaseSort.NearPoint("/loc", 0, 0)],
        }));
        await Assert.That(bodies.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Missing_text_index_preserves_invalid_query_and_sends_once()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json("""{"error":{"code":"invalid_query","message":"Text index required"}}""", HttpStatusCode.BadRequest));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => db.QueryAsync("articles", new() { Filter = TerraBaseFilter.Search("/title", "database") }));
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_query");
        await Assert.That(error.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Text_index_kind_round_trips_and_existing_btree_calls_stay_unchanged()
    {
        const string index = """{"name":"articles_text","collection":"articles","fields":["title","metadata.summary"],"state":"active","kind":"text"}""";
        var calls = new List<(string Method, string Path, string? Body)>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            return TerraBaseIntegrationFixture.Json(request.Method == HttpMethod.Get ? "{\"items\":[" + index + "]}" : index,
                request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var created = await db.CreateIndexAsync("articles", kind: "text", fields: ["title", "metadata.summary"]);
        var listed = await db.ListIndexesAsync();
        await db.CreateIndexAsync("articles", ["title"], null, default);
        await Assert.That(created.Kind).IsEqualTo("text");
        await Assert.That(listed.Items.Single().Kind).IsEqualTo("text");
        await Assert.That(calls[0].Body).IsEqualTo("""{"collection":"articles","fields":["title","metadata.summary"],"kind":"text"}""");
        await Assert.That(calls[0].Path).EndsWith("/databases/app/indexes");
        await Assert.That(calls[2].Body).IsEqualTo("""{"collection":"articles","fields":["title"]}""");
        await Assert.ThrowsAsync<ArgumentException>(() => db.CreateIndexAsync("articles", kind: "unknown", fields: ["title"]));
        await Assert.That(calls.Count).IsEqualTo(3);
    }
}
