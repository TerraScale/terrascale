using System.Net;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseQueryParityTests
{
    private const string QueryId = "01JA0000000000000000000002";
    private const string Saved = """{"id":"01JA0000000000000000000002","name":"recent","sql":"SELECT * FROM items LIMIT 1","createdAt":"2026-10-22T00:00:00Z","updatedAt":null}""";
    private const string Plan = """{"ok":true,"error":null,"collection":"items","hasLimit":true,"plan":["SEARCH"],"indexes":["items_title"],"suggestions":[{"field":"title","reason":"unindexed"}]}""";

    [Test]
    public async Task Saved_queries_use_project_routes_ULIDs_and_after_pagination()
    {
        var calls = new List<(string Method, string Path, string Query, string? Body)>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("project-user-token");
            calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            return request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : TerraBaseIntegrationFixture.Json(request.RequestUri.Query.Length > 0 ? "{\"items\":[" + Saved + "],\"next\":\"" + QueryId + "\"}" : Saved, request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var project = TerraBaseIntegrationFixture.Client(http).Project(TerraBaseIntegrationFixture.ProjectId);
        var created = await project.CreateSavedQueryAsync("recent", "SELECT * FROM items LIMIT 1");
        var page = await project.ListSavedQueriesAsync(QueryId, 1);
        var found = await project.GetSavedQueryAsync(QueryId);
        await project.UpdateSavedQueryAsync(QueryId, "recent", found.Sql);
        await project.DeleteSavedQueryAsync(QueryId);
        await Assert.That(created.Id).IsEqualTo(QueryId);
        await Assert.That(created.UpdatedAt).IsNull();
        await Assert.That(page.Next).IsEqualTo(QueryId);
        await Assert.That(page.Items.Single()).IsEqualTo(found);
        await Assert.That(calls.Select(c => c.Method)).IsEquivalentTo(["POST", "GET", "GET", "PUT", "DELETE"]);
        await Assert.That(calls[1].Query).IsEqualTo($"?after={QueryId}&limit=1");
        await Assert.That(calls[2].Path).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/saved-queries/{QueryId}");
        using var body = JsonDocument.Parse(calls[0].Body!);
        await Assert.That(body.RootElement.GetProperty("sql").GetString()).IsEqualTo(found.Sql);
        await Assert.That(calls[3].Body).IsEqualTo(calls[0].Body);
        await Assert.ThrowsAsync<ArgumentException>(() => project.GetSavedQueryAsync("query_prefixed"));
        await Assert.That(calls.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Check_explain_and_shapes_preserve_plans_nullable_errors_and_int64_counts()
    {
        var calls = new List<(string Path, string? Body)>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls.Add((request.RequestUri!.AbsolutePath, request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            return TerraBaseIntegrationFixture.Json(request.Method == HttpMethod.Get
                ? """{"items":[{"id":"sha256-shape","collection":"items","shape":"SELECT * FROM items WHERE title = ?","queries":9007199254740993,"totalLatencyMs":2.5,"maxLatencyMs":1.5,"rowsScanned":9007199254740994,"lastSeenAt":"2026-10-22T00:00:00Z"},{"id":"sha256-p95","collection":"items","shape":"SELECT * FROM items","queries":20,"totalLatencyMs":60,"maxLatencyMs":7.5,"rowsScanned":40,"lastSeenAt":"2026-10-23T00:00:00Z","latencyBuckets":[0,10,9,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"p95LatencyMs":4}]}"""
                : calls.Count == 2 ? """{"ok":false,"error":"invalid SQL","collection":null,"hasLimit":false,"plan":[],"indexes":[],"suggestions":[]}""" : Plan);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var plan = await db.ExplainSqlAsync("SELECT * FROM items LIMIT 1");
        var invalid = await db.CheckSqlAsync("invalid");
        var shapes = await db.ListQueryShapesAsync();
        await Assert.That(plan.Indexes.Single()).IsEqualTo("items_title");
        await Assert.That(plan.Suggestions.Single().Field).IsEqualTo("title");
        await Assert.That(invalid.Ok).IsFalse();
        await Assert.That(invalid.Collection).IsNull();
        await Assert.That(shapes.Items[0].Queries).IsEqualTo(9007199254740993L);
        await Assert.That(shapes.Items[0].RowsScanned).IsEqualTo(9007199254740994L);
        await Assert.That(shapes.Items[0].LatencyBuckets).IsNull();
        await Assert.That(shapes.Items[0].P95LatencyMs).IsNull();
        await Assert.That(shapes.Items[1].LatencyBuckets!.Count).IsEqualTo(20);
        await Assert.That(shapes.Items[1].LatencyBuckets![2]).IsEqualTo(9);
        await Assert.That(shapes.Items[1].P95LatencyMs).IsEqualTo(4);
        await Assert.That(calls[0].Path).EndsWith("/databases/app/sql/explain");
        await Assert.That(calls[1].Path).EndsWith("/databases/app/sql/check");
        await Assert.That(calls[2].Path).EndsWith("/databases/app/query-shapes");
        await Assert.That(calls[0].Body).IsEqualTo("{\"sql\":\"SELECT * FROM items LIMIT 1\"}");
    }

    [Test]
    [Arguments(400, "invalid_request")]
    [Arguments(403, "forbidden_scope")]
    [Arguments(404, "not_found")]
    [Arguments(503, "storage_unavailable")]
    public async Task Query_failures_keep_codes_and_send_once(int status, string code)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json($$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""", (HttpStatusCode)status));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => db.ExplainSqlAsync("SELECT * FROM items"));
        await Assert.That(error!.ErrorCode).IsEqualTo(code);
        await Assert.That(calls).IsEqualTo(1);
    }
}
