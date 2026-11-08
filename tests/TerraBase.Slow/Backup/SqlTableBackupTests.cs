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

namespace TerraBase.Fast.Backup;

public sealed class SqlTableBackupTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task A_database_rewinds_in_place_to_a_time_keeping_its_identity_and_backing_up_the_rewind(CancellationToken cancellationToken)
    {
        await using var host = await SqlBackupHost.StartAsync(cancellationToken);
        await host.OkAsync(
            "CREATE TABLE teams (id TEXT PRIMARY KEY, name TEXT NOT NULL)",
            "CREATE TABLE players (id TEXT PRIMARY KEY, team TEXT NOT NULL REFERENCES teams (id) ON DELETE CASCADE)",
            "CREATE TABLE audit (id INTEGER PRIMARY KEY, player TEXT NOT NULL)",
            "CREATE TRIGGER players_audit AFTER INSERT ON players BEGIN INSERT INTO audit (player) VALUES (new.id); END");
        await host.OkAsync(Stmt("INSERT INTO teams VALUES ('red', ?)", Faker.Company.CompanyName()), Stmt("INSERT INTO players VALUES ('p1', 'red')"));
        await host.SnapshotAsync(cancellationToken);
        var k = host.SnapshotAt.AddSeconds(2);
        await host.OkAsync(Stmt("INSERT INTO players VALUES ('p2', 'red')"));
        await host.StampAsync(k.AddSeconds(-1), cancellationToken);
        await host.OkAsync("DROP TABLE players", "CREATE TABLE later (id TEXT PRIMARY KEY)", "INSERT INTO later VALUES ('x')");
        await host.StampAsync(k, cancellationToken);
        await host.ShipAsync(TimeSpan.FromMilliseconds(2500), cancellationToken);

        using var rewound = await host.Management.PostAsJsonAsync(ManagementApi.Uri($"{host.Database.Path}/point-in-time/rewind"), new { pointInTime = k }, cancellationToken);
        var body = await rewound.Content.ReadAsStringAsync(cancellationToken);
        await Assert.That(rewound.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(body);
        await host.OkAsync(Stmt("INSERT INTO players VALUES ('p3', 'red')"));
        using var databases = JsonDocument.Parse(await host.Management.GetStringAsync(ManagementApi.Uri($"/v1/projects/{host.Database.Project}/databases"), cancellationToken));

        await Assert.That(JsonElement.Parse(body).GetProperty("id").GetString()).IsEqualTo(host.Database.Id);
        await Assert.That(await host.RowsAsync(host.Database.Name, "SELECT id FROM players ORDER BY id")).IsEquivalentTo(["p1", "p2", "p3"]);
        await Assert.That(await host.RowsAsync(host.Database.Name, "SELECT player FROM audit ORDER BY id")).IsEquivalentTo(["p1", "p2", "p3"]);
        await Assert.That(await host.RowsAsync(host.Database.Name, "SELECT name FROM sqlite_schema WHERE name = 'later'")).IsEmpty();
        await Assert.That(await host.RefusedAsync(host.Database.Name, Stmt("INSERT INTO players VALUES ('p9', 'green')"))).IsEqualTo("foreign_key_violation");
        await Assert.That(databases.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var undone = await host.RestoreAsync(await host.LatestAsync(cancellationToken), "after-rewind", cancellationToken);
        await Assert.That(await host.RowsAsync(undone, "SELECT id FROM players ORDER BY id")).IsEquivalentTo(["p1", "p2", "p3"]);
    }

    private static object Stmt(string sql, params object?[] args) => new { sql, args };

}
