using Bogus;
using TerraScale.TerraBase.Engine.Sqlite;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Documents;

public sealed class QueryPlanTests
{
    private static readonly Faker Faker = new();

    // The plans recorded in docs/sqlite-query-plans.md. Every statement seeks the primary key;
    // none scans the table or sorts in a temporary B-tree.
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["Get"] = "SEARCH documents USING PRIMARY KEY (collection=? AND id=?)",
        // Inserts have no plan; the upsert's conflict check is a primary-key lookup.
        ["Put"] = "",
        ["Delete"] = "SEARCH documents USING PRIMARY KEY (collection=? AND id=?)",
        ["ListFirst"] = "SEARCH documents USING PRIMARY KEY (collection=?)",
        ["ListAfter"] = "SEARCH documents USING PRIMARY KEY (collection=? AND id>?)",
    };

    [Test]
    public async Task Every_document_statement_seeks_the_primary_key()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!, await server.IssueAsync("documents:write"));
        (await client.PutAsync(new Uri("/v1/databases/plans", UriKind.Relative), null)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync(
            new Uri($"/v1/databases/plans/collections/items/documents/{Faker.Random.AlphaNumeric(8)}", UriKind.Relative),
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
}
