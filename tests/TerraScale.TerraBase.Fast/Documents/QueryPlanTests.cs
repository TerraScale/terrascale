using Bogus;
using TerraScale.TerraBase.Engine.Sqlite;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Documents;

public sealed class QueryPlanTests
{
    private static readonly Faker Faker = new();

    // The plans recorded in docs/sqlite-query-plans.md. Every statement seeks a primary key, except the
    // first page of collections, which walks the collections key in order up to its limit; none sorts in a
    // temporary B-tree.
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["Get"] = "SEARCH documents USING PRIMARY KEY (collection=? AND id=?)",
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
        ["GetCollection"] = "SEARCH collections USING PRIMARY KEY (name=?)",
        ["ListCollectionsFirst"] = "SCAN collections",
        ["ListCollectionsAfter"] = "SEARCH collections USING PRIMARY KEY (name>?)",
        ["DropDocuments"] = "SEARCH documents USING PRIMARY KEY (collection=?)",
        ["DropCollection"] = "SEARCH collections USING PRIMARY KEY (name=?)",
    };

    [Test]
    public async Task Every_document_statement_seeks_the_primary_key()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!, await server.IssueAsync("documents:write"));
        (await client.PutAsync(new Uri("/v1/databases/plans", UriKind.Relative), null)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(new Uri("/v1/databases/plans/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync(
            new Uri($"/v1/databases/plans/collections/items/documents/{Guid.CreateVersion7()}", UriKind.Relative),
            new { title = Faker.Commerce.ProductName() })).EnsureSuccessStatusCode();
        // Every statement the engine runs must have a recorded plan.
        await Assert.That(SqliteStatements.All.Keys).IsEquivalentTo(Expected.Keys);
        // _system (created by issuing the key) holds API keys and Management documents with the same
        // statements. Plans depend on the schema, not on the rows, so an almost empty file is enough.
        foreach (var database in new[] { "plans", "_system" })
        {
            var path = Path.Combine(server.DataDirectory, database + ".db");
            foreach (var (name, sql) in SqliteStatements.All)
            {
                var plan = await QueryPlan.ExplainAsync(path, sql);
                await Assert.That($"{database} {name}: {plan}").IsEqualTo($"{database} {name}: {Expected[name]}");
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
        var live = "ffffffff-ffff-7fff-8fff-ffffffffffff";
        (await api.PutAsync(live, new { n = 2 })).Status.ShouldBe(System.Net.HttpStatusCode.Created);
        var listed = await api.SendAsync(HttpMethod.Get, DocumentApi.Documents + "?limit=1");
        var queried = await api.QueryAsync(new { limit = 1 });
        await Assert.That(listed.Ids).IsEquivalentTo([live]);
        await Assert.That(queried.Ids).IsEquivalentTo([live]);

        using var command = new Microsoft.Data.Sqlite.SqliteCommand();
        var sql = new SqliteQueryCompiler(command.Parameters).Compile(new TerraScale.TerraBase.Engine.DocumentQuery(
            "items", null, [], null, 1, 1024));
        var plan = await QueryPlan.ExplainAsync(Path.Combine(api.Server.DataDirectory, "db.db"), sql);
        await Assert.That(plan.Contains("USING INDEX documents_live", StringComparison.Ordinal)).IsTrue();
        await Assert.That(plan.Contains("SCAN documents", StringComparison.Ordinal)).IsFalse();
        foreach (var descending in new[] { false, true })
        {
            foreach (var duplicate in new[] { false, true })
            {
                var idSort = new TerraScale.TerraBase.Engine.SortKey(TerraScale.TerraBase.Engine.DocumentPath.Id, descending);
                TerraScale.TerraBase.Engine.SortKey[] keys = duplicate ? [idSort, idSort] : [idSort];
                foreach (var cursor in new[] { false, true })
                {
                    using var idCommand = new Microsoft.Data.Sqlite.SqliteCommand();
                    var position = cursor ? new TerraScale.TerraBase.Engine.QueryPosition(duplicate ? [5L, live, 5L, live] : [5L, live], live) : null;
                    var idSql = new SqliteQueryCompiler(idCommand.Parameters).Compile(new TerraScale.TerraBase.Engine.DocumentQuery(
                        "items", null, keys, position, 1, 1024));
                    var idPlan = await QueryPlan.ExplainAsync(Path.Combine(api.Server.DataDirectory, "db.db"), idSql);
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
}
