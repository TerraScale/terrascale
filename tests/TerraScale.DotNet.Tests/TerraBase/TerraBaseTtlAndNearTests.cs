using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>Collection TTL routes and the vector near sort against a fake handler; no TerraBase runs.</summary>
public sealed class TerraBaseTtlAndNearTests
{
    private static readonly string Collection = $"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/collections/items";

    [Test]
    public async Task Ttl_methods_send_their_method_path_and_body()
    {
        var calls = new List<(string Method, string Path, string? Body)>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            return TerraBaseIntegrationFixture.Json(request.Method == HttpMethod.Put ? """{"path":"/expiresAt"}""" : """{"path":null}""");
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");

        var set = await db.SetCollectionTtlAsync("items", "/expiresAt");
        var read = await db.GetCollectionTtlAsync("items");
        var cleared = await db.ClearCollectionTtlAsync("items");

        await Assert.That(set.Path).IsEqualTo("/expiresAt");
        await Assert.That(read.Path).IsNull();
        await Assert.That(cleared.Path).IsNull();
        await Assert.That(calls[0]).IsEqualTo(("PUT", Collection + "/ttl", (string?)"""{"path":"/expiresAt"}"""));
        await Assert.That(calls[1]).IsEqualTo(("GET", Collection + "/ttl", (string?)null));
        await Assert.That(calls[2]).IsEqualTo(("DELETE", Collection + "/ttl", (string?)null));
        await Assert.ThrowsAsync<ArgumentException>(() => db.SetCollectionTtlAsync("items", ""));
        await Assert.That(calls.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Near_query_sends_the_exact_sort_and_decodes_distances()
    {
        string? sent = null;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            sent = await request.Content!.ReadAsStringAsync(token);
            return TerraBaseIntegrationFixture.Json(
                """{"items":[{"id":"01JA00000000000000000000D1","version":"tbv1-a","state":"value","document":{"title":"a"},"distance":0.125},{"id":"01JA00000000000000000000D2","version":"tbv1-b","state":"value","document":{}}],"next_cursor":null}""");
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        using var filter = JsonDocument.Parse("""{"eq":["/status","active"]}""");

        var page = await db.QueryAsync("items", new TerraBaseQuery
        {
            Filter = filter.RootElement.Clone(),
            Sort = [TerraBaseSort.Near("/embedding", [0.1f, 0.2f])],
            Project = ["/title"],
            Limit = 10,
        });

        await Assert.That(sent).IsEqualTo(
            """{"filter":{"eq":["/status","active"]},"sort":[{"near":{"path":"/embedding","vector":[0.1,0.2],"metric":"cosine"}}],"project":["/title"],"limit":10}""");
        await Assert.That(page.Items[0].Distance).IsEqualTo(0.125);
        await Assert.That(page.Items[1].Distance).IsNull();
    }

    [Test]
    public async Task Invalid_near_sorts_are_rejected_before_any_request()
    {
        using var handler = new TerraBaseTestHandler((_, _) => throw new HttpRequestException("sent"));
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var near = TerraBaseSort.Near("/embedding", [1f], "l2");

        await Assert.That(() => TerraBaseSort.Near("/embedding", [0f, 0f])).Throws<ArgumentException>();
        await Assert.That(() => TerraBaseSort.Near("/embedding", [float.NaN])).Throws<ArgumentException>();
        await Assert.That(() => TerraBaseSort.Near("/embedding", [])).Throws<ArgumentException>();
        await Assert.That(() => TerraBaseSort.Near("/embedding", [1f], "dot")).Throws<ArgumentException>();
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("items", new() { Sort = [near, new("/title")] }));
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("items", new() { Sort = [near], Limit = 1001 }));
        await Assert.ThrowsAsync<ArgumentException>(() => db.QueryAsync("items", new() { Sort = [near], Cursor = "c" }));
    }
}
