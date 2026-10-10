using Bogus;
using TerraBase.Engine.Sqlite;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class QueryPlanTests
{
    private static readonly Faker Faker = new();

    // The plans recorded in docs/sqlite-query-plans.md. Every statement seeks a primary key, except the
    // first page of collections and the collection counts and sizes, which walk the collections key in order, and
    // the index catalog reads, which scan the small schema table.
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["DocumentBytes"] = "SEARCH d USING PRIMARY KEY (collection=? AND id=?) | SEARCH c USING PRIMARY KEY (collection=? AND id=?) LEFT-JOIN",
        ["StoredDocumentBytes"] = "SCAN collections | CORRELATED SCALAR SUBQUERY 1 | SEARCH d USING PRIMARY KEY (collection=?) | SEARCH c USING PRIMARY KEY (collection=? AND id=?) LEFT-JOIN",
        ["Get"] = "SEARCH documents USING PRIMARY KEY (collection=? AND id=?)",
        ["UniqueRawDocument"] = "SEARCH d USING PRIMARY KEY (collection=? AND id=?) | SEARCH c USING PRIMARY KEY (collection=? AND id=?) LEFT-JOIN",
        ["Version"] = "SEARCH documents USING PRIMARY KEY (collection=? AND id=?)",
        // Inserts have no plan; the upsert's conflict check is a primary-key lookup.
        ["Put"] = "",
        ["Delete"] = "SEARCH documents USING PRIMARY KEY (collection=? AND id=?)",
        ["Tombstone"] = "SEARCH documents USING PRIMARY KEY (collection=? AND id=?)",
        ["ListFirst"] = "SEARCH documents USING INDEX documents_live (collection=?)",
        ["ListAfter"] = "SEARCH documents USING INDEX documents_live (collection=? AND id>?)",
        ["NextOffset"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        ["CurrentOffset"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        ["Incarnation"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        ["RenewIncarnation"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        ["CreateCollection"] = "",
        ["HasCollectionState"] = "SCAN CONSTANT ROW | SCALAR SUBQUERY 1 | SEARCH documents USING PRIMARY KEY (collection>? AND collection<?) | SCALAR SUBQUERY 2 | SEARCH collections USING PRIMARY KEY (name>? AND name<?) | SCALAR SUBQUERY 3 | SEARCH changes USING INDEX changes_deleted (collection>? AND collection<?) | SCALAR SUBQUERY 4 | SEARCH change_log USING COVERING INDEX change_log_by_offset (collection>? AND collection<?)",
        ["HasCollectionStateFrom"] = "SCAN CONSTANT ROW | SCALAR SUBQUERY 1 | SEARCH documents USING PRIMARY KEY (collection>?) | SCALAR SUBQUERY 2 | SEARCH collections USING PRIMARY KEY (name>?) | SCALAR SUBQUERY 3 | SEARCH changes USING INDEX changes_deleted (collection>?) | SCALAR SUBQUERY 4 | SEARCH change_log USING COVERING INDEX change_log_by_offset (collection>?)",
        ["GetCollection"] = "SEARCH collections USING PRIMARY KEY (name=?)",
        ["ListCollectionsFirst"] = "SCAN collections",
        ["ListCollectionsAfter"] = "SEARCH collections USING PRIMARY KEY (name>?)",
        ["DropDocuments"] = "SEARCH documents USING PRIMARY KEY (collection=?)",
        ["DropCollection"] = "SEARCH collections USING PRIMARY KEY (name=?)",
        // Shape history: an insert reads its collection row; the drop seeks the dropped generation.
        ["AppendChange"] = "SEARCH collections USING PRIMARY KEY (name=?)",
        ["HistoryFloor"] = "SEARCH shape_history USING INTEGER PRIMARY KEY (rowid=?)",
        ["DropHistory"] = "SEARCH change_log USING INDEX change_log_by_offset (collection=? AND generation=?) | SCALAR SUBQUERY 1 | SEARCH collections USING PRIMARY KEY (name=?)",
        ["CountDocuments"] = "SEARCH documents USING PRIMARY KEY (collection=?)",
        ["ListCollectionStats"] = "SCAN collections | SEARCH documents USING PRIMARY KEY (collection=?) LEFT-JOIN",
        ["DeleteCollectionDocuments"] = "SEARCH documents USING PRIMARY KEY (collection>? AND collection<?)",
        ["DeleteCollectionNames"] = "SEARCH collections USING PRIMARY KEY (name>? AND name<?)",
        ["ListIndexes"] = "SCAN sqlite_schema",
        ["IndexExists"] = "SCAN sqlite_schema",
        ["ObserveOffset"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        // Client databases (docs/design/anna-core.md): registers by key, a drop's live ids from the index alone, the journal
        // of the database's own stream and the applied positions of the others, baselines in key order.
        ["GetState"] = "SEARCH documents USING PRIMARY KEY (collection=? AND id=?)",
        ["ClientHasCollectionState"] = "SCAN CONSTANT ROW | SCALAR SUBQUERY 1 | SEARCH documents USING PRIMARY KEY (collection>? AND collection<?) | SCALAR SUBQUERY 2 | SEARCH collections USING PRIMARY KEY (name>? AND name<?) | SCALAR SUBQUERY 3 | SEARCH change_log USING COVERING INDEX change_log_by_offset (collection>? AND collection<?)",
        ["ClientHasCollectionStateFrom"] = "SCAN CONSTANT ROW | SCALAR SUBQUERY 1 | SEARCH documents USING PRIMARY KEY (collection>?) | SCALAR SUBQUERY 2 | SEARCH collections USING PRIMARY KEY (name>?) | SCALAR SUBQUERY 3 | SEARCH change_log USING COVERING INDEX change_log_by_offset (collection>?)",
        ["Materialized"] = "SEARCH d USING PRIMARY KEY (collection=? AND id=?) | SEARCH c USING PRIMARY KEY (collection=? AND id=?) LEFT-JOIN",
        ["PutState"] = "",
        ["LiveIds"] = "SEARCH documents USING INDEX documents_live (collection=?)",
        ["JournalState"] = "SEARCH replication_state USING INTEGER PRIMARY KEY (rowid=?)",
        ["ClaimJournal"] = "SEARCH replication_state USING INTEGER PRIMARY KEY (rowid=?)",
        ["LastGroup"] = "SEARCH journal",
        ["AppendGroup"] = "",
        ["AppendGroupChange"] = "",
        ["GroupDependencies"] = "SEARCH journal USING INTEGER PRIMARY KEY (rowid=?)",
        ["GroupChanges"] = "SEARCH changes USING PRIMARY KEY (sequence=?)",
        ["GetApplied"] = "SEARCH applied USING PRIMARY KEY (origin=?)",
        ["AppliedPositions"] = "SCAN applied",
        ["SetApplied"] = "",
        ["DropSupersededApplied"] = "SCAN applied",
        ["BaselineFirst"] = "SCAN documents",
        ["BaselineAfter"] = "SEARCH documents USING PRIMARY KEY ((collection,id)>(?,?))",
        ["ReplicationStateFirst"] = "SCAN documents USING INDEX documents_replication_keys",
        ["ReplicationStateAfter"] = "SEARCH documents USING INDEX documents_replication_keys ((collection,id)>(?,?))",
        ["Origin"] = "SEARCH commit_offset USING INTEGER PRIMARY KEY (rowid=?)",
        ["MarkComplete"] = "SEARCH replication_state USING INTEGER PRIMARY KEY (rowid=?)",
        ["LastSequence"] = "SEARCH changes USING PRIMARY KEY",
        ["AppendReplicationChange"] = "",
        ["GetChangeDigest"] = "SEARCH journal_prefixes USING PRIMARY KEY (sequence=?)",
        ["AppendChangeDigest"] = "",
        // System database replication: snapshot pages, group pages, prefix drops, replica writes, journal tail.
        ["SystemDocumentsFirst"] = "SCAN documents USING INDEX documents_live",
        ["SystemDocumentsAfter"] = "SEARCH documents USING INDEX documents_live ((collection,id)>(?,?))",
        ["GroupSize"] = "SEARCH changes USING PRIMARY KEY (sequence=?)",
        ["ReadGroupPage"] = "SEARCH changes USING PRIMARY KEY (sequence=? AND ordinal>?)",
        ["CollectionRangeIds"] = "SEARCH documents USING PRIMARY KEY (collection>? AND collection<?)",
        ["ReplaceDocument"] = "",
        ["SystemLastSequence"] = "SEARCH journal_prefixes USING PRIMARY KEY",
        ["TruncateChanges"] = "SEARCH changes USING PRIMARY KEY (sequence<?)",
        ["TruncatePrefixes"] = "SEARCH journal_prefixes USING PRIMARY KEY (sequence<?)",
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
                var plan = await QueryPlan.ExplainAsync(path, sql);
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
        using var command = new Microsoft.Data.Sqlite.SqliteCommand();
        var sql = new SqliteQueryCompiler(command.Parameters).Compile(new TerraBase.Engine.DocumentQuery(
            "items", null, [], null, 1, 1024));
        var plan = await QueryPlan.ExplainAsync(databaseFile, sql);
        await Assert.That(plan.Contains("USING INDEX documents_live", StringComparison.Ordinal)).IsTrue();
        await Assert.That(plan.Contains("SCAN documents", StringComparison.Ordinal)).IsFalse();
        foreach (var descending in new[] { false, true })
        {
            foreach (var duplicate in new[] { false, true })
            {
                var idSort = new TerraBase.Engine.SortKey(TerraBase.Engine.DocumentPath.Id, descending);
                TerraBase.Engine.SortKey[] keys = duplicate ? [idSort, idSort] : [idSort];
                foreach (var cursor in new[] { false, true })
                {
                    using var idCommand = new Microsoft.Data.Sqlite.SqliteCommand();
                    var position = cursor ? new TerraBase.Engine.QueryPosition(duplicate ? [5L, live, 5L, live] : [5L, live], live) : null;
                    var idSql = new SqliteQueryCompiler(idCommand.Parameters).Compile(new TerraBase.Engine.DocumentQuery(
                        "items", null, keys, position, 1, 1024));
                    var idPlan = await QueryPlan.ExplainAsync(databaseFile, idSql);
                    var range = cursor ? (descending ? " AND id<?" : " AND id>?") : "";
                    await Assert.That(idPlan).IsEqualTo($"SEARCH documents USING INDEX documents_live (collection=?{range})");
                    await Assert.That(idPlan.Contains("TEMP B-TREE", StringComparison.Ordinal)).IsFalse();
                }
            }
        }

        var tombstone = await api.GetAsync(ids[0]);
        await Assert.That(tombstone.Status).IsEqualTo(System.Net.HttpStatusCode.NotFound);
        await Assert.That(tombstone.ETag is not null).IsTrue();
    }

    [Test]
    public async Task Shape_snapshot_and_change_reads_seek_the_history_indexes_without_sorting()
    {
        await using var api = await DocumentApi.StartAsync();
        (await api.PutAsync(DocumentApi.NewId(), new { status = "open" })).Status.ShouldBe(System.Net.HttpStatusCode.Created);
        var path = await api.DatabaseFileAsync();
        var filter = new TerraBase.Engine.CompareFilter(
            new TerraBase.Engine.DocumentPath(["status"]), TerraBase.Engine.Comparison.Equal,
            new TerraBase.Engine.ScalarValue(TerraBase.Engine.ScalarKind.Text, "open"));
        using (var command = new Microsoft.Data.Sqlite.SqliteCommand())
        {
            var sql = new SqliteQueryCompiler(command.Parameters).CompileSnapshot(filter, 1, 10);
            var plan = await QueryPlan.ExplainAsync(path, sql);
            Console.WriteLine($"snapshot: {plan}");
            // One walk of the births key in id order, no sort; version <= offset is tested on each key entry. An id
            // that existed at the offset seeks its last write there and reads that write by primary key.
            // The walk is bounded above by the highest id at the offset, one seek evaluated once per page.
            await Assert.That(plan).StartsWith("SEARCH b USING PRIMARY KEY (collection=? AND generation=? AND id>? AND id<?) | SCALAR SUBQUERY");
            await Assert.That(plan).Contains("SEARCH high USING PRIMARY KEY (collection=? AND generation=? AND version<?)");
            await Assert.That(plan.Contains("CORRELATED SCALAR SUBQUERY 3", StringComparison.Ordinal)).IsFalse();
            await Assert.That(plan).Contains("SEARCH last USING COVERING INDEX change_log_by_document (collection=? AND generation=? AND id=? AND version<?)");
            await Assert.That(plan).Contains("SEARCH change_log USING INTEGER PRIMARY KEY (rowid=?)");
            await Assert.That(plan.Contains("SCAN", StringComparison.Ordinal) || plan.Contains("TEMP B-TREE", StringComparison.Ordinal)).IsFalse();
        }

        using var changes = new Microsoft.Data.Sqlite.SqliteCommand();
        var changeSql = new SqliteQueryCompiler(changes.Parameters).CompileChanges(filter, 1, 4097);
        var changePlan = await QueryPlan.ExplainAsync(path, changeSql);
        Console.WriteLine($"changes: {changePlan}");
        await Assert.That(changePlan).StartsWith("SEARCH c USING INDEX change_log_by_offset (collection=? AND generation=? AND version>? AND version<?)");
        // Each document's last write of a commit is one seek to its (id, version) range end.
        await Assert.That(changePlan).Contains("SEARCH last USING COVERING INDEX change_log_by_document (collection=? AND generation=? AND id=? AND version=?)");
        await Assert.That(changePlan.Contains("SCAN", StringComparison.Ordinal) || changePlan.Contains("TEMP B-TREE", StringComparison.Ordinal)).IsFalse();
    }
}
