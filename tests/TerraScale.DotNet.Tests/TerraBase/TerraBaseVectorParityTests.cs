using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseVectorParityTests
{
    [Test]
    [Arguments(false, 101)]
    [Arguments(false, 1000)]
    [Arguments(true, 101)]
    [Arguments(true, 1000)]
    public async Task Vector_and_geo_near_accept_producer_limit(bool geo, int limit)
    {
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/collections/items/query");
            await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("project-user-token");
            var near = geo ? "\"lat\":0,\"lon\":0" : "\"vector\":[1],\"metric\":\"cosine\"";
            await Assert.That(await request.Content!.ReadAsStringAsync(token)).IsEqualTo($$$"""{"sort":[{"near":{"path":"/value",{{{near}}}}}],"limit":{{{limit}}}}""");
            return TerraBaseIntegrationFixture.Json("""{"items":[],"next_cursor":null}""");
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var sort = geo ? TerraBaseSort.NearPoint("/value", 0, 0) : TerraBaseSort.Near("/value", [1f]);
        var page = await db.QueryAsync("items", new() { Sort = [sort], Limit = limit });
        await Assert.That(page.NextCursor).IsNull();
    }

    [Test]
    [Arguments("building", 1)]
    [Arguments("active", 4096)]
    public async Task Vector_index_request_and_create_list_responses_preserve_dimensions(string state, int dimensions)
    {
        var index = $$$"""{"name":"items_vector","collection":"items","fields":["embedding"],"state":"{{{state}}}","kind":"vector","dimensions":{{{dimensions}}}}""";
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/indexes");
            await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("project-user-token");
            if (request.Method == HttpMethod.Post)
            {
                await Assert.That(request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
                await Assert.That(await request.Content.ReadAsStringAsync(token)).IsEqualTo($$$"""{"collection":"items","fields":["embedding"],"kind":"vector","dimensions":{{{dimensions}}}}""");
                return TerraBaseIntegrationFixture.Json(index, HttpStatusCode.Created);
            }

            await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
            return TerraBaseIntegrationFixture.Json("{\"items\":[" + index + "]}");
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var created = await db.CreateIndexAsync(new TerraBaseCreateIndexRequest("items", ["embedding"], Kind: "vector", Dimensions: dimensions));
        var listed = (await db.ListIndexesAsync()).Items.Single();
        foreach (var result in new[] { created, listed })
        {
            await Assert.That(result.State).IsEqualTo(state);
            await Assert.That(result.Kind).IsEqualTo("vector");
            await Assert.That(result.Dimensions).IsEqualTo(dimensions);
        }
    }

    [Test]
    public async Task Invalid_vector_index_and_near_requests_do_not_send()
    {
        using var handler = new TerraBaseTestHandler((_, _) => throw new HttpRequestException("sent"));
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var valid = new TerraBaseCreateIndexRequest("items", ["embedding"], Kind: "vector", Dimensions: 2);
        foreach (var invalid in new[] { valid with { Dimensions = null }, valid with { Dimensions = 0 }, valid with { Dimensions = 4097 }, valid with { Fields = [] }, valid with { Fields = ["a", "b"] }, valid with { Kind = "btree" }, valid with { Kind = "text" }, valid with { Kind = null }, valid with { Kind = "unknown" } })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => db.CreateIndexAsync(invalid));
        }

        foreach (var sort in new[] { TerraBaseSort.Near("/value", [1f]), TerraBaseSort.NearPoint("/value", 0, 0) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("items", new() { Sort = [sort], Limit = 0 }));
            await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("items", new() { Sort = [sort], Limit = 1001 }));
            await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("items", new() { Sort = [sort], Cursor = "next" }));
            await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("items", new() { Sort = [sort], Filter = TerraBaseFilter.Search("/title", "fox") }));
            await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("items", new() { Sort = [sort, new("/name")] }));
        }
    }

    [Test]
    public async Task Vector_index_failure_is_not_retried_and_caller_cancellation_is_preserved()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            var response = TerraBaseIntegrationFixture.Json("""{"error":{"code":"unavailable","message":"busy"}}""", HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new(TimeSpan.Zero);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var request = new TerraBaseCreateIndexRequest("items", ["embedding"], Kind: "vector", Dimensions: 2);
        await Assert.ThrowsAsync<TerraBaseException>(() => db.CreateIndexAsync(request));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.CreateIndexAsync(request, cancelled.Token));
        await Assert.That(calls).IsEqualTo(1);
    }
}
