using Microsoft.EntityFrameworkCore;

namespace TerraScale.EntityFrameworkCore.Tests;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TerraBaseSessionTests(TerraBaseFixture fixture)
{
    private readonly TerraBaseServer server = fixture.Server;

    [Test]
    [Timeout(10_000)]
    public async Task A_peer_credential_client_migrates_runs_transactions_and_saves_large_change_sets(CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseTerraBase(await server.CreatePeerDatabaseAsync(cancellationToken)).Options;
        await using (var context = new AppDbContext(options))
        {
            await context.Database.MigrateAsync(cancellationToken);
            await Assert.That(await context.Database.GetAppliedMigrationsAsync(cancellationToken)).IsEquivalentTo(context.Database.GetMigrations());
        }

        var id = server.NewId();
        await using (var context = new AppDbContext(options))
        {
            await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                context.Accounts.Add(server.NewAccount(id, "peer@example.com"));
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            });
            context.Sessions.AddRange(Enumerable.Range(0, 300).Select(i => new Session { Id = server.NewId(), AccountId = id, Device = $"device {i}", ExpiresAt = DateTime.UtcNow }));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = new AppDbContext(options))
        {
            await Assert.That(await context.Sessions.CountAsync(session => session.AccountId == id, cancellationToken)).IsEqualTo(300);
        }
    }

    [Test]
    [Timeout(10_000)]
    public async Task Concurrent_migrators_apply_every_pending_migration_once(CancellationToken cancellationToken)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        var options = new DbContextOptionsBuilder<AppDbContext>().UseTerraBase(await server.CreateDatabaseAsync(http, cancellationToken)).Options;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var context = new AppDbContext(options);
            await context.Database.MigrateAsync(cancellationToken);
        }));

        await using var migrated = new AppDbContext(options);
        var migrations = migrated.Database.GetMigrations().ToList();
        await Assert.That(migrations.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(await migrated.Database.GetAppliedMigrationsAsync(cancellationToken)).IsEquivalentTo(migrations);
        await Assert.That(await migrated.Database.SqlQueryRaw<long>("""SELECT COUNT(*) AS "Value" FROM "__EFMigrationsHistory" """).SingleAsync(cancellationToken))
            .IsEqualTo(migrations.Count);
        await Assert.That(await migrated.Sessions.CountAsync(session => session.RevokedAt != null, cancellationToken)).IsEqualTo(0);
    }

    [Test]
    [Timeout(10_000)]
    public async Task A_synchronously_disposed_transaction_surfaces_the_callers_exception_and_frees_the_writer(CancellationToken cancellationToken)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        var database = await server.CreateDatabaseAsync(http, cancellationToken);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseTerraBase(database, terraBase => terraBase.CommandTimeout(2)).Options;
        await using (var context = new AppDbContext(options))
        {
            await context.Database.EnsureCreatedAsync(cancellationToken);
        }

        var held = server.NewId();
        var failure = await Assert.That(() => FailInsideTransactionAsync()).Throws<InvalidOperationException>();
        await Assert.That(failure!.Message).IsEqualTo("the caller's own failure");

        var id = server.NewId();
        await using (var context = new AppDbContext(options))
        {
            context.Accounts.Add(server.NewAccount(id, "next@example.com"));
            await context.SaveChangesAsync(cancellationToken);
            await Assert.That(await context.Accounts.Select(account => account.Id).ToListAsync(cancellationToken)).IsEquivalentTo([id]);
        }

        async Task FailInsideTransactionAsync()
        {
            await using var context = new AppDbContext(options);
            await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                context.Accounts.Add(server.NewAccount(held, "held@example.com"));
                await context.SaveChangesAsync(cancellationToken);
                await using (var blocked = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseTerraBase(database, terraBase => terraBase.CommandTimeout(1)).Options))
                {
                    await Assert.That(() => blocked.Database.ExecuteSqlRawAsync("""UPDATE "Accounts" SET "Verified" = 1 WHERE "Id" = 'none'""", cancellationToken))
                        .Throws<TimeoutException>();
                }

                throw new InvalidOperationException("the caller's own failure");
            });
        }
    }
}
