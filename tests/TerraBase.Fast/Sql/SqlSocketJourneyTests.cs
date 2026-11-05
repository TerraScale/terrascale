using Bogus;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Sql;

public sealed class SqlSocketJourneyTests
{
    [Test]
    public async Task A_project_token_creates_reads_updates_and_deletes_rows_over_websocket()
    {
        await using var database = await StartSharedAsync();
        await using var socket = await database.BrowserSocketAsync();
        var admitted = await socket.AuthAsync(database.Api.ProjectToken(database.Database.Project, "sql:read sql:write"));
        var name = new Faker().Name.FirstName();
        var answer = await socket.ExecuteAsync(
            Stmt("CREATE TABLE notes (id INTEGER PRIMARY KEY, name TEXT NOT NULL)"),
            Stmt("INSERT INTO notes VALUES (?1, ?2)", 1L, name),
            Stmt("SELECT name FROM notes WHERE id = ?1", 1L),
            Stmt("UPDATE notes SET name = ?1 WHERE id = ?2", "updated", 1L),
            Stmt("DELETE FROM notes WHERE id = ?1", 1L),
            Stmt("SELECT count(*) FROM notes"));

        await Assert.That(admitted.GetProperty("type").GetString()).IsEqualTo("result");
        await Assert.That(answer.GetProperty("type").GetString()).IsEqualTo("result");
        var results = answer.GetProperty("results");
        await Assert.That(results[2].GetProperty("rows")[0][0].GetString()).IsEqualTo(name);
        await Assert.That(results[3].GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(results[4].GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(results[5].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(0);
    }
}
