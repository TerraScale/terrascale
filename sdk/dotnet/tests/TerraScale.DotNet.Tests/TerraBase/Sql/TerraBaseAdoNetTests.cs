using System.Net;
using Dapper;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase.Sql;

public sealed partial class TerraBaseAdoNetTests
{
    private static readonly string Route = $"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/sql/execute";

    internal sealed record Person(long Id, string Name);

    [Test]
    public async Task Dapper_queries_and_executes_through_the_ado_net_provider()
    {
        var bodies = new List<string>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(Route);
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return TerraBaseIntegrationFixture.Json("""
                {"results":[{"columns":[{"name":"id","decltype":"INTEGER"},{"name":"name","decltype":"TEXT"}],
                "rows":[[1,"Ada"],[2,"Bob"]],"rows_affected":1,"last_insert_rowid":null}]}
                """);
        });
        using var http = new HttpClient(handler);
        using var connection = new TerraBaseDbConnection(TerraBaseSqlRestTests.Database(http));
        await connection.OpenAsync();

        var people = await connection.QueryAsync<Person>("SELECT id, name FROM people WHERE id > @min", new { min = 0L });
        var affected = await connection.ExecuteAsync("INSERT INTO people (name) VALUES (@name)", new { name = "Cara" });

        await Assert.That(bodies[0]).IsEqualTo("""{"statements":[{"sql":"SELECT id, name FROM people WHERE id > @min","named":{"min":0}}]}""");
        await Assert.That(bodies[1]).IsEqualTo("""{"statements":[{"sql":"INSERT INTO people (name) VALUES (@name)","named":{"name":"Cara"}}]}""");
        await Assert.That(people).IsEquivalentTo([new Person(1, "Ada"), new Person(2, "Bob")]);
        await Assert.That(affected).IsEqualTo(1);
        await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Open);
    }
}
