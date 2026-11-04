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
    public async Task A_manual_backup_restores_sql_tables_as_they_were_when_it_was_taken(CancellationToken cancellationToken)
    {
        await using var host = await SqlBackupHost.StartAsync(cancellationToken);
        await host.OkAsync("CREATE TABLE items (id TEXT PRIMARY KEY, v TEXT NOT NULL)", "CREATE UNIQUE INDEX items_v ON items (v)");
        await host.OkAsync(Stmt("INSERT INTO items VALUES ('kept', ?)", Faker.Lorem.Word()));

        using var created = await host.Api.Client.PostAsync(ManagementApi.Uri($"{host.Database.Path}/backups"), content: null, cancellationToken);
        var backupId = (await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("id").GetString();
        await host.OkAsync(Stmt("INSERT INTO items VALUES ('later', ?)", Faker.Lorem.Sentence()), Stmt("DELETE FROM items WHERE id = 'kept'"));
        using var restored = await host.Api.Client.PostAsJsonAsync(ManagementApi.Uri($"{host.Database.Path}/backups/{backupId}/restore"), new { name = "manual" }, cancellationToken);

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(await host.RowsAsync("manual", "SELECT id FROM items")).IsEquivalentTo(["kept"]);
        await Assert.That(await host.RowsAsync("manual", "SELECT name FROM sqlite_schema WHERE type = 'index' AND name = 'items_v'")).IsEquivalentTo(["items_v"]);
        await Assert.That(await host.RowsAsync(host.Database.Name, "SELECT id FROM items")).IsEquivalentTo(["later"]);
    }

    [Test]
    public async Task A_restored_database_is_a_new_incarnation_and_a_replica_of_the_source_rebootstraps_from_it(CancellationToken cancellationToken)
    {
        await using var host = await SqlBackupHost.StartAsync(cancellationToken);
        await host.OkAsync("CREATE TABLE items (id TEXT PRIMARY KEY, v TEXT NOT NULL)");
        var rows = Enumerable.Range(0, 4).Select(_ => (Id: EntityIds.New().ToString(), Value: Faker.Lorem.Word())).ToList();
        await host.OkAsync([.. rows.Select(row => Stmt("INSERT INTO items VALUES (?, ?)", row.Id, row.Value))]);
        await host.SnapshotAsync(cancellationToken);
        await host.OkAsync(Stmt("DELETE FROM items WHERE id = ?", rows[0].Id));
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var replica = new RevisionReplica();
        var source = await replica.PullAsync(host.Api.Server, host.Tenant);

        var latest = await host.LatestAsync(cancellationToken);
        var restored = await host.RestoreAsync(latest, "restored", cancellationToken);
        var again = await host.RestoreAsync(latest, "again", cancellationToken);
        var restoredTenant = await host.TenantAsync(restored, cancellationToken);
        var rebootstrap = await replica.PullAsync(host.Api.Server, restoredTenant);
        var steady = await replica.PullAsync(host.Api.Server, restoredTenant);
        var other = await new RevisionReplica().PullAsync(host.Api.Server, await host.TenantAsync(again, cancellationToken));

        await Assert.That(rebootstrap.Full).IsEqualTo(FullReason.OtherIncarnation);
        await Assert.That(rebootstrap.Revision.Incarnation).IsNotEqualTo(source.Revision.Incarnation);
        await Assert.That(other.Revision.Incarnation).IsNotEqualTo(source.Revision.Incarnation).And.IsNotEqualTo(rebootstrap.Revision.Incarnation);
        await Assert.That(steady.Full).IsNull();
        await Assert.That(replica.Rows()).IsEquivalentTo(rows.Skip(1).ToDictionary(row => row.Id, row => row.Value));
    }

    private static object Stmt(string sql, params object?[] args) => new { sql, args };

}
