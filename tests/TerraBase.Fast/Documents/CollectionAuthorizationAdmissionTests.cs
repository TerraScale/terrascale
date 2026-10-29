using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;

namespace TerraBase.Fast.Documents;

public sealed class CollectionAuthorizationAdmissionTests
{
    [Test]
    [Arguments(false, 10_001, 1024)]
    [Arguments(true, 10_001, 1024)]
    [Arguments(false, 9, 1_000_000)]
    public async Task Hidden_rows_do_not_change_owner_sort_list_count_or_assert_count(bool indexed, int hiddenCount, int payloadBytes)
    {
        await using var api = await StartAsync();
        var actor = DocumentApi.NewId();
        var ownId = DocumentApi.NewId();
        (await api.PutAsync(ownId, new { owner = actor, rank = 7 })).Status.ShouldBe(HttpStatusCode.Created);
        using var owner = CollectionAccessRulesTests.Reader(api, actor);
        using var empty = CollectionAccessRulesTests.Reader(api, DocumentApi.NewId());
        await CheckReadsAsync(api, owner, ownId);
        await CheckReadsAsync(api, empty, null);
        var store = await StoreAsync(api);
        await SeedAsync(store, DocumentApi.NewId(), hiddenCount, payloadBytes);
        if (indexed)
        {
            await IndexOwnerAsync(store);
        }

        await CheckReadsAsync(api, owner, ownId);
        await CheckReadsAsync(api, empty, null);
        var filter = new { eq = new object[] { "$id", ownId } };
        var sorted = await api.QueryAsync(new { filter, sort = new[] { new { path = "/rank" } }, limit = 1 });
        await Assert.That((sorted.Status, sorted.Code)).IsEqualTo((HttpStatusCode.BadRequest, "sort_requires_index"));
        var asserted = await api.TransactAsync(new { op = "assert_count", collection = "items", filter, gte = 1, lte = 1 });
        if (hiddenCount > 10_000)
        {
            await Assert.That((asserted.Status, asserted.Code)).IsEqualTo((HttpStatusCode.BadRequest, "assert_requires_index"));
        }
        else
        {
            await Assert.That(asserted.Status).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task Hidden_state_beyond_public_count_limit_does_not_make_owner_count_inexact()
    {
        await using var api = await StartAsync();
        var actor = DocumentApi.NewId();
        var ownId = DocumentApi.NewId();
        (await api.PutAsync(ownId, new { owner = actor, rank = 7 })).Status.ShouldBe(HttpStatusCode.Created);
        var store = await StoreAsync(api);
        await SeedAsync(store, DocumentApi.NewId(), 100_001, 0);
        await IndexOwnerAsync(store);
        using var owner = CollectionAccessRulesTests.Reader(api, actor);
        using var empty = CollectionAccessRulesTests.Reader(api, DocumentApi.NewId());
        await CheckReadsAsync(api, owner, ownId);
        await CheckReadsAsync(api, empty, null);
        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await read.CountAsync("items", null, 100_000, CancellationToken.None)).IsEqualTo((100_000L, false));
    }

    [Test]
    public async Task Submitted_filters_cannot_narrow_the_authorized_candidate_allowance()
    {
        await using var api = await StartAsync();
        var actor = DocumentApi.NewId();
        var ownId = DocumentApi.NewId();
        (await api.PutAsync(ownId, new { owner = actor, rank = 7 })).Status.ShouldBe(HttpStatusCode.Created);
        await SeedAsync(await StoreAsync(api), actor, 10_001, 0);
        using var owner = CollectionAccessRulesTests.Reader(api, actor);
        var filter = new { eq = new object[] { "$id", ownId } };
        var sorted = await CollectionAccessRulesTests.SendAsync(owner, HttpMethod.Post, api.Query,
            new { filter, sort = new[] { new { path = "/rank" } }, limit = 1 });
        await Assert.That((sorted.Status, sorted.Code)).IsEqualTo((HttpStatusCode.BadRequest, "sort_requires_index"));
        var asserted = await CollectionAccessRulesTests.SendAsync(owner, HttpMethod.Post, api.Transactions,
            new { ops = new[] { new { op = "assert_count", collection = "items", filter, gte = 1, lte = 1 } } });
        await Assert.That((asserted.Status, asserted.Code)).IsEqualTo((HttpStatusCode.BadRequest, "assert_requires_index"));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Text_search_requires_collection_wide_read_before_native_scoring_or_admission(bool publicRead)
    {
        await using var api = await StartAsync();
        var actor = DocumentApi.NewId();
        var ownId = DocumentApi.NewId();
        var hiddenId = DocumentApi.NewId();
        (await api.PutAsync(ownId, new { owner = actor, body = "native search" })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(hiddenId, new { owner = DocumentApi.NewId(), body = "native search" })).Status.ShouldBe(HttpStatusCode.Created);
        var store = await StoreAsync(api);
        await using (var write = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await Assert.That(DocumentField.TryParse("body", out var field)).IsTrue();
            await Assert.That(await write.CreateIndexAsync(new IndexDefinition("text_docs", "items", [field], "text"), CancellationToken.None)).IsTrue();
            await write.CommitAsync(CancellationToken.None);
        }

        using var owner = CollectionAccessRulesTests.Reader(api, actor, "reader");
        var search = new { search = new[] { "/body", "native" } };
        foreach (var filter in new object[] { search, new { not = search }, new { or = new object[] { search, new { exists = "/body" } } } })
        {
            var denied = await CollectionAccessRulesTests.SendAsync(owner, HttpMethod.Post, api.Query, new { filter, count = true });
            await Assert.That((denied.Status, denied.Code, denied.Message)).IsEqualTo(
                (HttpStatusCode.BadRequest, "invalid_query", "search requires collection-wide read access"));
        }

        var ranked = new { filter = search, sort = new[] { new { path = "$score", order = "desc" } }, count = true };
        var deniedScore = await CollectionAccessRulesTests.SendAsync(owner, HttpMethod.Post, api.Query, ranked);
        await Assert.That((deniedScore.Status, deniedScore.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_query"));
        var asserted = await CollectionAccessRulesTests.SendAsync(owner, HttpMethod.Post, api.Transactions,
            new { ops = new[] { new { op = "assert_count", collection = "items", filter = search, gte = 1, lte = 1 } } });
        await Assert.That((asserted.Status, asserted.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_query"));

        await CollectionAccessRulesTests.DeclareAsync(api, CollectionAccessRulesTests.OwnerRules() with
        {
            Read = new CollectionAccessPolicy(true, publicRead, publicRead ? [] : ["reader"]),
        });
        var allowed = await CollectionAccessRulesTests.SendAsync(owner, HttpMethod.Post, api.Query, ranked);
        await Assert.That(allowed.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(allowed.Ids).IsEquivalentTo([ownId, hiddenId]);
        await Assert.That(allowed.Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("2");
    }

    private static async Task CheckReadsAsync(DocumentApi api, HttpClient reader, string? ownId)
    {
        var expected = ownId is null ? "0" : "1";
        foreach (var query in new object[]
        {
            new { limit = 1, count = true },
            new { limit = 1, count = true, sort = new[] { new { path = "/rank" } } },
            new { limit = 1, count = true, filter = new { eq = new object[] { "/rank", 7 } } },
        })
        {
            var result = await CollectionAccessRulesTests.SendAsync(reader, HttpMethod.Post, api.Query, query);
            await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(result.Ids).IsEquivalentTo(ownId is null ? Array.Empty<string>() : [ownId]);
            await Assert.That(result.Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo(expected);
            await Assert.That(result.Json.GetProperty("count").GetProperty("exact").GetBoolean()).IsTrue();
            await Assert.That(result.Json.GetProperty("next_cursor").ValueKind).IsEqualTo(JsonValueKind.Null);
        }

        var list = await CollectionAccessRulesTests.SendAsync(reader, HttpMethod.Get, api.Documents + "?limit=1");
        await Assert.That(list.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(list.Ids).IsEquivalentTo(ownId is null ? Array.Empty<string>() : [ownId]);
        var count = ownId is null ? 0 : 1;
        var asserted = await CollectionAccessRulesTests.SendAsync(reader, HttpMethod.Post, api.Transactions,
            new
            {
                ops = new object[]
                {
                new { op = "assert_count", collection = "items", gte = count, lte = count },
                new { op = "assert_count", collection = "items", filter = new { exists = "/missing" }, gte = 0, lte = 0 },
                },
            });
        await Assert.That(asserted.Status).IsEqualTo(HttpStatusCode.OK);
    }

    private static async Task<DocumentApi> StartAsync()
    {
        var api = await DocumentApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()));
        try
        {
            using var owner = api.Management.As(api.Tenant.User);
            await api.Management.VerifyProjectBindingAsync(api.Tenant.OrganizationId, api.Tenant.ProjectId, owner);
            await CollectionAccessRulesTests.DeclareAsync(api, CollectionAccessRulesTests.OwnerRules());
            return api;
        }
        catch
        {
            await api.DisposeAsync();
            throw;
        }
    }

    private static async Task<TursoDatabaseStore> StoreAsync(DocumentApi api) =>
        (TursoDatabaseStore)(await api.Server.Services.GetRequiredService<TursoStorageEngine>()
            .OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;

    private static async Task IndexOwnerAsync(TursoDatabaseStore store)
    {
        await using var write = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await Assert.That(DocumentField.TryParse("owner", out var field)).IsTrue();
        await Assert.That(await write.CreateIndexAsync(new IndexDefinition("owner", "items", [field]), CancellationToken.None)).IsTrue();
        await write.CommitAsync(CancellationToken.None);
    }

    private static Task<int> SeedAsync(TursoDatabaseStore store, string owner, int count, int payloadBytes) =>
        store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < $count)
                INSERT INTO "items" (id, doc) SELECT printf('%026d', n), $body FROM rows;
                INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2)
                    SELECT 'items', id, '', 65536, 0, 1, 0, 0 FROM "items" WHERE id <= printf('%026d', $count);
                WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < $count)
                INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2)
                    SELECT 'items', printf('1%025d', n), '', 65536, 0, 0, 0, 0 FROM rows;
                """;
            command.Parameters.AddWithValue("$count", count);
            command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(new { owner, rank = 1, payload = new string('x', payloadBytes) }));
            return command.ExecuteNonQuery();
        }, "authorization admission fixture", CancellationToken.None);
}
