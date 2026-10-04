using System.Net;
using System.Text.Json;

namespace TerraScale.TerraBase.Fast.Documents;

public sealed class CollectionApiTests
{
    private const string Collections = $"/v1/databases/{DocumentApi.Database}/collections";

    [Test]
    public async Task Collection_routes_enforce_read_and_write_scopes_without_mutating_on_denial()
    {
        await using var api = await DocumentApi.StartAsync();
        using var reader = await api.ClientWithScopesAsync("documents:read");
        using var writer = await api.ClientWithScopesAsync("documents:write");
        using var createBody = JsonContent.Create(new { name = "denied" });
        var create = await DocumentApi.SendAsync(reader, HttpMethod.Post, Collections, createBody);
        var drop = await DocumentApi.SendAsync(reader, HttpMethod.Delete, Collections + "/items");
        var get = await DocumentApi.SendAsync(writer, HttpMethod.Get, Collections + "/items");
        var list = await DocumentApi.SendAsync(writer, HttpMethod.Get, Collections);
        foreach (var denied in new[] { create, drop, get, list })
        {
            await Assert.That((denied.Status, denied.Code)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden_scope"));
        }

        var intact = await DocumentApi.SendAsync(reader, HttpMethod.Get, Collections + "/items");
        var absent = await DocumentApi.SendAsync(reader, HttpMethod.Get, Collections + "/denied");
        await Assert.That(intact.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(absent.Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Collections_are_created_listed_read_and_dropped()
    {
        await using var api = await DocumentApi.StartAsync();
        var names = new[] { "alpha", "beta", "gamma" };
        foreach (var name in names)
        {
            (await api.SendAsync(HttpMethod.Post, Collections, new { name })).Status.ShouldBe(HttpStatusCode.Created);
        }

        var id = DocumentApi.NewId();
        // The write must land, or the emptiness check after the drop below proves nothing.
        (await api.SendAsync(HttpMethod.Put, $"{Collections}/beta/documents/{id}", new { a = 1 })).Status.ShouldBe(HttpStatusCode.Created);

        var created = await api.SendAsync(HttpMethod.Post, Collections, new { name = "delta" });
        var taken = await api.SendAsync(HttpMethod.Post, Collections, new { name = "beta" });
        var read = await api.SendAsync(HttpMethod.Get, $"{Collections}/beta");
        var first = await api.SendAsync(HttpMethod.Get, $"{Collections}?limit=3");
        var second = await api.SendAsync(HttpMethod.Get, $"{Collections}?limit=3&cursor={first.Json.GetProperty("next_cursor").GetString()}");
        var dropped = await api.SendAsync(HttpMethod.Delete, $"{Collections}/beta");
        var readDropped = await api.SendAsync(HttpMethod.Get, $"{Collections}/beta");
        var writeDropped = await api.SendAsync(HttpMethod.Put, $"{Collections}/beta/documents/{id}", new { a = 2 });
        var dropAgain = await api.SendAsync(HttpMethod.Delete, $"{Collections}/beta");
        (await api.SendAsync(HttpMethod.Post, Collections, new { name = "beta" })).Status.ShouldBe(HttpStatusCode.Created);
        var recreatedIsEmpty = await api.SendAsync(HttpMethod.Get, $"{Collections}/beta/documents/{id}");

        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Location!.OriginalString).IsEqualTo($"{Collections}/delta");
        await Assert.That(created.Json.GetProperty("name").GetString()).IsEqualTo("delta");
        await Assert.That(long.Parse(created.Json.GetProperty("created_at").GetString()!, System.Globalization.CultureInfo.InvariantCulture))
            .IsGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());
        await Assert.That((taken.Status, taken.Code)).IsEqualTo((HttpStatusCode.Conflict, "name_taken"));
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
        // Name order, page by page.
        await Assert.That(string.Join(',', Names(first))).IsEqualTo("alpha,beta,delta");
        await Assert.That(string.Join(',', Names(second))).IsEqualTo("gamma,items");
        await Assert.That(second.Json.GetProperty("next_cursor").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(dropped.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(dropped.Json.GetProperty("name").GetString()).IsEqualTo("beta");
        await Assert.That((readDropped.Status, readDropped.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_collection"));
        await Assert.That((writeDropped.Status, writeDropped.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_collection"));
        await Assert.That((dropAgain.Status, dropAgain.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_collection"));
        // Dropping removes the documents, not just the name.
        await Assert.That((recreatedIsEmpty.Status, recreatedIsEmpty.Code)).IsEqualTo((HttpStatusCode.NotFound, "not_found"));
    }

    [Test]
    public async Task Invalid_collection_requests_are_rejected()
    {
        await using var api = await DocumentApi.StartAsync();

        var noDatabase = await api.SendAsync(HttpMethod.Post, "/v1/databases/nope/collections", new { name = "a" });
        var listNoDatabase = await api.SendAsync(HttpMethod.Get, "/v1/databases/nope/collections");
        var badName = await api.SendAsync(HttpMethod.Post, Collections, new { name = "Bad Name" });
        var noName = await api.SendAsync(HttpMethod.Post, Collections, new { });
        var schema = await api.SendAsync(HttpMethod.Post, Collections, new { name = "typed", schema = new { fields = Array.Empty<object>() } });
        var badCursor = await api.SendAsync(HttpMethod.Get, $"{Collections}?cursor=!!");
        var badLimit = await api.SendAsync(HttpMethod.Get, $"{Collections}?limit=201");
        var notJson = await api.SendRawAsync(HttpMethod.Post, Collections, "name=a", "application/x-www-form-urlencoded");

        await Assert.That((noDatabase.Status, noDatabase.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_database"));
        await Assert.That((listNoDatabase.Status, listNoDatabase.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_database"));
        await Assert.That((badName.Status, badName.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request"));
        await Assert.That(noName.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((schema.Status, schema.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request"));
        await Assert.That((badCursor.Status, badCursor.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_cursor"));
        await Assert.That(badLimit.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((notJson.Status, notJson.Code)).IsEqualTo((HttpStatusCode.UnsupportedMediaType, "unsupported_media_type"));
    }

    private static List<string> Names(ApiResponse page) =>
        [.. page.Json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!)];
}
