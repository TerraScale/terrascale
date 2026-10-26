using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseDocumentPaginationTests
{
    private const string Documents = """{"items":[{"id":"01JA0000000000000000000002","version":"v1","state":"value","document":{}},{"id":"01JA0000000000000000000003","version":"v2","state":"value","document":{}}],"next_cursor":"never"}""";

    [Test]
    public async Task Pages_are_lazy_preserve_the_query_and_follow_empty_pages()
    {
        var bodies = new List<JsonElement>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
            await Assert.That(request.RequestUri!.AbsolutePath).EndsWith("/collections/items/query");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            bodies.Add(body.RootElement.Clone());
            return TerraBaseIntegrationFixture.Json(bodies.Count == 1
                ? """{"items":[],"next_cursor":"opaque/+next=","count":{"value":4,"exact":false}}"""
                : """{"items":[],"next_cursor":null}""");
        });
        using var http = new HttpClient(handler);
        var query = new TerraBaseQuery { Cursor = "opaque/+initial=", Limit = 2, Count = true, Explain = true,
            Filter = JsonSerializer.SerializeToElement(new { eq = new object[] { "/status", "open" } }),
            Sort = [new("$id", "desc")], Project = ["/status"] };
        await using var pages = Database(http).QueryPagesAsync("items", query).GetAsyncEnumerator();
        await Assert.That(bodies.Count).IsEqualTo(0);
        await Assert.That(await pages.MoveNextAsync()).IsTrue();
        await Assert.That(bodies.Count).IsEqualTo(1);
        await Assert.That(pages.Current.Items.Count).IsEqualTo(0);
        await Assert.That(pages.Current.Count!.Value).IsEqualTo(4L);
        await Assert.That(pages.Current.Count!.Exact).IsFalse();
        await Assert.That(await pages.MoveNextAsync()).IsTrue();
        await Assert.That(await pages.MoveNextAsync()).IsFalse();
        await Assert.That(bodies.Count).IsEqualTo(2);
        await Assert.That(query.Cursor).IsEqualTo("opaque/+initial=");
        await Assert.That(query.Count).IsEqualTo(true);
        await Assert.That(bodies[0].GetProperty("count").GetBoolean()).IsTrue();
        await Assert.That(bodies[1].GetProperty("count").GetBoolean()).IsFalse();
        await Assert.That(bodies[0].GetProperty("cursor").GetString()).IsEqualTo(query.Cursor);
        await Assert.That(bodies[1].GetProperty("cursor").GetString()).IsEqualTo("opaque/+next=");
        await Assert.That(JsonElement.DeepEquals(bodies[0].GetProperty("filter"), bodies[1].GetProperty("filter"))).IsTrue();
        foreach (var name in new[] { "sort", "project", "limit", "explain" })
            await Assert.That(JsonElement.DeepEquals(bodies[0].GetProperty(name), bodies[1].GetProperty(name))).IsTrue();
    }

    [Test]
    public async Task Documents_keep_order_after_an_empty_continuation_page()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(TerraBaseIntegrationFixture.Json(calls++ == 0
            ? """{"items":[],"next_cursor":"opaque"}""" : Documents.Replace("\"never\"", "null", StringComparison.Ordinal))));
        using var http = new HttpClient(handler);
        var ids = new List<string>();
        await foreach (var item in Database(http).QueryDocumentsAsync("items", new())) ids.Add(item.Id);
        await Assert.That(string.Join(',', ids)).IsEqualTo("01JA0000000000000000000002,01JA0000000000000000000003");
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancellation_stops_before_another_buffered_document(bool enumeratorToken)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) => { calls++; return Task.FromResult(TerraBaseIntegrationFixture.Json(Documents)); });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var stream = Database(http).QueryDocumentsAsync("items", new(), enumeratorToken ? default : cancellation.Token);
        await using var items = stream.GetAsyncEnumerator(enumeratorToken ? cancellation.Token : default);
        await Assert.That(await items.MoveNextAsync()).IsTrue();
        await Assert.That(items.Current.Id).IsEqualTo("01JA0000000000000000000002");
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => { await items.MoveNextAsync(); });
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_stops_before_the_next_page_or_initial_request()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) => { calls++; return Task.FromResult(TerraBaseIntegrationFixture.Json(Documents)); });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        await using var pages = Database(http).QueryPagesAsync("items", new()).GetAsyncEnumerator(cancellation.Token);
        await Assert.That(await pages.MoveNextAsync()).IsTrue();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => { await pages.MoveNextAsync(); });
        await using var initial = Database(http).QueryPagesAsync("items", new(), cancellation.Token).GetAsyncEnumerator();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => { await initial.MoveNextAsync(); });
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Early_break_does_not_fetch_another_page(bool pages)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) => { calls++; return Task.FromResult(TerraBaseIntegrationFixture.Json(Documents)); });
        using var http = new HttpClient(handler);
        if (pages) { await foreach (var _ in Database(http).QueryPagesAsync("items", new())) { break; } }
        else { await foreach (var _ in Database(http).QueryDocumentsAsync("items", new())) { break; } }
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Aggregate_pages_keep_groups_and_explain_metadata()
    {
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(TerraBaseIntegrationFixture.Json(
            """{"items":[],"next_cursor":null,"groups":[{"key":["open"],"count":2,"sum":{"/amount":3}}],"explain":{"plan":["SCAN items"],"indexes":[],"full_scan":true,"rows_scanned":2,"work_units":5,"elapsed_ms":0.5}}""")));
        using var http = new HttpClient(handler);
        await using var pages = Database(http).QueryPagesAsync("items", new() { Explain = true,
            Aggregate = new() { GroupBy = ["/status"], Sum = ["/amount"] } }).GetAsyncEnumerator();
        await Assert.That(await pages.MoveNextAsync()).IsTrue();
        await Assert.That(pages.Current.Groups!.Single().Key.Single().GetString()).IsEqualTo("open");
        await Assert.That(pages.Current.Groups!.Single().Sum!["/amount"]).IsEqualTo(3d);
        await Assert.That(pages.Current.Explain!.WorkUnits).IsEqualTo(5L);
        await Assert.That(await pages.MoveNextAsync()).IsFalse();
    }

    [Test]
    [Arguments(null, false, 2)]
    [Arguments("a", false, 1)]
    [Arguments(null, true, 3)]
    public async Task Cursor_cycles_fail_without_an_unbounded_walk(string? initial, bool alternating, int expectedCalls)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            if (++calls > expectedCalls) throw new InvalidOperationException("Pagination did not stop at the cycle.");
            var cursor = alternating && calls % 2 == 0 ? "b" : "a";
            return Task.FromResult(TerraBaseIntegrationFixture.Json("{\"items\":[],\"next_cursor\":\"" + cursor + "\"}"));
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in Database(http).QueryPagesAsync("items", new() { Cursor = initial })) { }
        });
        await Assert.That(error!.Message).IsEqualTo("TerraBase returned a cyclic query continuation.");
        await Assert.That(calls).IsEqualTo(expectedCalls);
    }

    private static TerraBaseDatabase Database(HttpClient http) => TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
}
