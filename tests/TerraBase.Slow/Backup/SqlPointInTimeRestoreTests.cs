using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Revisions;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class SqlPointInTimeRestoreTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Sql_tables_restore_to_a_point_in_time_with_their_rows_schema_constraints_and_triggers(CancellationToken cancellationToken)
    {
        await using var host = await SqlBackupHost.StartAsync(cancellationToken);
        var (first, second, third) = (Faker.Internet.Email(), Faker.Internet.Email(), Faker.Internet.Email());
        await host.OkAsync(
            "CREATE TABLE teams (id TEXT PRIMARY KEY, name TEXT NOT NULL)",
            "CREATE TABLE players (id TEXT PRIMARY KEY, team TEXT NOT NULL REFERENCES teams (id) ON DELETE CASCADE, email TEXT UNIQUE, goals INTEGER NOT NULL CHECK (goals >= 0), profile TEXT)",
            "CREATE INDEX players_team ON players (team)",
            "CREATE VIEW scorers AS SELECT id, goals FROM players WHERE goals > 0",
            "CREATE TABLE audit (id INTEGER PRIMARY KEY, player TEXT NOT NULL)",
            "CREATE TRIGGER players_audit AFTER INSERT ON players BEGIN INSERT INTO audit (player) VALUES (new.id); END");
        await host.OkAsync(
            Stmt("INSERT INTO teams VALUES ('red', ?), ('blue', ?)", Faker.Company.CompanyName(), Faker.Company.CompanyName()),
            Stmt("INSERT INTO players VALUES ('p1', 'red', ?, 0, ?)", first, """{"city":"Recife"}"""),
            Stmt("INSERT INTO players VALUES ('p2', 'blue', ?, 2, ?)", second, """{"city":"Porto"}"""));
        await host.SnapshotAsync(cancellationToken);
        var k = host.SnapshotAt.AddSeconds(2);

        await host.OkAsync(
            Stmt("UPDATE teams SET name = ? WHERE id = 'red'", Faker.Company.CompanyName()),
            Stmt("UPDATE players SET goals = 5, profile = json_set(profile, '$.city', 'Natal') WHERE id = 'p1'"),
            Stmt("INSERT INTO players VALUES ('p3', 'red', ?, 1, NULL)", third));
        await host.StampAsync(k.AddSeconds(-1), cancellationToken);
        await host.OkAsync(Stmt("DELETE FROM teams WHERE id = 'blue'"), Stmt("INSERT INTO players VALUES ('p4', 'red', NULL, 7, NULL)"));
        await host.StampAsync(k, cancellationToken);
        await host.ShipAsync(TimeSpan.FromMilliseconds(2500), cancellationToken);
        var latest = await host.LatestAsync(cancellationToken);

        var atK = await host.RestoreAsync(k, "at-k", cancellationToken);
        var atLatest = await host.RestoreAsync(latest, "at-latest", cancellationToken);

        await Assert.That(await host.RowsAsync(atK, "SELECT id, team, goals, profile ->> '$.city' FROM players ORDER BY id"))
            .IsEquivalentTo(["p1|red|5|Natal", "p2|blue|2|Porto", "p3|red|1|"]);
        await Assert.That(await host.RowsAsync(atK, "SELECT player FROM audit ORDER BY id")).IsEquivalentTo(["p1", "p2", "p3"]);
        await Assert.That(await host.RowsAsync(atLatest, "SELECT id, team, goals FROM players ORDER BY id")).IsEquivalentTo(["p1|red|5", "p3|red|1", "p4|red|7"]);
        await Assert.That(await host.RowsAsync(atLatest, "SELECT id FROM teams")).IsEquivalentTo(["red"]);
        await Assert.That(await host.RowsAsync(atLatest, "SELECT player FROM audit ORDER BY id")).IsEquivalentTo(["p1", "p2", "p3", "p4"]);
        await Assert.That(await host.RowsAsync(atLatest, "SELECT id FROM scorers ORDER BY id")).IsEquivalentTo(["p1", "p3", "p4"]);
        await Assert.That(await host.RowsAsync(atK, "SELECT type, name FROM sqlite_schema WHERE name IN ('players_team', 'scorers', 'players_audit') ORDER BY name"))
            .IsEquivalentTo(["trigger|players_audit", "index|players_team", "view|scorers"]);

        await Assert.That(await host.RefusedAsync(atK, Stmt("INSERT INTO players VALUES ('p9', 'green', NULL, 0, NULL)"))).IsEqualTo("foreign_key_violation");
        await Assert.That(await host.RefusedAsync(atK, Stmt("INSERT INTO players VALUES ('p9', 'red', ?, 0, NULL)", first))).IsEqualTo("unique_violation");
        await Assert.That(await host.RefusedAsync(atK, Stmt("UPDATE players SET goals = -1 WHERE id = 'p1'"))).IsEqualTo("check_violation");
        await host.OkInAsync(atK, Stmt("INSERT INTO players VALUES ('p5', 'blue', NULL, 0, NULL)"), Stmt("DELETE FROM teams WHERE id = 'red'"));
        await Assert.That(await host.RowsAsync(atK, "SELECT id FROM players ORDER BY id")).IsEquivalentTo(["p2", "p5"]);
        await Assert.That(await host.RowsAsync(atK, "SELECT player FROM audit ORDER BY id")).IsEquivalentTo(["p1", "p2", "p3", "p5"]);
        await Assert.That(await host.RowsAsync(host.Database.Name, "SELECT id FROM players ORDER BY id")).IsEquivalentTo(["p1", "p3", "p4"]);
    }

    private static object Stmt(string sql, params object?[] args) => new { sql, args };
}
