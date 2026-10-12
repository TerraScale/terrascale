using Bogus;
using TerraBase.Engine.Sqlite;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class QueryPlanTests
{
    private static readonly Faker Faker = new();

    // The plans Turso reports (docs/sqlite-query-plans.md). Every statement seeks a key: a rowid, or the unique index of a
    // primary key (rowid tables only), except the first page of collections and the collection counts and sizes, which
    // walk the collections key in order, the index catalog reads, which scan the small schema table, and the journal
    // tails, which Turso labels SCAN although ORDER BY ... DESC LIMIT 1 reads only the last index entry.
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["DocumentBytes"] = "SEARCH d USING INDEX sqlite_autoindex_documents_1 (collection=? AND id=?) | SEARCH c USING INDEX sqlite_autoindex__tb_unique_conflicts_1 (collection=? AND id=?) LEFT-JOIN",
        ["StoredDocumentBytes"] = "SCAN collections USING COVERING INDEX sqlite_autoindex_collections_1 | CORRELATED SCALAR SUBQUERY 1 | SEARCH d USING INDEX sqlite_autoindex_documents_1 (collection=?) | SEARCH c USING INDEX sqlite_autoindex__tb_unique_conflicts_1 (collection=? AND id=?) LEFT-JOIN",
        ["Get"] = "SEARCH documents USING INDEX sqlite_autoindex_documents_1 (collection=? AND id=?)",
        ["Version"] = "SEARCH documents USING INDEX sqlite_autoindex_documents_1 (collection=? AND id=?)",
        // Inserts have no plan; the upsert's conflict check is a primary-key lookup.
        ["Put"] = "",
        ["Delete"] = "SEARCH documents USING COVERING INDEX sqlite_autoindex_documents_1 (collection=? AND id=?)",
        ["Tombstone"] = "SEARCH documents USING COVERING INDEX sqlite_autoindex_documents_1 (collection=? AND id=?)",
        ["ListFirst"] = "SEARCH documents USING INDEX documents_live (collection=?)",
        ["ListAfter"] = "SEARCH documents USING INDEX documents_live (collection=? AND id>?)",
        ["NextOffset"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        ["CurrentOffset"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        ["Incarnation"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        ["RenewIncarnation"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        ["CreateCollection"] = "",
        ["HasCollectionState"] = "SEARCH documents USING COVERING INDEX sqlite_autoindex_documents_1 (collection>=? AND collection<?) | SEARCH collections USING COVERING INDEX sqlite_autoindex_collections_1 (name>=? AND name<?) | SEARCH changes USING COVERING INDEX changes_deleted (collection>=? AND collection<?) | SEARCH change_log USING COVERING INDEX change_log_by_offset (collection>=? AND collection<?) | SCAN CONSTANT ROW",
        ["HasCollectionStateFrom"] = "SEARCH documents USING COVERING INDEX sqlite_autoindex_documents_1 (collection>=?) | SEARCH collections USING COVERING INDEX sqlite_autoindex_collections_1 (name>=?) | SEARCH changes USING COVERING INDEX changes_deleted (collection>=?) | SEARCH change_log USING COVERING INDEX change_log_by_offset (collection>=?) | SCAN CONSTANT ROW",
        ["GetCollection"] = "SEARCH collections USING INDEX sqlite_autoindex_collections_1 (name=?)",
        ["ListCollectionsFirst"] = "SCAN collections USING INDEX sqlite_autoindex_collections_1",
        ["ListCollectionsAfter"] = "SEARCH collections USING INDEX sqlite_autoindex_collections_1 (name>?)",
        ["DropDocuments"] = "SEARCH documents USING COVERING INDEX sqlite_autoindex_documents_1 (collection=?)",
        ["DropStates"] = "SEARCH _tb_state USING COVERING INDEX sqlite_autoindex__tb_state_1 (collection=?)",
        ["DropCollection"] = "SEARCH collections USING COVERING INDEX sqlite_autoindex_collections_1 (name=?)",
        // Shape history: an insert reads its collection row; the drop seeks the dropped generation.
        ["AppendChange"] = "SEARCH collections USING INDEX sqlite_autoindex_collections_1 (name=?)",
        ["HistoryFloor"] = "SEARCH shape_history USING INTEGER PRIMARY KEY (rowid=?)",
        ["DropHistory"] = "SCALAR SUBQUERY 1 | SEARCH collections USING INDEX sqlite_autoindex_collections_1 (name=?) | SEARCH change_log USING INDEX change_log_by_offset (collection=? AND generation=?)",
        ["CountDocuments"] = "SEARCH documents USING COVERING INDEX documents_live (collection=?)",
        ["ListCollectionStats"] = "SCAN collections USING COVERING INDEX sqlite_autoindex_collections_1 | SEARCH documents USING INDEX sqlite_autoindex_documents_1 (collection=?) LEFT-JOIN",
        ["DeleteCollectionDocuments"] = "SEARCH documents USING COVERING INDEX sqlite_autoindex_documents_1 (collection>=? AND collection<?)",
        ["DeleteCollectionNames"] = "SEARCH collections USING COVERING INDEX sqlite_autoindex_collections_1 (name>=? AND name<?)",
        ["ListIndexes"] = "SCAN sqlite_schema",
        ["IndexExists"] = "SCAN sqlite_schema",
        ["ObserveOffset"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        // Client databases (docs/terrabase/multi-region-replication.md): states and documents by key.
        ["GetState"] = "SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?) | SEARCH d USING INDEX sqlite_autoindex_items_1 (id=?) LEFT-JOIN | SEARCH c USING INDEX sqlite_autoindex__tb_unique_conflicts_1 (collection=? AND id=?) LEFT-JOIN",
        ["ClientHasCollectionState"] = "SEARCH _tb_state USING COVERING INDEX sqlite_autoindex__tb_state_1 (collection>=? AND collection<?) | SEARCH collections USING COVERING INDEX sqlite_autoindex_collections_1 (name>=? AND name<?) | SEARCH change_log USING COVERING INDEX change_log_by_offset (collection>=? AND collection<?) | SCAN CONSTANT ROW",
        ["ClientHasCollectionStateFrom"] = "SEARCH _tb_state USING COVERING INDEX sqlite_autoindex__tb_state_1 (collection>=?) | SEARCH collections USING COVERING INDEX sqlite_autoindex_collections_1 (name>=?) | SEARCH change_log USING COVERING INDEX change_log_by_offset (collection>=?) | SCAN CONSTANT ROW",
        ["UpdateState"] = "SEARCH _tb_state USING COVERING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?)",
        ["InsertState"] = "",
        ["UpdateApplied"] = "SEARCH _tb_peers USING INTEGER PRIMARY KEY (rowid=?)",
        ["InsertApplied"] = "",
        ["UpdateCollection"] = "SEARCH collections USING COVERING INDEX sqlite_autoindex_collections_1 (name=?)",
        ["Origin"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        ["LastSequence"] = "SCALAR SUBQUERY 1 | SCAN changes USING COVERING INDEX sqlite_autoindex_changes_1 | SCAN CONSTANT ROW",
        ["AppendReplicationChange"] = "",
        ["GetChangeDigest"] = "SEARCH journal_prefixes USING INTEGER PRIMARY KEY (rowid=?)",
        ["AppendChangeDigest"] = "",
        // System database replication: snapshot pages, group pages, prefix drops, replica writes, journal tail.
        ["SystemDocumentsFirst"] = "SCAN documents USING INDEX documents_live",
        ["SystemDocumentsAfter"] = "COMPOUND QUERY | LEFT-MOST SUBQUERY | SCAN (subquery-0) | SEARCH documents USING INDEX sqlite_autoindex_documents_1 (collection=? AND id>?) | UNION ALL | SCAN (subquery-0) | SEARCH documents USING INDEX documents_live (collection>?) | USE SORTER FOR ORDER BY",
        ["GroupSize"] = "SEARCH changes USING COVERING INDEX sqlite_autoindex_changes_1 (sequence=?)",
        ["ReadGroupPage"] = "SEARCH changes USING INDEX sqlite_autoindex_changes_1 (sequence=? AND ordinal>=?)",
        ["CollectionRangeIds"] = "SEARCH documents USING COVERING INDEX sqlite_autoindex_documents_1 (collection>=? AND collection<?)",
        ["ReplaceDocument"] = "",
        ["SystemLastSequence"] = "SCALAR SUBQUERY 1 | SCAN journal_prefixes | SCAN CONSTANT ROW",
        ["TruncateChanges"] = "SEARCH changes USING COVERING INDEX sqlite_autoindex_changes_1 (sequence<=?)",
        ["ClientDocumentBytes"] = "SCALAR SUBQUERY 1 | SEARCH _tb_unique_conflicts USING INDEX sqlite_autoindex__tb_unique_conflicts_1 (collection=? AND id=?) | SCALAR SUBQUERY 2 | SEARCH items USING INDEX sqlite_autoindex_items_1 (id=?) | SCAN CONSTANT ROW",
        ["ClientListFirst"] = "SCAN items AS d USING INDEX sqlite_autoindex_items_1 | CORRELATED SCALAR SUBQUERY 1 | SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?)",
        ["ClientListAfter"] = "SEARCH d USING INDEX sqlite_autoindex_items_1 (id>?) | CORRELATED SCALAR SUBQUERY 1 | SEARCH s USING INDEX sqlite_autoindex__tb_state_1 (collection=? AND id=?)",
        ["ClientCollectionStats"] = "SCAN items",
        ["ClientConflictBytes"] = "SCAN _tb_unique_conflicts",
        ["ClientCollectionNames"] = "SCAN collections USING COVERING INDEX sqlite_autoindex_collections_1",
        ["UpdateRow"] = "SEARCH items USING COVERING INDEX sqlite_autoindex_items_1 (id=?)",
        ["InsertRow"] = "",
        ["DeleteRow"] = "SEARCH items USING COVERING INDEX sqlite_autoindex_items_1 (id=?)",
        ["TruncatePrefixes"] = "SEARCH journal_prefixes USING INTEGER PRIMARY KEY (rowid=?)",
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
        // Every statement the engine runs must have a recorded plan.
        await Assert.That(SqliteStatements.Shared.Keys.Concat(SqliteStatements.ClientOnly.Keys).Concat(SqliteStatements.SystemOnly.Keys))
            .IsEquivalentTo(Expected.Keys);
        // _system (created by issuing the key) holds API keys and Management documents with the shared statements and its
        // own. Plans depend on the schema, not on the rows, so an almost empty file is enough.
        foreach (var (database, path, statements) in new[]
        {
            ("plans", Path.Combine(api.Server.DataDirectory, tenant.OrganizationId, tenant.ProjectId, plans + ".db"), SqliteStatements.ClientOnly),
            ("_system", Path.Combine(api.Server.DataDirectory, "_system.db"), SqliteStatements.SystemOnly),
        })
        {
            foreach (var (name, sql) in SqliteStatements.Shared.Concat(statements))
            {
                // A client statement over one collection's table names it as {0}.
                var plan = await QueryPlan.ExplainAsync(
                    path, statements == SqliteStatements.ClientOnly ? string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, "\"items\"") : sql);
                await Assert.That($"{database} {name}: {plan}").IsEqualTo($"{database} {name}: {Expected[name]}").Because(plan);
            }
        }
    }

    [Test]
    public async Task Listing_seeks_live_rows_past_a_tombstone_prefix()
    {
        await using var api = await DocumentApi.StartAsync();
        var ids = Enumerable.Range(0, 128).Select(i => $"00000000-0000-7000-8000-{i:x12}").ToList();
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
        var sql = new SqliteQueryCompiler(command.Parameters, RowSource.Client("items")).Compile(new TerraBase.Engine.DocumentQuery(
            "items", null, [], null, 1, 1024));
        var plan = await QueryPlan.ExplainAsync(databaseFile, sql);
        // The collection's table holds live documents only: its id key leads straight to the live one.
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
                    var idSql = new SqliteQueryCompiler(idCommand.Parameters, RowSource.Client("items")).Compile(new TerraBase.Engine.DocumentQuery(
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
