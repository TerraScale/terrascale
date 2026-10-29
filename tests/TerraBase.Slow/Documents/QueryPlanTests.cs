using Bogus;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Documents;

public sealed class QueryPlanTests
{
    private static readonly Faker Faker = new();
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["DocumentBytes"] = "SEARCH d USING INDEX sqlite_autoindex_documents_1 (collection=? AND id=?) | SEARCH c USING INDEX sqlite_autoindex__tb_unique_conflicts_1 (collection=? AND id=?) LEFT-JOIN",
        ["StoredDocumentBytes"] = "SCAN collections USING COVERING INDEX sqlite_autoindex_collections_1 | CORRELATED SCALAR SUBQUERY 1 | SEARCH d USING INDEX sqlite_autoindex_documents_1 (collection=?) | SEARCH c USING INDEX sqlite_autoindex__tb_unique_conflicts_1 (collection=? AND id=?) LEFT-JOIN",
        ["Get"] = "SCALAR SUBQUERY 1 | SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?) | SEARCH documents USING INDEX sqlite_autoindex_documents_1 (collection=? AND id=?)",
        ["Version"] = "SCALAR SUBQUERY 1 | SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?) | SEARCH documents USING COVERING INDEX sqlite_autoindex_documents_1 (collection=? AND id=?) | SCAN CONSTANT ROW",
        ["ListFirst"] = "SEARCH d USING INDEX sqlite_autoindex_documents_1 (collection=?) | CORRELATED SCALAR SUBQUERY 1 | SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?)",
        ["ListAfter"] = "SEARCH d USING INDEX sqlite_autoindex_documents_1 (collection=? AND id>?) | CORRELATED SCALAR SUBQUERY 1 | SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?)",
        ["SetOffset"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        ["CurrentOffset"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        ["Incarnation"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        ["RenewIncarnation"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        ["CreateCollection"] = "",
        ["HasCollectionState"] = "SEARCH _tb_state USING COVERING INDEX sqlite_autoindex__tb_state_1 (collection>=? AND collection<?) | SEARCH collections USING COVERING INDEX sqlite_autoindex_collections_1 (name>=? AND name<?) | SCAN CONSTANT ROW",
        ["HasCollectionStateFrom"] = "SEARCH _tb_state USING COVERING INDEX sqlite_autoindex__tb_state_1 (collection>=?) | SEARCH collections USING COVERING INDEX sqlite_autoindex_collections_1 (name>=?) | SCAN CONSTANT ROW",
        ["GetCollection"] = "SEARCH collections USING INDEX sqlite_autoindex_collections_1 (name=?)",
        ["ListCollectionsFirst"] = "SCAN collections USING INDEX sqlite_autoindex_collections_1",
        ["ListCollectionsAfter"] = "SEARCH collections USING INDEX sqlite_autoindex_collections_1 (name>?)",
        ["DropDocuments"] = "SEARCH documents USING COVERING INDEX sqlite_autoindex_documents_1 (collection=?)",
        ["DropStates"] = "SEARCH _tb_state USING COVERING INDEX sqlite_autoindex__tb_state_1 (collection=?)",
        ["DropCollection"] = "SEARCH collections USING COVERING INDEX sqlite_autoindex_collections_1 (name=?)",
        ["CountDocuments"] = "SEARCH documents USING COVERING INDEX sqlite_autoindex_documents_1 (collection=?)",
        ["ListCollectionStats"] = "SCAN collections USING COVERING INDEX sqlite_autoindex_collections_1 | SEARCH documents USING INDEX sqlite_autoindex_documents_1 (collection=?) LEFT-JOIN",
        ["DeleteCollectionDocuments"] = "SEARCH documents USING COVERING INDEX sqlite_autoindex_documents_1 (collection>=? AND collection<?)",
        ["DeleteCollectionNames"] = "SEARCH collections USING COVERING INDEX sqlite_autoindex_collections_1 (name>=? AND name<?)",
        ["DeleteCollectionStates"] = "SEARCH _tb_state USING COVERING INDEX sqlite_autoindex__tb_state_1 (collection>=? AND collection<?)",
        ["SystemGetStateBytes"] = "SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?) | SEARCH d USING INDEX sqlite_autoindex_documents_1 (collection=? AND id=?) LEFT-JOIN",
        ["SystemGetState"] = "SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?) | SEARCH d USING INDEX sqlite_autoindex_documents_1 (collection=? AND id=?) LEFT-JOIN",
        ["SystemUpdateRow"] = "SEARCH documents USING COVERING INDEX sqlite_autoindex_documents_1 (collection=? AND id=?)",
        ["SystemInsertRow"] = "",
        ["SystemDeleteRow"] = "SEARCH documents USING COVERING INDEX sqlite_autoindex_documents_1 (collection=? AND id=?)",
        ["ListIndexes"] = "SCAN sqlite_schema",
        ["IndexExists"] = "SCAN sqlite_schema",
        ["GetStateBytes"] = "SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?) | SEARCH d USING INDEX sqlite_autoindex_items_1 (id=?) LEFT-JOIN | SEARCH c USING INDEX sqlite_autoindex__tb_unique_conflicts_1 (collection=? AND id=?) LEFT-JOIN",
        ["GetState"] = "SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?) | SEARCH d USING INDEX sqlite_autoindex_items_1 (id=?) LEFT-JOIN | SEARCH c USING INDEX sqlite_autoindex__tb_unique_conflicts_1 (collection=? AND id=?) LEFT-JOIN",
        ["UpdateState"] = "SEARCH _tb_state USING COVERING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?)",
        ["InsertState"] = "",
        ["UpdateCollection"] = "SEARCH collections USING COVERING INDEX sqlite_autoindex_collections_1 (name=?)",
        ["ClientDocumentBytes"] = "SCALAR SUBQUERY 1 | SEARCH _tb_unique_conflicts USING INDEX sqlite_autoindex__tb_unique_conflicts_1 (collection=? AND id=?) | SCALAR SUBQUERY 2 | SEARCH items USING INDEX sqlite_autoindex_items_1 (id=?) | SCAN CONSTANT ROW",
        ["ClientListFirst"] = "SCAN items AS d USING INDEX sqlite_autoindex_items_1 | CORRELATED SCALAR SUBQUERY 1 | SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?)",
        ["ClientListAfter"] = "SEARCH d USING INDEX sqlite_autoindex_items_1 (id>?) | CORRELATED SCALAR SUBQUERY 1 | SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?)",
        ["ClientCollectionStats"] = "SCAN collections AS c USING COVERING INDEX sqlite_autoindex_collections_1 | SEARCH s USING INDEX sqlite_autoindex__tb_collection_stats_1 (collection=?) LEFT-JOIN",
        ["ClientStoredBytes"] = "SCAN _tb_collection_stats",
        ["AddCollectionStats"] = "",
        ["DropCollectionStats"] = "SEARCH _tb_collection_stats USING COVERING INDEX sqlite_autoindex__tb_collection_stats_1 (collection=?)",
        ["ClientConflictBytes"] = "SCAN _tb_unique_conflicts",
        ["ClientCollectionNames"] = "SCAN collections USING COVERING INDEX sqlite_autoindex_collections_1",
        ["UpdateRow"] = "SEARCH items USING COVERING INDEX sqlite_autoindex_items_1 (id=?)",
        ["InsertRow"] = "",
        ["DeleteRow"] = "SEARCH items USING COVERING INDEX sqlite_autoindex_items_1 (id=?)",
        ["RowBytes"] = "SEARCH items USING INDEX sqlite_autoindex_items_1 (id=?)",
    };

    [Test]
    public async Task Every_document_statement_seeks_the_primary_key()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:write");
        using var created = await tenant.Data.PutAsync(tenant.Database("plans"), null);
        created.EnsureSuccessStatusCode();
        var plans = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetString()!;
        (await tenant.Data.PostAsJsonAsync(tenant.Database("plans", "/collections"), new { name = "items" })).EnsureSuccessStatusCode();
        (await tenant.Data.PutAsJsonAsync(
            tenant.Database("plans", $"/collections/items/documents/by-key?id={EntityIds.New()}"),
            new { title = Faker.Commerce.ProductName() })).EnsureSuccessStatusCode();
        await Assert.That(TursoStatements.Shared.Keys.Concat(TursoStatements.ClientOnly.Keys).Concat(TursoStatements.SystemOnly.Keys))
            .IsEquivalentTo(Expected.Keys);
        foreach (var (database, path, statements) in new[]
        {
            ("plans", Path.Combine(api.Server.DataDirectory, tenant.OrganizationId, tenant.ProjectId, plans + ".db"), TursoStatements.ClientOnly),
            ("_system", Path.Combine(api.Server.DataDirectory, "_system.db"), TursoStatements.SystemOnly),
        })
        {
            foreach (var (name, sql) in TursoStatements.Shared.Concat(statements))
            {
                var plan = await QueryPlan.ExplainAsync(
                    path, statements == TursoStatements.ClientOnly ? string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, "\"items\"") : sql);
                await Assert.That($"{database} {name}: {plan}").IsEqualTo($"{database} {name}: {Expected[name]}").Because(plan);
            }
        }
    }

    [Test]
    public async Task Search_candidates_seek_text_ids_for_queries_counts_and_sort_admission()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { body = "native" })).Status.ShouldBe(System.Net.HttpStatusCode.Created);
        var databaseFile = await api.DatabaseFileAsync();
        using var command = new Turso.Data.Sqlite.SqliteCommand();
        var source = RowSource.Client("items");
        var compiler = new TursoQueryCompiler(command.Parameters, source, candidates: $"[\"{id}\"]");
        var query = new DocumentQuery("items", null, [], null, 1, 1024);
        (string Sql, int Seeks)[] statements =
        [
            (compiler.Compile(query), 1),
            (compiler.Compile(query with { Sort = [new SortKey(DocumentPath.Id, false)], After = new QueryPosition([5L, id], id) }), 1),
            (compiler.Compile(query with { Sort = [new SortKey(new DocumentPath(["body"]), false)] }), 1),
            (compiler.CompileCount(null, 1), 2),
            ($"SELECT octet_length(doc) FROM {TursoQueryCompiler.CandidateFrom(source, "$hits")} WHERE 1 ORDER BY id LIMIT 10001;", 1),
        ];
        foreach (var (sql, expectedSeeks) in statements)
        {
            var plan = await QueryPlan.ExplainAsync(databaseFile, sql);
            await Assert.That(plan.Contains("SCAN items", StringComparison.Ordinal)).IsFalse().Because(plan);
            var seeks = plan.Split(" | ", StringSplitOptions.None).Count(step =>
                step.StartsWith("SEARCH items USING ", StringComparison.Ordinal)
                && step.EndsWith("sqlite_autoindex_items_1 (id=?)", StringComparison.Ordinal));
            await Assert.That(seeks).IsEqualTo(expectedSeeks).Because(plan);
        }
    }

    [Test]
    public async Task Listing_seeks_live_rows_past_a_tombstone_prefix()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var ids = Enumerable.Range(0, 128).Select(i => $"{i:D26}").ToList();
        (await api.TransactAsync([.. ids.Select(id => new { op = "create", collection = "items", id, document = new { n = 1 } })]))
            .Status.ShouldBe(System.Net.HttpStatusCode.OK);
        (await api.TransactAsync([.. ids.Select(id => new { op = "delete", collection = "items", id })]))
            .Status.ShouldBe(System.Net.HttpStatusCode.OK);
        var live = "7ZZZZZZZZZFZZRZZZZZZZZZZZZ";
        (await api.PutAsync(live, new { n = 2 })).Status.ShouldBe(System.Net.HttpStatusCode.Created);
        var listed = await api.SendAsync(HttpMethod.Get, api.Documents + "?limit=1");
        var queried = await api.QueryAsync(new { limit = 1 });
        await Assert.That(listed.Ids).IsEquivalentTo([live]);
        await Assert.That(queried.Ids).IsEquivalentTo([live]);

        var databaseFile = await api.DatabaseFileAsync();
        using var command = new Turso.Data.Sqlite.SqliteCommand();
        var sql = new TursoQueryCompiler(command.Parameters, RowSource.Client("items")).Compile(new TerraBase.Engine.DocumentQuery(
            "items", null, [], null, 1, 1024));
        var plan = await QueryPlan.ExplainAsync(databaseFile, sql);
        await Assert.That(plan).IsEqualTo("SCAN items USING INDEX sqlite_autoindex_items_1 | CORRELATED SCALAR SUBQUERY 1 | SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?)");
        foreach (var descending in new[] { false, true })
        {
            foreach (var duplicate in new[] { false, true })
            {
                var idSort = new TerraBase.Engine.SortKey(TerraBase.Engine.DocumentPath.Id, descending);
                TerraBase.Engine.SortKey[] keys = duplicate ? [idSort, idSort] : [idSort];
                foreach (var cursor in new[] { false, true })
                {
                    using var idCommand = new Turso.Data.Sqlite.SqliteCommand();
                    var position = cursor ? new TerraBase.Engine.QueryPosition(duplicate ? [5L, live, 5L, live] : [5L, live], live) : null;
                    var idSql = new TursoQueryCompiler(idCommand.Parameters, RowSource.Client("items")).Compile(new TerraBase.Engine.DocumentQuery(
                        "items", null, keys, position, 1, 1024));
                    var idPlan = await QueryPlan.ExplainAsync(databaseFile, idSql);
                    var range = cursor ? (descending ? " AND id<?" : " AND id>?") : "";
                    var walk = cursor ? $"SEARCH items USING INDEX sqlite_autoindex_items_1 (id{range[7..]})" : "SCAN items USING INDEX sqlite_autoindex_items_1";
                    await Assert.That(idPlan).IsEqualTo($"{walk} | CORRELATED SCALAR SUBQUERY 1 | SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?)");
                    await Assert.That(idPlan.Contains("USE SORTER", StringComparison.Ordinal)).IsFalse();
                }
            }
        }

        var tombstone = await api.GetAsync(ids[0]);
        await Assert.That(tombstone.Status).IsEqualTo(System.Net.HttpStatusCode.NotFound);
        await Assert.That(tombstone.ETag is not null).IsTrue();
    }
}
