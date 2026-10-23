using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Documents;
using TerraBase.Fast.Storage;

namespace TerraBase.Fast.Sql;

public sealed class SqlEngineReadTests
{
    private static readonly string[] PeopleColumns = ["id", "name", "team"];
    private static readonly string[] TeamColumns = ["code", "title"];

    private static async Task<DocumentApi> SeedAsync()
    {
        var api = await DocumentApi.StartAsync();
        foreach (var name in new[] { "people", "teams" })
        {
            (await api.SendAsync(HttpMethod.Post, api.Collections, new { name })).Status.ShouldBe(HttpStatusCode.Created);
        }

        (await api.TransactAsync(
            new { op = "put", collection = "people", id = StorageFixture.Id("1"), document = new { id = 1, name = "ann", team = "a" } },
            new { op = "put", collection = "people", id = StorageFixture.Id("2"), document = new { id = 2, name = "bob", team = "b" } },
            new { op = "put", collection = "teams", id = StorageFixture.Id("a"), document = new { code = "a", title = "Alpha" } })).Status.ShouldBe(HttpStatusCode.OK);
        return api;
    }

    private static async Task<SqlProgramResult> RunAsync(DocumentApi api, string statement, IReadOnlyList<object?> parameters, int maxRows = 100, string? ctes = null)
    {
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        await using var transaction = (await engine.BeginTransactionAsync(await api.TenantOfAsync(), write: false, CancellationToken.None))!;
        return await transaction.ExecuteSqlAsync(
            new SqlProgram([new SqlProgramTable("people", PeopleColumns), new SqlProgramTable("teams", TeamColumns)], ctes, statement, parameters, maxRows, 1024 * 1024),
            CancellationToken.None);
    }

    [Test]
    public async Task Tables_read_as_ctes_of_their_fields_with_parameters_and_the_statements_own_ctes()
    {
        await using var api = await SeedAsync();

        var joined = await RunAsync(api, "SELECT p.name, t.title, $p1 FROM people p LEFT JOIN teams t ON t.code = p.team WHERE p.id >= $p2 ORDER BY p.id", ["x", 1L]);
        var own = await RunAsync(api, "SELECT n FROM named", [], ctes: "named(n) AS (SELECT name FROM people WHERE id = 2)");
        var truncated = await RunAsync(api, "SELECT id FROM people", [], maxRows: 1);

        await Assert.That(joined.Columns).IsEquivalentTo(["name", "title", "$p1"]);
        await Assert.That(joined.Rows.Select(row => string.Join('|', row))).IsEquivalentTo(["ann|Alpha|x", "bob||x"]);
        await Assert.That(joined.Truncated).IsFalse();
        await Assert.That(own.Rows.Single()[0]).IsEqualTo("bob");
        await Assert.That((truncated.Rows.Count, truncated.Truncated)).IsEqualTo((1, true));
        await Assert.That(truncated.Rows[0][0]).IsEqualTo(1L);
    }

    [Test]
    public async Task Engine_errors_name_the_rule_without_the_statement_text()
    {
        await using var api = await SeedAsync();

        var error = await Assert.ThrowsAsync<SqlProgramException>(async () => await RunAsync(api, "SELECT secret_column FROM people", []));

        await Assert.That(error!.Message).IsEqualTo("no such column");
    }
}
