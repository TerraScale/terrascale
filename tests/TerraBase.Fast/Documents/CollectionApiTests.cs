using TerraBase.Testing;
using System.Net;
using System.Text.Json;

namespace TerraBase.Fast.Documents;

public sealed class CollectionApiTests
{
    [Test]
    public async Task Collection_routes_enforce_read_and_write_scopes_without_mutating_on_denial()
    {
        await using var api = await DocumentApi.StartAsync();
        using var reader = await api.ClientWithScopesAsync("documents:read");
        using var writer = await api.ClientWithScopesAsync("documents:write");
        using var createBody = JsonContent.Create(new { name = "denied" });
        var create = await DocumentApi.SendAsync(reader, HttpMethod.Post, api.Collections, createBody);
        var drop = await DocumentApi.SendAsync(reader, HttpMethod.Delete, api.Collections + "/items");
        var get = await DocumentApi.SendAsync(writer, HttpMethod.Get, api.Collections + "/items");
        var list = await DocumentApi.SendAsync(writer, HttpMethod.Get, api.Collections);
        foreach (var denied in new[] { create, drop, get, list })
        {
            await Assert.That((denied.Status, denied.Code)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden_scope"));
        }

        var intact = await DocumentApi.SendAsync(reader, HttpMethod.Get, api.Collections + "/items");
        var absent = await DocumentApi.SendAsync(reader, HttpMethod.Get, api.Collections + "/denied");
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
            (await api.SendAsync(HttpMethod.Post, api.Collections, new { name })).Status.ShouldBe(HttpStatusCode.Created);
        }

        var id = DocumentApi.NewId();
        // The write must land, or the emptiness check after the drop below proves nothing.
        (await api.SendAsync(HttpMethod.Put, $"{api.Collections}/beta/documents/by-key?id={id}", new { a = 1 })).Status.ShouldBe(HttpStatusCode.Created);

        var created = await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "delta" });
        var taken = await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "beta" });
        var read = await api.SendAsync(HttpMethod.Get, $"{api.Collections}/beta");
        var first = await api.SendAsync(HttpMethod.Get, $"{api.Collections}?limit=3");
        var second = await api.SendAsync(HttpMethod.Get, $"{api.Collections}?limit=3&cursor={first.Json.GetProperty("next_cursor").GetString()}");
        var dropped = await api.SendAsync(HttpMethod.Delete, $"{api.Collections}/beta");
        var readDropped = await api.SendAsync(HttpMethod.Get, $"{api.Collections}/beta");
        var writeDropped = await api.SendAsync(HttpMethod.Put, $"{api.Collections}/beta/documents/by-key?id={id}", new { a = 2 });
        var dropAgain = await api.SendAsync(HttpMethod.Delete, $"{api.Collections}/beta");
        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "beta" })).Status.ShouldBe(HttpStatusCode.Created);
        var recreatedIsEmpty = await api.SendAsync(HttpMethod.Get, $"{api.Collections}/beta/documents/by-key?id={id}");

        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Location!.OriginalString).IsEqualTo($"{api.Collections}/delta");
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

    private static List<string> Names(ApiResponse page) =>
        [.. page.Json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!)];
}
