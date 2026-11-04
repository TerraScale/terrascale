using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Sql;

public sealed class SqlBillingTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Sql_storage_counts_against_the_quota_and_deletes_free_it()
    {
        await using var db = await StartAsync(services => services.AddSingleton<IStorageQuota>(provider =>
            new StorageQuota(provider.GetRequiredService<IStorageEngine>(), provider.GetRequiredService<ITenantDirectory>(),
                plan => plan == PlanTier.Hobby ? 64 * 1024 : long.MaxValue)));
        await db.OkAsync("CREATE TABLE files (id INTEGER PRIMARY KEY, data BLOB NOT NULL)");

        string? full = null;
        for (var i = 0; i < 64 && full is null; i++)
        {
            var (status, body) = await db.PostAsync(db.Writer, Stmt("INSERT INTO files (data) VALUES (randomblob(3000))"));
            full = status == HttpStatusCode.OK ? null : body.GetProperty("error").GetProperty("sqlite_code").GetString();
        }

        var rows = await db.CountAsync("files");
        await db.OkAsync("DELETE FROM files");
        await db.OkAsync("INSERT INTO files (data) VALUES (randomblob(3000))");

        await Assert.That(full).IsEqualTo("SQLITE_FULL");
        await Assert.That(rows).IsGreaterThan(0).And.IsLessThan(64);
        await Assert.That(await db.CountAsync("files")).IsEqualTo(1);
    }

    [Test]
    public async Task Rows_undone_by_rollback_to_a_savepoint_are_not_billed()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");
        var before = await WritesAsync(db);

        await db.OkAsync(
            Stmt("SAVEPOINT s"), Stmt("INSERT INTO notes (body) VALUES (?)", Faker.Lorem.Sentence()), Stmt("ROLLBACK TO s"),
            Stmt("INSERT INTO notes (body) VALUES (?)", Faker.Lorem.Sentence()));

        await Assert.That(await WritesAsync(db) - before).IsEqualTo(1);
        await Assert.That(await db.CountAsync("notes")).IsEqualTo(1);
    }

    private static async Task<long> WritesAsync(SqlNativeDatabase db)
    {
        var meter = db.Api.Server.Services.GetRequiredService<UsageMeter>();
        var tenant = new TenantContext(db.Database.Organization, PlanTier.Hobby, db.Database.Project, db.Database.Id);
        long writes = -1;
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            await meter.FlushAsync(token);
            var window = new UsageWindow(UsageWindow.Hour, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600, 1);
            if (await meter.GetAsync(tenant, window, token) is not UsageCounts counts || meter.PendingDatabases != 0)
            {
                return false;
            }

            writes = counts.Lifetime.Writes;
            return true;
        }, timeout: TimeSpan.FromSeconds(30));
        return writes;
    }
}
