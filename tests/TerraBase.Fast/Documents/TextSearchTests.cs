using System.Net;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class TextSearchTests
{
    private static readonly string[] NativeStrings = ["native"];
    private static readonly string[] ProfileFields = ["profile.body", "title", "summary", "tags"];
    private static readonly string[] BodyFields = ["body"];
    private static readonly string[] BodyTextFields = ["body", "text"];
    private static readonly string[] BodyTitleFields = ["body", "title"];
    private static readonly string[] ExtraBodyFields = ["items_extra", "body"];
    private static readonly string[] NameCollisionCollections = ["items_extra", "text"];
    private static readonly string[] IndexKinds = ["btree", "text"];
    private static readonly string[] LargeMatchIds = ["00000000000000000000010001"];
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
    public async Task Ranked_pages_preserve_unequal_scores_and_equal_score_id_ties()
    {
        await using var api = await DocumentApi.StartAsync();
        var ids = new[] { DocumentApi.NewId(), DocumentApi.NewId(), DocumentApi.NewId() }.Order(StringComparer.Ordinal).ToArray();
        (await api.PutAsync(ids[0], new { body = "native" })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(ids[1], new { body = "native" })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(ids[2], new { body = "native " + string.Join(' ', Enumerable.Repeat("unrelated", 100)) })).Status.ShouldBe(HttpStatusCode.Created);
        await CreateAsync(api, BodyFields);
        var filter = Search("/body", "native");
        var sort = new[] { new { path = "$score", order = "desc" } };
        var all = await api.QueryAsync(new { filter, sort });
        await Assert.That(all.Status).IsEqualTo(HttpStatusCode.OK);
        var expected = all.Json.GetProperty("items").EnumerateArray()
            .Select(item => (Id: item.GetProperty("id").GetString()!, Score: item.GetProperty("score").GetDouble())).ToArray();
        await Assert.That(expected[0].Score).IsGreaterThan(expected[^1].Score);
        await Assert.That(expected[0].Score).IsEqualTo(expected[1].Score);
        await Assert.That(expected.Select(item => item.Id).SequenceEqual(ids, StringComparer.Ordinal)).IsTrue();
        string? cursor = null;
        for (var i = 0; i < expected.Length; i++)
        {
            var page = await api.QueryAsync(new { filter, sort, limit = 1, cursor });
            await Assert.That(page.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(page.Ids).IsEquivalentTo([expected[i].Id]);
            await Assert.That(page.Json.GetProperty("items")[0].GetProperty("score").GetDouble()).IsEqualTo(expected[i].Score);
            cursor = page.Json.GetProperty("next_cursor").GetString();
            await Assert.That(cursor is null).IsEqualTo(i == expected.Length - 1);
        }
    }

    [Test]
    public async Task Indexed_candidates_bound_search_count_and_score_sort_in_a_large_collection()
    {
        await using var api = await DocumentApi.StartAsync();
        var tenant = await api.TenantOfAsync();
        var database = new ManagedDatabaseRef(tenant.OrganizationId!, tenant.ProjectId!, tenant.DatabaseId!, DocumentApi.Database);
        await ManagementApi.ExecuteStoredAsync(api.Server.DataDirectory, database, """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 10001)
            INSERT INTO items (id, doc) SELECT printf('%026d', n),
                json_object('body', CASE WHEN n = 10001 THEN 'commonterm rareterm' ELSE 'commonterm' END,
                    'padding', $padding) FROM rows;
            INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2)
                SELECT 'items', id, '', 65536, 0, 1, 0, 0 FROM items;
            """, ("$padding", new string('x', 1000)));
        await CreateAsync(api, BodyFields);
        var filter = Search("/body", "rareterm");
        object[][] sorts = [[], [new { path = "$score", order = "desc" }], [new { path = "/body", order = "asc" }]];
        foreach (var sort in sorts)
        {
            var page = await api.QueryAsync(new { filter, sort, count = true });
            await Assert.That(page.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(page.Ids).IsEquivalentTo(LargeMatchIds);
            await Assert.That(page.Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("1");
        }

        var counted = await api.TransactAsync(new { op = "assert_count", collection = "items", filter, gte = 1, lte = 1 });
        await Assert.That(counted.Status).IsEqualTo(HttpStatusCode.OK);
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var broad = await api.QueryAsync(new { filter = Search("/body", "commonterm"), limit = 1 });
        await Assert.That(broad.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((broad.Code, broad.Message)).IsEqualTo(("invalid_query", "query too expensive"));
        await Assert.That((await api.QueryAsync(new { filter })).Ids).IsEquivalentTo(LargeMatchIds);
    }

    [Test]
    public async Task Text_and_btree_default_names_do_not_collide()
    {
        await using var api = await DocumentApi.StartAsync();
        using var owner = api.Management.As(api.Tenant.User);
        var path = Uri(await IndexPathAsync(api));
        using var btree = await owner.PostAsJsonAsync(path, new { collection = "items", fields = BodyTextFields });
        using var text = await owner.PostAsJsonAsync(path, new { collection = "items", kind = "text", fields = BodyFields });
        await Assert.That(btree.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(text.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var indexes = (await owner.GetFromJsonAsync<JsonElement>(path)).GetProperty("items").EnumerateArray().ToArray();
        await Assert.That(indexes.Length).IsEqualTo(2);
        await Assert.That(indexes.Select(index => index.GetProperty("name").GetString()!).Distinct().Count()).IsEqualTo(2);
        await Assert.That(indexes.Select(index => index.GetProperty("kind").GetString()!)).IsEquivalentTo(IndexKinds);
    }

    [Test]
    public async Task Hashed_default_names_distinguish_kind_from_collection_and_field_boundaries()
    {
        await using var api = await DocumentApi.StartAsync();
        foreach (var name in NameCollisionCollections)
        {
            (await api.SendAsync(HttpMethod.Post, api.Collections, new { name })).Status.ShouldBe(HttpStatusCode.Created);
        }

        using var owner = api.Management.As(api.Tenant.User);
        var path = Uri(await IndexPathAsync(api));
        using var text = await owner.PostAsJsonAsync(path, new { collection = "items_extra", kind = "text", fields = BodyFields });
        using var btree = await owner.PostAsJsonAsync(path, new { collection = "text", fields = ExtraBodyFields });
        await Assert.That(text.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(btree.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var names = (await owner.GetFromJsonAsync<JsonElement>(path)).GetProperty("items").EnumerateArray()
            .Select(index => index.GetProperty("name").GetString()!).ToArray();
        await Assert.That(names.Length).IsEqualTo(2);
        await Assert.That(names[0][..^9]).IsEqualTo(names[1][..^9]);
        await Assert.That(names.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(2);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Native_text_index_names_do_not_claim_customer_collection_names(bool collectionFirst)
    {
        await using var api = await DocumentApi.StartAsync();
        const string Collection = "tb_text_text_docs__0";
        if (collectionFirst)
        {
            (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = Collection })).Status.ShouldBe(HttpStatusCode.Created);
        }

        await CreateAsync(api, BodyFields);
        if (!collectionFirst)
        {
            (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = Collection })).Status.ShouldBe(HttpStatusCode.Created);
        }

        var id = DocumentApi.NewId();
        var document = $"{api.Collections}/{Collection}/documents/by-key?id={id}";
        (await api.SendAsync(HttpMethod.Put, document, new { body = "customer" })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(id, new { body = "native" })).Status.ShouldBe(HttpStatusCode.Created);
        await Assert.That((await api.QueryAsync(new { filter = Search("/body", "native") })).Ids).IsEquivalentTo([id]);
        await Assert.That((await api.QueryAsync(new { filter = Search("/body", "customer") })).Ids).IsEmpty();
        var customer = await api.SendAsync(HttpMethod.Get, document);
        await Assert.That(customer.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(customer.Json.GetProperty("document").GetProperty("body").GetString()).IsEqualTo("customer");
        var reserved = await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "fts_dir__tb_fts_index_text_docs__0" });
        await Assert.That((reserved.Status, reserved.Code)).IsEqualTo((HttpStatusCode.BadRequest, "reserved_name"));
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

    [Test]
    public async Task Search_requires_a_matching_index_and_rejects_invalid_terms_and_score_combinations()
    {
        await using var api = await DocumentApi.StartAsync();
        var missing = await api.QueryAsync(new { filter = Search("/body", "native") });
        await Assert.That((missing.Status, missing.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_query"));
        await CreateAsync(api, BodyFields);
        (await api.PutAsync(DocumentApi.NewId(), new { body = "native" })).Status.ShouldBe(HttpStatusCode.Created);
        var where = System.Uri.EscapeDataString("""{"not":{"search":["/body","native"]}}""");
        var shape = await api.SendAsync(HttpMethod.Get, $"{api.Collections}/items/shape?offset=-1&where={where}");
        await Assert.That((shape.Status, shape.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_query"));
        foreach (var request in new object[]
        {
            new { filter = Search("$id", "native") },
            new { filter = Search("/body", "") },
            new { filter = Search("/body", new string('é', 2049)) },
            new { filter = Search("/body", "native\0search") },
            new { filter = Search("/body", new string('(', 65) + "native" + new string(')', 65)) },
            new { filter = Search("/body", "native"), sort = new[] { new { path = "$score", order = "asc" } } },
            new { sort = new[] { new { path = "$score", order = "desc" } } },
            new { filter = Search("/body.with.dot", "native") },
        })
        {
            var result = await api.QueryAsync(request);
            await Assert.That((result.Status, result.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_query"));
        }

        const string Malformed = "no_such_field:private_search_term";
        var invalid = await api.QueryAsync(new { filter = Search("/body", Malformed), count = true });
        await Assert.That((invalid.Status, invalid.Code, invalid.Message)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_query", "invalid search terms"));
        var invalidCount = await api.TransactAsync(new { op = "assert_count", collection = "items", filter = Search("/body", Malformed), gte = 0 });
        await Assert.That((invalidCount.Status, invalidCount.Code, invalidCount.Message)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_query", "invalid search terms"));
    }

    [Test]
    public async Task Dropping_the_last_covering_index_invalidates_cached_searches()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { body = "native" })).Status.ShouldBe(HttpStatusCode.Created);
        await CreateAsync(api, BodyFields);
        using var owner = api.Management.As(api.Tenant.User);
        var indexes = await IndexPathAsync(api);
        using var second = await owner.PostAsJsonAsync(Uri(indexes), new { name = "second_text", collection = "items", kind = "text", fields = BodyTitleFields });
        second.EnsureSuccessStatusCode();
        var request = new { filter = Search("/body", "native"), count = true };
        await Assert.That((await api.QueryAsync(request)).Ids).IsEquivalentTo([id]);
        using var firstDrop = await owner.DeleteAsync(Uri($"{indexes}/text_docs"));
        firstDrop.EnsureSuccessStatusCode();
        await Assert.That((await api.QueryAsync(request)).Ids).IsEquivalentTo([id]);
        using var lastDrop = await owner.DeleteAsync(Uri($"{indexes}/second_text"));
        lastDrop.EnsureSuccessStatusCode();
        var missing = await api.QueryAsync(request);
        await Assert.That((missing.Status, missing.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_query"));
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
