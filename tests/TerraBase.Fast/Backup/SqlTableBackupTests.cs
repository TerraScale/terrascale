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

    private static object Stmt(string sql, params object?[] args) => new { sql, args };

}
