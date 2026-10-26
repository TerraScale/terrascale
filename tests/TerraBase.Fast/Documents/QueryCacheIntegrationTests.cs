using System.Diagnostics.Metrics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Fast.Storage;
using TerraBase.Features.Documents;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class QueryCacheIntegrationTests
{
    private static readonly string[] Projection = ["/label"];

    [Test]
    public async Task Replacing_tenant_documents_preserves_system_reads_and_api_key_authentication()
    {
        await using var api = await DocumentApi.StartAsync(services: services =>
            services.AddSingleton<IDocumentService>(provider => new TenantDocuments(provider.GetRequiredService<DocumentService>())));
        var documents = api.Server.Services.GetRequiredService<IDocumentService>();
        var system = api.Server.Services.GetRequiredService<ISystemDocumentReader>();
        await Assert.That(documents is ISystemDocumentReader).IsFalse();
        await Assert.That(ReferenceEquals(system, api.Server.Services.GetRequiredService<DocumentService>())).IsTrue();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        var read = await api.GetAsync(id);
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        var replacement = (TenantDocuments)documents;
        await Assert.That(replacement.Reads).IsGreaterThan(0);
        await Assert.That(replacement.Writes).IsGreaterThan(0);
    }

    [Test]
    [Arguments("point")]
    [Arguments("query")]
    [Arguments("list")]
    [Arguments("system")]
    public async Task Caller_mutations_of_first_and_cached_json_do_not_change_later_reads(string kind)
    {
        await using var api = await DocumentApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()));
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        var service = api.Server.Services.GetRequiredService<IDocumentService>();
        var database = await api.TenantOfAsync();
        var system = api.Server.Services.GetRequiredService<ISystemDocumentReader>();
        var keyId = await api.Server.Services.GetRequiredService<TerraBase.Features.ApiKeys.IApiKeyStore>().FindAsync(
            api.Client.DefaultRequestHeaders.Authorization!.Parameter!, CancellationToken.None) switch
        {
            TerraBase.Features.ApiKeys.ApiKey key => key.Id,
            NotFound => throw new InvalidOperationException("The test API key must exist."),
        };
        if (kind == "system")
        {
            // Rewriting the real key invalidates its warmed auth entry, exercising first admission too.
            var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            var key = (await transaction.GetAsync("api_keys", keyId, CancellationToken.None))!;
            await transaction.PutAsync("api_keys", keyId, key.Json, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var document = await ReadAsync();
        var original = document.Json.ToArray();
        for (var read = 0; read < 2; read++)
        {
            await Assert.That(document.Json.ToArray()).IsEquivalentTo(original);
            MemoryMarshal.AsMemory(document.Json).Span.Fill((byte)'?');
            document = await ReadAsync();
        }
        await Assert.That((await ReadAsync()).Json.ToArray()).IsEquivalentTo(original);
        var http = await api.GetAsync(id);
        await Assert.That(http.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(http.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        var queried = await api.QueryAsync(new { count = true });
        await Assert.That(queried.Status).IsEqualTo(HttpStatusCode.OK)
            .Because($"count query: {queried.Code}: {queried.Message}");
        await Assert.That(queried.Ids).IsEquivalentTo([id]);
        await Assert.That(queried.Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("1");
        using var listed = await api.Client.GetAsync(new Uri(api.Documents, UriKind.Relative));
        var listedBody = await listed.Content.ReadAsStringAsync();
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK)
            .Because($"uncached list: {listedBody[..Math.Min(listedBody.Length, 4096)]}");
        await Assert.That(listedBody).Contains("\"n\":1");

        async Task<StoredDocument> ReadAsync()
        {
            if (kind == "point")
            {
                return Ok(await service.GetAsync(new DocumentKey(database, "items", id), CancellationToken.None));
            }
            if (kind == "system")
            {
                return (await system.GetSystemAsync("api_keys", keyId, CancellationToken.None))!;
            }
            var query = new DocumentQuery("items", null,
                kind == "query" ? [new SortKey(new DocumentPath(["n"]), false)] : [], null, 50, 8 * 1024 * 1024);
            return Ok(await service.QueryAsync(database, query, kind == "query" ? 100_000 : null, CancellationToken.None)).Page.Rows[0].Document;
        }
    }

    [Test]
    public async Task Cached_query_rows_and_sort_keys_cannot_be_changed_by_service_callers()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var api = await DocumentApi.StartAsync(cancellationToken: deadline.Token);
        var first = DocumentApi.NewId();
        var second = DocumentApi.NewId();
        (await api.PutAsync(first, new { n = 1 }, cancellationToken: deadline.Token)).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(second, new { n = 2 }, cancellationToken: deadline.Token)).Status.ShouldBe(HttpStatusCode.Created);
        var service = api.Server.Services.GetRequiredService<IDocumentService>();
        var database = await api.TenantOfAsync(cancellationToken: deadline.Token);
        var query = new DocumentQuery("items", null, [new SortKey(new DocumentPath(["n"]), false)], null, 1, 8 * 1024 * 1024);
        var (page, count) = Ok(await service.QueryAsync(database, query, 100_000, deadline.Token));
        var rows = (IList<QueryRow>)page.Rows;
        var keys = (IList<object?>)page.Rows[0].Keys;
        var originalKeys = keys.ToArray();
        keys[0] = 999L;
        rows.Clear();
        // The first caller owns its result; the cache retains a detached page.
        var (cached, cachedCount) = Ok(await service.QueryAsync(database, query, 100_000, deadline.Token));
        await Assert.That(ReferenceEquals(page, cached)).IsFalse();
        await Assert.That(cached.Rows[0].Document.Id).IsEqualTo(first);
        await Assert.That(cached.Rows[0].Keys).IsEquivalentTo(originalKeys);
        await Assert.That(cachedCount).IsEqualTo(count);
        var cachedRows = (IList<QueryRow>)cached.Rows;
        var cachedKeys = (IList<object?>)cached.Rows[0].Keys;
        await Assert.That(cachedRows.Clear).Throws<NotSupportedException>();
        await Assert.That(() => cachedKeys[0] = 999L).Throws<NotSupportedException>();
        var request = new { sort = new[] { new { path = "/n" } }, limit = 1, count = true };
        var http = await api.QueryAsync(request, cancellationToken: deadline.Token);
        await Assert.That(http.Ids).IsEquivalentTo([first]);
        await Assert.That(http.Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("2");
        var next = await api.QueryAsync(new
        {
            request.sort,
            request.limit,
            cursor = http.Json.GetProperty("next_cursor").GetString(),
        }, cancellationToken: deadline.Token);
        await Assert.That(next.Ids).IsEquivalentTo([second]);
    }

    [Test]
    public async Task Identical_queries_keep_pages_and_counts_separate_between_tenants()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var management = await ManagementApi.StartAsync();
        using var first = await management.CreateTenantAsync("documents:read", "documents:write");
        using var second = await management.CreateTenantAsync("documents:read", "documents:write");
        var firstDatabase = await first.ContextAsync(management.Server, "db", deadline.Token);
        var secondDatabase = await second.ContextAsync(management.Server, "db", deadline.Token);
        var id = StorageFixture.Id("same");
        foreach (var tenant in new[] { first, second })
        {
            await StorageFixture.CreateCollectionAsync(tenant, "db", "items", deadline.Token);
            using var written = await tenant.Data.PutAsJsonAsync(
                tenant.Database("db", $"/collections/items/documents/by-key?id={id}"), new { n = 1, label = tenant.OrganizationId }, deadline.Token);
            written.EnsureSuccessStatusCode();
        }

        using var extra = await second.Data.PutAsJsonAsync(
            second.Database("db", $"/collections/items/documents/by-key?id={StorageFixture.Id("extra")}"), new { n = 2, label = "extra" }, deadline.Token);
        extra.EnsureSuccessStatusCode();
        var request = new { filter = new { exists = "/n" }, count = true };
        foreach (var tenant in new[] { first, second })
        {
            using var warmed = await tenant.Data.PostAsJsonAsync(tenant.Database("db", "/collections/items/query"), request, deadline.Token);
            warmed.EnsureSuccessStatusCode();
            var json = await warmed.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            await Assert.That(json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo(ReferenceEquals(tenant, first) ? "1" : "2");
        }

        var documents = management.Server.Services.GetRequiredService<IDocumentService>();
        var query = new DocumentQuery("items", new ExistsFilter(new DocumentPath(["n"])), [], null, 50, 8 * 1024 * 1024);
        var (firstPage, firstCount) = Ok(await documents.QueryAsync(firstDatabase, query, 100_000, deadline.Token));
        var (secondPage, secondCount) = Ok(await documents.QueryAsync(secondDatabase, query, 100_000, deadline.Token));
        await Assert.That(ReferenceEquals(firstPage, secondPage)).IsFalse();
        await Assert.That(firstCount).IsEqualTo(((long Count, bool Complete)?)(1L, true));
        await Assert.That(secondCount).IsEqualTo(((long Count, bool Complete)?)(2L, true));
        var (firstAgain, _) = Ok(await documents.QueryAsync(firstDatabase, query, 100_000, deadline.Token));
        var (secondAgain, _) = Ok(await documents.QueryAsync(secondDatabase, query, 100_000, deadline.Token));
        await Assert.That(ReferenceEquals(firstPage, firstAgain)).IsFalse();
        await Assert.That(ReferenceEquals(secondPage, secondAgain)).IsFalse();
    }

    [Test]
    public async Task Equivalent_queries_share_raw_pages_and_preserve_count_sort_and_cursor_shapes()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var api = await DocumentApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()), cancellationToken: deadline.Token);
        await using var cleanup = api;
        var firstId = DocumentApi.NewId();
        var secondId = DocumentApi.NewId();
        (await api.PutAsync(firstId, new { n = 1, label = "first" }, cancellationToken: deadline.Token)).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(secondId, new { n = 2, label = "second" }, cancellationToken: deadline.Token)).Status.ShouldBe(HttpStatusCode.Created);
        var request = new { filter = new { exists = "/n" }, sort = new[] { new { path = "/n" } }, limit = 1, count = true };
        var warmed = await api.QueryAsync(request, cancellationToken: deadline.Token);
        await Assert.That(warmed.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(warmed.Ids).IsEquivalentTo([firstId]);
        await Assert.That(warmed.Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("2");
        var documents = api.Server.Services.GetRequiredService<IDocumentService>();
        var database = await api.TenantOfAsync();
        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long hits = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Name == "terrabase.document_cache.lookups" && ReferenceEquals(instrument.Meter, meter))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var pageHit = 0;
            foreach (var tag in tags)
            {
                if ((tag.Key == "cache" && Equals(tag.Value, "page")) || (tag.Key == "result" && Equals(tag.Value, "hit")))
                {
                    pageHit++;
                }
            }
            if (pageHit == 2)
            {
                Interlocked.Add(ref hits, value);
            }
        });
        listener.Start();
        // Separately allocated filters, paths and lists normalize to the same key warmed by HTTPS.
        static DocumentQuery Query()
        {
            return new("items", new ExistsFilter(new DocumentPath(["n"])),
                [new SortKey(new DocumentPath(["n"]), false)], null, 1, 8 * 1024 * 1024);
        }
        var beforeInternal = await api.ReadUsageAsync();
        var (firstPage, _) = Ok(await documents.QueryAsync(database, Query(), 100_000, deadline.Token));
        var (equivalentPage, equivalentCount) = Ok(await documents.QueryAsync(database, Query(), 100_000, deadline.Token));
        await Assert.That(ReferenceEquals(firstPage, equivalentPage)).IsFalse();
        await Assert.That(hits).IsEqualTo(2);
        await Assert.That(equivalentPage.Rows[0].Document.Id).IsEqualTo(firstId);
        await Assert.That(equivalentPage.Rows[0].Keys).IsEquivalentTo(firstPage.Rows[0].Keys);
        await Assert.That(equivalentCount).IsEqualTo(((long Count, bool Complete)?)(2L, true));
        var (_, withoutCount) = Ok(await documents.QueryAsync(database, Query(), null, deadline.Token));
        await Assert.That(withoutCount).IsNull();
        var (_, boundedCount) = Ok(await documents.QueryAsync(database, Query(), 1, deadline.Token));
        await Assert.That(boundedCount).IsEqualTo(((long Count, bool Complete)?)(1L, false));
        await Assert.That(await api.ReadUsageAsync()).IsEqualTo(beforeInternal).Because("internal queries do not accept client read charges");
        var next = await api.QueryAsync(new
        {
            request.filter,
            request.sort,
            request.limit,
            cursor = warmed.Json.GetProperty("next_cursor").GetString(),
        }, cancellationToken: deadline.Token);
        await Assert.That(next.Status).IsEqualTo(HttpStatusCode.OK)
            .Because($"cursor continuation: {next.Code}: {next.Message}");
        await Assert.That(next.Ids).IsEquivalentTo([secondId]);
        var descending = await api.QueryAsync(new { request.filter, sort = new[] { new { path = "/n", order = "desc" } }, request.limit }, cancellationToken: deadline.Token);
        await Assert.That(descending.Status).IsEqualTo(HttpStatusCode.OK)
            .Because($"uncached descending query: {descending.Code}: {descending.Message}");
        await Assert.That(descending.Ids).IsEquivalentTo([secondId]);
        var projected = await api.QueryAsync(new { request.filter, request.sort, request.limit, request.count, project = Projection }, cancellationToken: deadline.Token);
        await Assert.That(projected.Status).IsEqualTo(HttpStatusCode.OK)
            .Because($"projected query: {projected.Code}: {projected.Message}");
        await Assert.That(projected.Json.GetProperty("items")[0].GetProperty("document").GetRawText()).IsEqualTo("{\"label\":\"first\"}");

        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => documents.QueryAsync(database, Query(), 100_000, cancelled.Token).AsTask());
    }

    [Test]
    public async Task Patch_atomic_writes_and_collection_recreation_invalidate_documents_pages_and_counts()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var api = await DocumentApi.StartAsync(cancellationToken: deadline.Token);
        await using var cleanup = api;
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { n = 1 }, cancellationToken: deadline.Token)).Status.ShouldBe(HttpStatusCode.Created);
        var original = await api.GetAsync(id, cancellationToken: deadline.Token);
        var request = new { filter = new { eq = new object[] { "/n", 1 } }, count = true };
        var before = await api.QueryAsync(request, cancellationToken: deadline.Token);
        await Assert.That(before.Ids).IsEquivalentTo([id]);
        var patch = await api.SendRawAsync(HttpMethod.Patch, api.Document(id), "{\"n\":2}", "application/merge-patch+json", cancellationToken: deadline.Token);
        await Assert.That(patch.Status).IsEqualTo(HttpStatusCode.OK);
        var afterPatch = await api.GetAsync(id, cancellationToken: deadline.Token);
        await Assert.That(afterPatch.Version).IsNotEqualTo(original.Version);
        await Assert.That(afterPatch.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(2);
        var noLongerMatches = await api.QueryAsync(request, cancellationToken: deadline.Token);
        await Assert.That(noLongerMatches.Ids.Count).IsEqualTo(0);
        await Assert.That(noLongerMatches.Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("0");
        var fresh = DocumentApi.NewId();
        var atomic = await api.TransactAsync(
            [
                new { op = "delete", collection = "items", id },
                new { op = "create", collection = "items", id = fresh, document = new { n = 1 } },
            ], cancellationToken: deadline.Token);
        await Assert.That(atomic.Status).IsEqualTo(HttpStatusCode.OK);
        var deleted = await api.GetAsync(id, cancellationToken: deadline.Token);
        await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.NotFound);
        var afterAtomic = await api.QueryAsync(request, cancellationToken: deadline.Token);
        await Assert.That(afterAtomic.Ids).IsEquivalentTo([fresh]);
        await Assert.That(afterAtomic.Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("1");
        (await api.GetAsync(fresh, cancellationToken: deadline.Token)).Status.ShouldBe(HttpStatusCode.OK);
        var collections = api.Collections;
        (await api.SendAsync(HttpMethod.Delete, collections + "/items", cancellationToken: deadline.Token)).Status.ShouldBe(HttpStatusCode.OK);
        var droppedDocument = await api.GetAsync(fresh, cancellationToken: deadline.Token);
        var droppedPage = await api.QueryAsync(request, cancellationToken: deadline.Token);
        await Assert.That((droppedDocument.Status, droppedDocument.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_collection"));
        await Assert.That((droppedPage.Status, droppedPage.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_collection"));
        (await api.SendAsync(HttpMethod.Post, collections, new { name = "items" }, cancellationToken: deadline.Token)).Status.ShouldBe(HttpStatusCode.Created);
        var recreated = await api.QueryAsync(request, cancellationToken: deadline.Token);
        await Assert.That(recreated.Ids.Count).IsEqualTo(0);
        await Assert.That(recreated.Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("0");
        (await api.GetAsync(fresh, cancellationToken: deadline.Token)).Status.ShouldBe(HttpStatusCode.NotFound);
    }
    private static QueryOutcome Ok(QueryResult result) => result switch
    {
        QueryOutcome outcome => outcome,
        _ => throw new InvalidOperationException("the query was refused"),
    };

    private static StoredDocument Ok(DocumentRead read) => read switch
    {
        StoredDocument document => document,
        _ => throw new InvalidOperationException("the document was not found"),
    };

    private sealed class TenantDocuments(IDocumentService inner) : IDocumentService
    {
        public ValueTask<CollectionSchemaPreviewResult> PreviewCollectionSchemaAsync(TenantContext database, string name, JsonElement schema, string? cursor, CancellationToken cancellationToken) =>
            inner.PreviewCollectionSchemaAsync(database, name, schema, cursor, cancellationToken);

        public int Reads { get; private set; }

        public int Writes { get; private set; }

        public ValueTask<CollectionResult> CreateCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
            inner.CreateCollectionAsync(database, name, cancellationToken);

        public ValueTask<CollectionResult> GetCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
            inner.GetCollectionAsync(database, name, cancellationToken);

        public ValueTask<Found<CollectionPage>> ListCollectionsAsync(
            TenantContext database, string? afterName, int limit, CancellationToken cancellationToken) =>
            inner.ListCollectionsAsync(database, afterName, limit, cancellationToken);

        public ValueTask<CollectionResult> DropCollectionAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
            inner.DropCollectionAsync(database, name, cancellationToken);

        public ValueTask<DocumentRead> GetAsync(DocumentKey key, CancellationToken cancellationToken)
        {
            Reads++;
            return inner.GetAsync(key, cancellationToken);
        }

        public void AcceptRead(DocumentKey key, StoredDocument document) => inner.AcceptRead(key, document);

        public void AcceptRead(TenantContext database, string collection, QueryPage page) => inner.AcceptRead(database, collection, page);

        public ValueTask<QueryResult> QueryAsync(
            TenantContext database, DocumentQuery query, long? countLimit, CancellationToken cancellationToken) =>
            inner.QueryAsync(database, query, countLimit, cancellationToken);

        public ValueTask<WriteOutcome> WriteAsync(
            TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, CancellationToken cancellationToken)
        {
            Writes++;
            return inner.WriteAsync(database, operations, indexErrors, cancellationToken);
        }

        public ValueTask<WriteOutcome> WriteConfirmedAsync(
            TenantContext database, IReadOnlyList<DocumentOperation> operations, bool indexErrors, bool waitForRemote, CancellationToken cancellationToken)
        {
            Writes++;
            return inner.WriteConfirmedAsync(database, operations, indexErrors, waitForRemote, cancellationToken);
        }

        public IAsyncEnumerable<ExportRow> ExportAsync(TenantContext database, string? afterCollection, string? afterId, CancellationToken cancellationToken) =>
            inner.ExportAsync(database, afterCollection, afterId, cancellationToken);

        public ValueTask<ApplyOutcome> ApplyAsync(IStorageTransaction transaction, DocumentOperation operation, CancellationToken cancellationToken) =>
            inner.ApplyAsync(transaction, operation, cancellationToken);

        public ValueTask<QueryResult> QueryAsync(
            IStorageTransaction transaction, DocumentQuery query, long? countLimit, CancellationToken cancellationToken, QueryObservation? observation = null) =>
            inner.QueryAsync(transaction, query, countLimit, cancellationToken, observation);
        public ValueTask<CollectionRulesResult> GetCollectionRulesAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
            inner.GetCollectionRulesAsync(database, name, cancellationToken);
        public ValueTask<CollectionRulesResult> SetCollectionRulesAsync(TenantContext database, string name, CollectionAccessRules rules, CancellationToken cancellationToken) =>
            inner.SetCollectionRulesAsync(database, name, rules, cancellationToken);

        public ValueTask<CollectionSchemaResult> GetCollectionSchemaAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
            inner.GetCollectionSchemaAsync(database, name, cancellationToken);

        public ValueTask<CollectionSchemaResult> SetCollectionSchemaAsync(TenantContext database, string name, JsonElement? schema, CancellationToken cancellationToken) =>
            inner.SetCollectionSchemaAsync(database, name, schema, cancellationToken);

        public ValueTask<CollectionTtlResult> GetCollectionTtlAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
            inner.GetCollectionTtlAsync(database, name, cancellationToken);

        public ValueTask<CollectionTtlResult> SetCollectionTtlAsync(TenantContext database, string name, string? path, CancellationToken cancellationToken) =>
            inner.SetCollectionTtlAsync(database, name, path, cancellationToken);
    }

}
