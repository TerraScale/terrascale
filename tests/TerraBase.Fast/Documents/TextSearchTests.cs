using System.Net;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class TextSearchTests
{
    private static readonly string[] NativeStrings = ["native"];
    private static readonly string[] ProfileFields = ["profile.body", "title", "summary", "tags"];
    private static readonly string[] BodyFields = ["body"];
    private static readonly object[] GuideCategory = ["/category", "guide"];

    [Test]
    public async Task Text_indexes_backfill_strings_list_their_kind_and_combine_boolean_filters()
    {
        await using var api = await DocumentApi.StartAsync();
        var (first, second) = (DocumentApi.NewId(), DocumentApi.NewId());
        (await api.PutAsync(first, new { profile = new { body = "Native search database" }, category = "guide" })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(second, new { profile = new { body = "Native search" }, category = "note" })).Status.ShouldBe(HttpStatusCode.Created);
        foreach (var body in new object?[] { null, 42, true, NativeStrings, new { text = "native" } })
        {
            (await api.PutAsync(DocumentApi.NewId(), new { profile = new { body } })).Status.ShouldBe(HttpStatusCode.Created);
        }

        using var owner = api.Management.As(api.Tenant.User);
        var indexes = await IndexPathAsync(api);
        using var created = await owner.PostAsJsonAsync(Uri(indexes), new { name = "text_docs", collection = "items", kind = "text", fields = ProfileFields });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var list = await owner.GetFromJsonAsync<JsonElement>(Uri(indexes));
        var index = list.GetProperty("items")[0];
        await Assert.That(index.GetProperty("kind").GetString()).IsEqualTo("text");
        await Assert.That(index.GetProperty("fields").GetArrayLength()).IsEqualTo(4);

        var search = Search("/profile/body", "NATIVE");
        var all = await api.QueryAsync(new { filter = search, count = true });
        await Assert.That(all.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(all.Ids).IsEquivalentTo([first, second]);
        await Assert.That(all.Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("2");
        var filtered = await api.QueryAsync(new { filter = new { and = new object[] { search, new { eq = GuideCategory } } } });
        await Assert.That(filtered.Ids).IsEquivalentTo([first]);
        var negated = await api.QueryAsync(new { filter = new { and = new object[] { new { exists = "/category" }, new { not = search } } } });
        await Assert.That(negated.Ids).IsEmpty();
        var either = await api.QueryAsync(new { filter = new { or = new object[] { Search("/title", "absent"), search } } });
        await Assert.That(either.Ids).IsEquivalentTo([first, second]);
        var mixed = await api.QueryAsync(new { filter = new { or = new object[] { Search("/title", "absent"), new { eq = GuideCategory } } }, count = true });
        await Assert.That(mixed.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(mixed.Ids).IsEquivalentTo([first]);
        await Assert.That(mixed.Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("1");
    }

    [Test]
    public async Task Ranked_pages_keep_score_and_id_in_the_cursor_and_break_equal_scores_by_id()
    {
        await using var api = await DocumentApi.StartAsync();
        var ids = new[] { DocumentApi.NewId(), DocumentApi.NewId(), DocumentApi.NewId() }.Order(StringComparer.Ordinal).ToArray();
        foreach (var id in ids.Reverse())
        {
            (await api.PutAsync(id, new { body = "native search" })).Status.ShouldBe(HttpStatusCode.Created);
        }

        await CreateAsync(api, BodyFields);
        var filter = Search("/body", "native");
        var sort = new[] { new { path = "$score", order = "desc" } };
        var page = await api.QueryAsync(new { filter, sort, limit = 1 });
        var seen = new List<string>();
        for (var remaining = ids.Length; remaining > 0; remaining--)
        {
            await Assert.That(page.Status).IsEqualTo(HttpStatusCode.OK);
            seen.AddRange(page.Ids);
            await Assert.That(page.Json.GetProperty("items")[0].GetProperty("score").GetDouble()).IsGreaterThan(0);
            var cursor = page.Json.GetProperty("next_cursor").GetString();
            if (cursor is null)
            {
                break;
            }

            using var decoded = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(cursor));
            await Assert.That(decoded.RootElement.GetArrayLength()).IsEqualTo(4);
            await Assert.That(decoded.RootElement[2].GetInt64()).IsEqualTo(4);
            await Assert.That(decoded.RootElement[3].GetDouble()).IsGreaterThan(0);
            page = await api.QueryAsync(new { filter, sort, limit = 1, cursor });
        }

        await Assert.That(seen.SequenceEqual(ids, StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Text_indexes_follow_updates_deletes_rollback_drop_and_recreate()
    {
        await using var api = await DocumentApi.StartAsync();
        await CreateAsync(api, BodyFields);
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { body = "original" })).Status.ShouldBe(HttpStatusCode.Created);
        var guardId = DocumentApi.NewId();
        (await api.PutAsync(guardId, new { guard = true })).Status.ShouldBe(HttpStatusCode.Created);
        var failed = await api.TransactAsync(
            new { op = "put", collection = "items", id, document = new { body = "rolledback" } },
            new { op = "create", collection = "items", id = guardId, document = new { guard = false } });
        await Assert.That(failed.Status).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That((failed.Code, failed.Op)).IsEqualTo(("precondition_failed", 1));
        await Assert.That((await api.QueryAsync(new { filter = Search("/body", "original") })).Ids).IsEquivalentTo([id]);
        await Assert.That((await api.QueryAsync(new { filter = Search("/body", "rolledback") })).Ids).IsEmpty();
        (await api.PutAsync(id, new { body = "replacement" })).Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That((await api.QueryAsync(new { filter = Search("/body", "original") })).Ids).IsEmpty();
        await Assert.That((await api.QueryAsync(new { filter = Search("/body", "replacement") })).Ids).IsEquivalentTo([id]);
        var deleted = await api.SendAsync(HttpMethod.Delete, api.Document(id));
        deleted.Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That(deleted.Json.GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(deleted.Version).Matches("^tbv1-[0-9a-f]{32}$");
        await Assert.That(deleted.ETag).IsEqualTo(deleted.Version);
        var gone = await api.GetAsync(id);
        await Assert.That((gone.Status, gone.Code)).IsEqualTo((HttpStatusCode.NotFound, "not_found"));
        await Assert.That(gone.ETag).IsEqualTo(deleted.Version);
        await Assert.That((await api.QueryAsync(new { filter = Search("/body", "replacement") })).Ids).IsEmpty();

        using var owner = api.Management.As(api.Tenant.User);
        using var dropped = await owner.DeleteAsync(Uri($"{await IndexPathAsync(api)}/text_docs"));
        await Assert.That(dropped.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await api.QueryAsync(new { filter = Search("/body", "replacement") })).Code).IsEqualTo("invalid_query");
        await CreateAsync(api, BodyFields);
        await Assert.That((await api.QueryAsync(new { filter = Search("/body", "replacement") })).Ids).IsEmpty();
        var collection = await api.SendAsync(HttpMethod.Get, $"{api.Collections}/items");
        collection.Status.ShouldBe(HttpStatusCode.OK);
        var droppedCollection = await api.SendAsync(HttpMethod.Delete, $"{api.Collections}/items");
        droppedCollection.Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That(droppedCollection.Json.GetProperty("name").GetString()).IsEqualTo("items");
        await Assert.That(droppedCollection.Json.GetProperty("id").GetString()).IsEqualTo(collection.Json.GetProperty("id").GetString());
        (await api.SendAsync(HttpMethod.Get, $"{api.Collections}/items")).Status.ShouldBe(HttpStatusCode.NotFound);
        var indexes = await owner.GetFromJsonAsync<JsonElement>(Uri(await IndexPathAsync(api)));
        await Assert.That(indexes.GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    private static object Search(string path, string terms) => new { search = new[] { path, terms } };

    private static async Task CreateAsync(DocumentApi api, params string[] fields)
    {
        using var owner = api.Management.As(api.Tenant.User);
        using var response = await owner.PostAsJsonAsync(Uri(await IndexPathAsync(api)), new { name = "text_docs", collection = "items", kind = "text", fields });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> IndexPathAsync(DocumentApi api) =>
        $"/v1/management/organizations/{api.Tenant.OrganizationId}/projects/{api.Tenant.ProjectId}/databases/{(await api.TenantOfAsync()).DatabaseId}/indexes";

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
