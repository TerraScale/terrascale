using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>The Documents client against a real TerraBase process; nothing on the HTTP path is mocked.</summary>
[ClassDataSource<TerraBaseServer>(Shared = SharedType.PerTestSession)]
public sealed class TerraBaseDocumentsTests(TerraBaseServer server)
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task<TerraBaseDatabase> DatabaseAsync(string collection = "items")
    {
        server.Require();
        var tenant = await server.CreateTenantAsync();
        var database = server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "app");
        var created = await database.CreateAsync();
        await Assert.That(created.Name).IsEqualTo("app");
        await Assert.That(created.ProjectId).IsEqualTo(tenant.Project.Id);
        await database.CreateCollectionAsync(collection);
        return database;
    }

    [Test]
    public async Task CreatingADatabaseIsIdempotent()
    {
        server.Require();
        var tenant = await server.CreateTenantAsync();
        var database = server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "app");

        var first = await database.CreateAsync();
        var second = await database.CreateAsync();

        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    public async Task CollectionsAreCreatedListedReadAndDropped()
    {
        var database = await DatabaseAsync("alpha");
        await database.CreateCollectionAsync("beta");
        await database.CreateCollectionAsync("gamma");

        var conflict = await Assert.ThrowsAsync<TerraBaseConflictException>(() => database.CreateCollectionAsync("beta"));
        await Assert.That(conflict!.ErrorCode).IsEqualTo("name_taken");

        var first = await database.ListCollectionsAsync(limit: 2);
        await Assert.That(first.Items.Select(collection => collection.Name).SequenceEqual(["alpha", "beta"])).IsTrue();
        await Assert.That(first.NextCursor).IsNotNull();
        var second = await database.ListCollectionsAsync(limit: 2, cursor: first.NextCursor);
        await Assert.That(second.Items.Select(collection => collection.Name)).IsEquivalentTo(["gamma"]);
        await Assert.That(second.NextCursor).IsNull();

        var beta = await database.GetCollectionAsync("beta");
        await Assert.That(beta.CreatedAt).IsGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));

        await database.CreateDocumentAsync("beta", Json("""{"a":1}"""));
        var dropped = await database.DropCollectionAsync("beta");
        await Assert.That(dropped.Name).IsEqualTo("beta");
        await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => database.GetCollectionAsync("beta"));
    }

    [Test]
    public async Task DocumentsAreCreatedReadReplacedPatchedAndDeleted()
    {
        var database = await DatabaseAsync();

        var created = await database.CreateDocumentAsync("items", Json("""{"name":"a","count":1}"""));
        await Assert.That(created.Created).IsTrue();
        await Assert.That(Guid.TryParse(created.Id, out _)).IsTrue();
        await Assert.That(created.Version).StartsWith("tbv1-");
        await Assert.That(created.Durability).IsEqualTo("local_durable");

        var read = await database.GetDocumentAsync("items", created.Id);
        await Assert.That(read!.Version).IsEqualTo(created.Version);
        await Assert.That(read.Document.GetProperty("name").GetString()).IsEqualTo("a");

        var replaced = await database.PutDocumentAsync("items", created.Id, Json("""{"name":"b","count":1}"""));
        await Assert.That(replaced.Created).IsFalse();
        await Assert.That(replaced.Offset).IsGreaterThan(created.Offset);

        var merged = await database.PatchDocumentAsync("items", created.Id, Json("""{"name":null,"tag":"x"}"""));
        var operated = await database.PatchDocumentAsync("items", created.Id, [
            TerraBasePatchOperation.Increment("/count", Json("2")),
            TerraBasePatchOperation.Set("/nested/value", Json("true")),
            TerraBasePatchOperation.Append("/list", Json("1")),
            TerraBasePatchOperation.Unset("/tag"),
        ], TerraBasePrecondition.Matches(merged.Version));
        var patched = await database.GetDocumentAsync("items", created.Id);
        await Assert.That(patched!.Version).IsEqualTo(operated.Version);
        await Assert.That(JsonElement.DeepEquals(patched.Document, Json("""{ "list": [1], "nested": { "value": true }, "count": 3 }"""))).IsTrue();

        var typeConflict = await Assert.ThrowsAsync<TerraBaseConflictException>(
            () => database.PatchDocumentAsync("items", created.Id, [TerraBasePatchOperation.Append("/count", Json("1"))]));
        await Assert.That(typeConflict!.ErrorCode).IsEqualTo("patch_conflict");

        var deleted = await database.DeleteDocumentAsync("items", created.Id);
        await Assert.That(deleted.Version).IsNotEqualTo(operated.Version);
        await Assert.That(await database.GetDocumentAsync("items", created.Id)).IsNull();
        var absent = await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => database.DeleteDocumentAsync("items", created.Id));
        await Assert.That(absent!.ErrorCode).IsEqualTo("not_found");
    }

    [Test]
    public async Task VersionsAndCreateOnlyPreconditionsGuardWrites()
    {
        var database = await DatabaseAsync();
        var id = Guid.CreateVersion7().ToString("D");

        var first = await database.PutDocumentAsync("items", id, Json("""{"v":1}"""), TerraBasePrecondition.Absent);
        await Assert.That(first.Created).IsTrue();
        var exists = await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(
            () => database.PutDocumentAsync("items", id, Json("""{"v":2}"""), TerraBasePrecondition.Absent));
        await Assert.That(exists!.ErrorCode).IsEqualTo("precondition_failed");

        var second = await database.PutDocumentAsync("items", id, Json("""{"v":2}"""), TerraBasePrecondition.Matches(first.Version));
        await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(
            () => database.PutDocumentAsync("items", id, Json("""{"v":3}"""), TerraBasePrecondition.Matches(first.Version)));
        await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(
            () => database.PatchDocumentAsync("items", id, Json("""{"v":3}"""), TerraBasePrecondition.Matches(first.Version)));
        await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(
            () => database.DeleteDocumentAsync("items", id, TerraBasePrecondition.Matches(first.Version)));
        await database.PutDocumentAsync("items", id, Json("""{"v":3}"""), TerraBasePrecondition.Exists);

        await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(
            () => database.PutDocumentAsync("items", Guid.CreateVersion7().ToString("D"), Json("{}"), TerraBasePrecondition.Exists));

        await database.DeleteDocumentAsync("items", id);
        var recreated = await database.PutDocumentAsync("items", id, Json("""{"v":4}"""), TerraBasePrecondition.Absent);
        await Assert.That(recreated.Created).IsTrue();
        await Assert.That(recreated.Version).IsNotEqualTo(second.Version);
    }

    [Test]
    public async Task ListsPageInIdOrder()
    {
        var database = await DatabaseAsync();
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await database.CreateDocumentAsync("items", Json($$"""{"i":{{i}}}"""))).Id);
        }

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await database.ListDocumentsAsync("items", limit: 2, cursor: cursor);
            await Assert.That(page.Items.Count).IsLessThanOrEqualTo(2);
            seen.AddRange(page.Items.Select(document => document.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        await Assert.That(seen.SequenceEqual(ids.Order(StringComparer.Ordinal))).IsTrue();
    }

    [Test]
    public async Task PagesOfEscapedDocumentsFitTheDefaultResponseBound()
    {
        // TerraBase escapes '<' as \u003C, so 4 MB stored becomes a 24 MB response: more than the old 16 MiB cap.
        var database = await DatabaseAsync();
        var document = Json($$"""{"text":"{{new string('<', 1_000_000)}}"}""");
        for (var i = 0; i < 4; i++)
        {
            await database.CreateDocumentAsync("items", document);
        }

        var page = await database.ListDocumentsAsync("items", limit: 4);

        await Assert.That(page.Items.Count).IsEqualTo(4);
        await Assert.That(page.Items[0].Document.GetProperty("text").GetString()!.Length).IsEqualTo(1_000_000);
    }

    [Test]
    public async Task QueriesFilterSortProjectAndCount()
    {
        var database = await DatabaseAsync();
        foreach (var (name, age) in new[] { ("ada", 36), ("bob", 20), ("cy", 50), ("dee", 41) })
        {
            await database.CreateDocumentAsync("items", Json($$"""{"name":"{{name}}","age":{{age}},"secret":"x"}"""));
        }

        var query = new TerraBaseQuery
        {
            Filter = Json("""{"gte":["/age",30]}"""),
            Sort = [new TerraBaseSort("/age", "desc")],
            Project = ["/name"],
            Limit = 2,
            Count = true,
        };
        var first = await database.QueryAsync("items", query);
        await Assert.That(first.Count).IsEqualTo(new TerraBaseCount(3, true));
        await Assert.That(first.Items.Select(document => document.Document).SequenceEqual([Json("""{"name":"cy"}"""), Json("""{"name":"dee"}""")], EqualityComparer<JsonElement>.Create(JsonElement.DeepEquals))).IsTrue();

        var second = await database.QueryAsync("items", query with { Cursor = first.NextCursor, Count = null });
        await Assert.That(second.Items.Single().Document.GetProperty("name").GetString()).IsEqualTo("ada");
        await Assert.That(second.NextCursor).IsNull();

        var invalid = await Assert.ThrowsAsync<TerraBaseValidationException>(
            () => database.QueryAsync("items", new TerraBaseQuery { Filter = Json("""{"nope":1}""") }));
        await Assert.That(invalid!.ErrorCode).IsEqualTo("invalid_query");
    }

    [Test]
    public async Task TransactionsApplyEveryOperationKindAtomically()
    {
        var database = await DatabaseAsync();
        await database.CreateCollectionAsync("other");
        var kept = await database.CreateDocumentAsync("items", Json("""{"kind":"kept","n":1}"""));
        var removed = await database.CreateDocumentAsync("items", Json("""{"kind":"removed"}"""));
        var patched = await database.CreateDocumentAsync("items", Json("""{"n":1}"""));
        var putId = Guid.CreateVersion7().ToString("D");
        var absentId = Guid.CreateVersion7().ToString("D");

        var result = await database.TransactAsync([
            TerraBaseTransactionOperation.Create("other", Json("""{"made":true}""")),
            TerraBaseTransactionOperation.Put("items", putId, Json("""{"put":true}""")),
            TerraBaseTransactionOperation.Patch("items", patched.Id, Json("""{"m":2}"""), patched.Version),
            TerraBaseTransactionOperation.Delete("items", removed.Id, removed.Version),
            TerraBaseTransactionOperation.Assert("items", kept.Id, Json("""{"eq":["/kind","kept"]}""")),
            TerraBaseTransactionOperation.AssertAbsent("items", absentId),
            TerraBaseTransactionOperation.AssertCount("items", Json("""{"exists":"/kind"}"""), gte: 2, lte: 2),
        ]);

        await Assert.That(result.Results.Count).IsEqualTo(7);
        await Assert.That(result.Results[1].Id).IsEqualTo(putId);
        await Assert.That(result.Results[4].Asserted).IsTrue();
        await Assert.That(result.Results[6].Count).IsEqualTo(2);
        await Assert.That(await database.GetDocumentAsync("items", removed.Id)).IsNull();
        var current = (await database.GetDocumentAsync("items", patched.Id))!;
        await Assert.That(JsonElement.DeepEquals(current.Document, Json("""{ "m": 2, "n": 1 }"""))).IsTrue();

        var failed = await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(() => database.TransactAsync([
            TerraBaseTransactionOperation.Put("items", kept.Id, Json("""{"lost":true}""")),
            TerraBaseTransactionOperation.Patch("items", patched.Id, [TerraBasePatchOperation.Set("/n", Json("9"))], patched.Version),
        ]));
        await Assert.That(failed!.OperationIndex).IsEqualTo(1);
        await Assert.That((await database.GetDocumentAsync("items", kept.Id))!.Version).IsEqualTo(kept.Version);

        var createId = Guid.CreateVersion7().ToString("D");
        var committed = await database.TransactAsync([
            TerraBaseTransactionOperation.Patch("items", patched.Id, [TerraBasePatchOperation.Set("/n", Json("9"))], current.Version),
            TerraBaseTransactionOperation.Create("other", Json("""{"made":"explicit"}"""), createId),
        ]);

        await Assert.That(committed.Results.Select(operation => operation.Id).SequenceEqual([patched.Id, createId])).IsTrue();
        var updated = (await database.GetDocumentAsync("items", patched.Id))!;
        await Assert.That(updated.Version).IsEqualTo(committed.Results[0].Version);
        await Assert.That(JsonElement.DeepEquals(updated.Document, Json("""{ "m": 2, "n": 9 }"""))).IsTrue();
        await Assert.That(JsonElement.DeepEquals((await database.GetDocumentAsync("other", createId))!.Document, Json("""{"made":"explicit"}"""))).IsTrue();
    }

    [Test]
    public async Task ErrorsMapToTypedExceptions()
    {
        server.Require();
        var tenant = await server.CreateTenantAsync();
        var documents = server.Documents(tenant.ApiKey);

        var unknown = await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => documents.Database(tenant.Project.Id, "missing").ListCollectionsAsync());
        await Assert.That(unknown!.ErrorCode).IsEqualTo("unknown_database");

        var database = documents.Database(tenant.Project.Id, "app");
        await database.CreateAsync();
        await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => database.CreateDocumentAsync("missing", Json("{}")));
        var notObject = await Assert.ThrowsAsync<TerraBaseValidationException>(() => database.CreateCollectionAsync("Bad Name"));
        await Assert.That(notObject!.ErrorCode).IsEqualTo("invalid_request");

        var anonymous = await Assert.ThrowsAsync<TerraBaseAuthenticationException>(
            () => server.Documents("tb_unknown").Database(tenant.Project.Id, "app").ListCollectionsAsync());
        await Assert.That(anonymous!.ErrorCode).IsEqualTo("invalid_key");

        var readOnly = server.Documents(await server.CreateApiKeyAsync(tenant.Organization.Id, "documents:read")).Database(tenant.Project.Id, "app");
        await Assert.ThrowsAsync<TerraBaseForbiddenException>(() => readOnly.CreateCollectionAsync("items"));
    }

    [Test]
    public async Task CancellationStopsTheRequest()
    {
        var database = await DatabaseAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => database.ListDocumentsAsync("items", cancellationToken: cancelled.Token));
    }
}
