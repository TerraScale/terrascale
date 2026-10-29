using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>Query aggregates, geo within filters and the geo near sort against a fake handler; no TerraBase runs.</summary>
public sealed class TerraBaseAggregateAndGeoTests
{
    [Test]
    public async Task Aggregate_query_sends_the_exact_body_and_decodes_groups()
    {
        string? sent = null;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            sent = await request.Content!.ReadAsStringAsync(token);
            return TerraBaseIntegrationFixture.Json(
                """{"items":[],"next_cursor":null,"groups":[{"key":["paid"],"count":"250","sum":{"/amount":12500.5},"avg":{"/amount":50.002},"min":{"/amount":1},"max":{"/amount":99}},{"key":[null],"count":"2","sum":{"/amount":0},"avg":{"/amount":null}}]}""");
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");

        var page = await db.QueryAsync("orders", new TerraBaseQuery
        {
            Filter = TerraBaseFilter.WithinRadius("/loc", -23.55, -46.63, 1000),
            Aggregate = new() { GroupBy = ["/status"], Sum = ["/amount"], Avg = ["/amount"], Min = ["/amount"], Max = ["/amount"] },
            Limit = 100,
        });

        await Assert.That(sent).IsEqualTo(
            """{"filter":{"within":["/loc",{"lat":-23.55,"lon":-46.63,"meters":1000}]},"limit":100,"aggregate":{"group_by":["/status"],"sum":["/amount"],"avg":["/amount"],"min":["/amount"],"max":["/amount"]}}""");
        await Assert.That(page.Items.Count).IsEqualTo(0);
        var paid = page.Groups![0];
        await Assert.That(paid.Key[0].GetString()).IsEqualTo("paid");
        await Assert.That(paid.Count).IsEqualTo(250);
        await Assert.That(paid.Sum!["/amount"]).IsEqualTo(12500.5);
        await Assert.That(paid.Avg!["/amount"]).IsEqualTo(50.002);
        await Assert.That(paid.Min!["/amount"]).IsEqualTo(1);
        await Assert.That(paid.Max!["/amount"]).IsEqualTo(99);
        var missing = page.Groups[1];
        await Assert.That(missing.Key[0].ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(missing.Avg!["/amount"]).IsNull();
        await Assert.That(missing.Min).IsNull();
    }

    [Test]
    public async Task Geo_near_sort_and_box_filter_send_the_exact_body_and_decode_meters()
    {
        string? sent = null;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            sent = await request.Content!.ReadAsStringAsync(token);
            return TerraBaseIntegrationFixture.Json(
                """{"items":[{"id":"01JA00000000000000000000D1","version":"tbv1-a","state":"value","document":{},"distance":412.5}],"next_cursor":null}""");
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");

        var page = await db.QueryAsync("places", new TerraBaseQuery
        {
            Filter = TerraBaseFilter.WithinBox("/loc", -20, 179, -10, -170),
            Sort = [TerraBaseSort.NearPoint("/loc", -23.55, -46.63)],
            Limit = 10,
        });

        await Assert.That(sent).IsEqualTo(
            """{"filter":{"within":["/loc",{"south":-20,"west":179,"north":-10,"east":-170}]},"sort":[{"near":{"path":"/loc","lat":-23.55,"lon":-46.63}}],"limit":10}""");
        await Assert.That(page.Items[0].Distance).IsEqualTo(412.5);
        await Assert.That(page.Groups).IsNull();
    }

    [Test]
    public async Task Explain_query_sends_the_flag_and_decodes_the_plan_and_suggestion()
    {
        string? sent = null;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            sent = await request.Content!.ReadAsStringAsync(token);
            return TerraBaseIntegrationFixture.Json(
                """{"items":[],"next_cursor":null,"explain":{"plan":["SCAN items"],"indexes":[],"full_scan":true,"rows_scanned":"9007199254740993","work_units":2400,"elapsed_ms":9.4,"suggested_index":["status"]}}""");
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");

        var page = await db.QueryAsync("items", new TerraBaseQuery
        {
            Filter = JsonDocument.Parse("""{"eq":["/status","open"]}""").RootElement,
            Explain = true,
        });

        await Assert.That(sent).IsEqualTo("""{"filter":{"eq":["/status","open"]},"explain":true}""");
        var explain = page.Explain!;
        await Assert.That(explain.Plan.Single()).IsEqualTo("SCAN items");
        await Assert.That(explain.Indexes.Count).IsEqualTo(0);
        await Assert.That(explain.FullScan).IsTrue();
        await Assert.That(explain.RowsScanned).IsEqualTo(9007199254740993L);
        await Assert.That(explain.WorkUnits).IsEqualTo(2400);
        await Assert.That(explain.ElapsedMs).IsEqualTo(9.4);
        await Assert.That(explain.SuggestedIndex!.Single()).IsEqualTo("status");
    }

    [Test]
    public async Task Explain_without_suggestion_and_queries_without_explain_decode_null()
    {
        var bodies = new List<string>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return TerraBaseIntegrationFixture.Json(bodies.Count == 1
                ? """{"items":[],"next_cursor":null,"count":{"value":"0","exact":true},"explain":{"plan":["SEARCH items USING INDEX tb_index_by_status (key=?)"],"indexes":["by_status"],"full_scan":false,"rows_scanned":1,"work_units":3,"elapsed_ms":0.2}}"""
                : """{"items":[],"next_cursor":null}""");
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");

        var explained = await db.QueryAsync("items", new TerraBaseQuery { Count = true, Explain = true });
        var plain = await db.QueryAsync("items", new TerraBaseQuery { Limit = 1 });

        await Assert.That(bodies[0]).IsEqualTo("""{"count":true,"explain":true}""");
        await Assert.That(bodies[1]).IsEqualTo("""{"limit":1}""");
        await Assert.That(explained.Explain!.FullScan).IsFalse();
        await Assert.That(explained.Explain.Indexes.Single()).IsEqualTo("by_status");
        await Assert.That(explained.Explain.SuggestedIndex).IsNull();
        await Assert.That(plain.Explain).IsNull();
    }

    [Test]
    public async Task Invalid_geo_and_aggregate_queries_are_rejected_before_any_request()
    {
        using var handler = new TerraBaseTestHandler((_, _) => throw new HttpRequestException("sent"));
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var near = TerraBaseSort.NearPoint("/loc", 0, 0);
        TerraBaseAggregate aggregate = new() { Sum = ["/amount"] };

        await Assert.That(() => TerraBaseFilter.WithinRadius("/loc", 0, 0, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => TerraBaseFilter.WithinRadius("/loc", 0, 0, 20_000_001)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => TerraBaseFilter.WithinRadius("/loc", 91, 0, 10)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => TerraBaseFilter.WithinRadius("$id", 0, 0, 10)).Throws<ArgumentException>();
        await Assert.That(() => TerraBaseFilter.WithinBox("/loc", 10, 0, -10, 1)).Throws<ArgumentException>();
        await Assert.That(() => TerraBaseFilter.WithinBox("/loc", 0, 0, 1, 181)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => TerraBaseSort.NearPoint("/loc", double.NaN, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("places", new() { Sort = [near, new("/name")] }));
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("places", new() { Sort = [near], Limit = 1001 }));
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("orders", new() { Aggregate = aggregate, Sort = [new("/amount")] }));
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("orders", new() { Aggregate = aggregate, Project = ["/amount"] }));
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("orders", new() { Aggregate = aggregate, Cursor = "c" }));
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("orders", new() { Aggregate = aggregate, Count = true }));
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("orders", new() { Aggregate = new() { GroupBy = ["/a", "/b", "/c", "/d", "/e"] } }));
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("orders", new() { Aggregate = new() { Sum = [.. Enumerable.Range(0, 17).Select(i => $"/n{i}")] } }));
    }
}
