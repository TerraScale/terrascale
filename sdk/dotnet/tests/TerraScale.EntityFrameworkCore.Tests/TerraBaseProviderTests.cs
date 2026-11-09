using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.EntityFrameworkCore.Tests;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TerraBaseProviderTests(TerraBaseFixture fixture)
{
    private readonly TerraBaseServer server = fixture.Server;

    [Test]
    [Timeout(10_000)]
    public async Task Migrates_then_saves_queries_updates_and_detects_conflicts(CancellationToken cancellationToken)
    {
        using var counter = new SqlRequestCounter();
        using var http = new HttpClient(counter);
        var database = await server.CreateDatabaseAsync(http, cancellationToken);
        await using var services = new ServiceCollection()
            .AddDbContextPool<AppDbContext>(options => options.UseTerraBase(database).ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)))
            .BuildServiceProvider();

        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Database.MigrateAsync(cancellationToken);
            await context.Database.MigrateAsync(cancellationToken);
            await Assert.That(await context.Database.GetAppliedMigrationsAsync(cancellationToken)).IsEquivalentTo([InitialSchema.Id]);
            await Assert.That(context.GetService<IMigrator>().GenerateScript()).Contains("CREATE UNIQUE INDEX \"IX_Accounts_Email\" ON \"Accounts\" (\"Email\");");
        }

        var account = new Account
        {
            Id = server.NewId(),
            Email = "ada@example.com",
            Version = server.NewId(),
            CreatedAt = new DateTimeOffset(2026, 11, 9, 10, 30, 15, 125, TimeSpan.FromHours(-3)),
            Status = AccountStatus.Active,
            Verified = true,
            Avatar = [1, 2, 3],
            Preferences = new("dark", 50),
        };
        var now = DateTime.UtcNow;
        account.Sessions.AddRange([
            new Session { Id = server.NewId(), Device = "laptop", ExpiresAt = now.AddDays(1) },
            new Session { Id = server.NewId(), Device = "old phone", ExpiresAt = now.AddDays(-1) },
            new Session { Id = server.NewId(), Device = "phone", ExpiresAt = now.AddDays(2) },
        ]);
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Accounts.Add(account);
            var before = counter.Executed;
            await context.SaveChangesAsync(cancellationToken);
            await Assert.That(counter.Executed - before).IsEqualTo(1);
        }

        await using var staleScope = services.CreateAsyncScope();
        var stale = staleScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var staleAccount = await stale.Accounts.SingleAsync(row => row.Id == account.Id, cancellationToken);
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loaded = await context.Accounts
                .Include(row => row.Sessions.Where(session => session.ExpiresAt > now).OrderBy(session => session.Device))
                .SingleAsync(row => row.Email == "ada@example.com", cancellationToken);
            await Assert.That(loaded.Sessions.Select(session => session.Device)).IsEquivalentTo(["laptop", "phone"]);
            await Assert.That(loaded.Preferences).IsEqualTo(new Preferences("dark", 50));
            await Assert.That(loaded.CreatedAt).IsEqualTo(account.CreatedAt);
            await Assert.That(loaded.CreatedAt.Offset).IsEqualTo(TimeSpan.FromHours(-3));
            await Assert.That(loaded.Avatar!).IsEquivalentTo(new byte[] { 1, 2, 3 });
            await Assert.That(loaded.Status).IsEqualTo(AccountStatus.Active);
            await Assert.That(loaded.Verified).IsTrue();
            await Assert.That(await context.Accounts.CountAsync(row => row.Preferences.Theme == "dark" && row.Preferences.PageSize > 10, cancellationToken)).IsEqualTo(1);
            string[] ids = [account.Id, server.NewId()];
            await Assert.That(await context.Accounts.Where(row => ids.Contains(row.Id)).Select(row => row.Email).ToListAsync(cancellationToken)).IsEquivalentTo(["ada@example.com"]);

            loaded.Email = "ada.lovelace@example.com";
            loaded.Version = server.NewId();
            loaded.Preferences = loaded.Preferences with { Theme = "light" };
            loaded.Sessions.RemoveAt(0);
            loaded.Sessions.Add(new Session { Id = server.NewId(), Device = "tablet", ExpiresAt = now.AddDays(3) });
            var before = counter.Executed;
            await context.SaveChangesAsync(cancellationToken);
            await Assert.That(counter.Executed - before).IsEqualTo(1);

            await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                context.Sessions.Add(new Session { Id = server.NewId(), AccountId = account.Id, Device = "watch", ExpiresAt = now.AddDays(4) });
                await context.SaveChangesAsync(cancellationToken);
                await Assert.That(await context.Sessions.CountAsync(session => session.AccountId == account.Id, cancellationToken)).IsEqualTo(4);
                await transaction.CommitAsync(cancellationToken);
            });
        }

        staleAccount.Status = AccountStatus.Suspended;
        staleAccount.Version = server.NewId();
        var conflict = await Assert.That(() => stale.SaveChangesAsync(cancellationToken)).Throws<DbUpdateConcurrencyException>();
        await Assert.That(conflict!.Entries.Single().Entity).IsSameReferenceAs(staleAccount);

        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var saved = await context.Accounts.AsNoTracking().Include(row => row.Sessions).SingleAsync(cancellationToken);
            await Assert.That(saved.Email).IsEqualTo("ada.lovelace@example.com");
            await Assert.That(saved.Status).IsEqualTo(AccountStatus.Active);
            await Assert.That(saved.Preferences.Theme).IsEqualTo("light");
            await Assert.That(saved.Sessions.Select(session => session.Device)).IsEquivalentTo(["old phone", "phone", "tablet", "watch"]);
        }
    }

    [Test]
    [Timeout(10_000)]
    public async Task A_conflict_or_unique_violation_rolls_back_the_whole_save(CancellationToken cancellationToken)
    {
        using var counter = new SqlRequestCounter();
        using var http = new HttpClient(counter);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseTerraBase(await server.CreateDatabaseAsync(http, cancellationToken)).Options;
        await using (var context = new AppDbContext(options))
        {
            await Assert.That(await context.Database.EnsureCreatedAsync(cancellationToken)).IsTrue();
            await Assert.That(await context.Database.EnsureCreatedAsync(cancellationToken)).IsFalse();
        }

        var id = server.NewId();
        await using (var context = new AppDbContext(options))
        {
            context.Accounts.Add(NewAccount(id, "grace@example.com"));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var stale = new AppDbContext(options);
        var staleAccount = await stale.Accounts.SingleAsync(row => row.Id == id, cancellationToken);
        await using (var context = new AppDbContext(options))
        {
            (await context.Accounts.SingleAsync(row => row.Id == id, cancellationToken)).Version = server.NewId();
            await context.SaveChangesAsync(cancellationToken);
        }

        stale.Accounts.AddRange([NewAccount(server.NewId(), "a@example.com"), NewAccount(server.NewId(), "b@example.com"), NewAccount(server.NewId(), "c@example.com")]);
        stale.Sessions.Add(new Session { Id = server.NewId(), AccountId = id, Device = "laptop", ExpiresAt = DateTime.UtcNow.AddDays(1) });
        staleAccount.Status = AccountStatus.Suspended;
        staleAccount.Version = server.NewId();
        var before = counter.Executed;
        var conflict = await Assert.That(() => stale.SaveChangesAsync(cancellationToken)).Throws<DbUpdateConcurrencyException>();
        await Assert.That(counter.Executed - before).IsEqualTo(1);
        await Assert.That(conflict!.Entries.Single().Entity).IsSameReferenceAs(staleAccount);

        await using (var context = new AppDbContext(options))
        {
            context.Accounts.AddRange([NewAccount(server.NewId(), "d@example.com"), NewAccount(server.NewId(), "grace@example.com")]);
            var duplicate = await Assert.That(() => context.SaveChangesAsync(cancellationToken)).Throws<DbUpdateException>();
            await Assert.That(duplicate!.InnerException).IsTypeOf<TerraBaseSqlException>();
            await Assert.That(((TerraBaseSqlException)duplicate.InnerException!).Code).IsEqualTo(TerraBaseSqlErrorCodes.UniqueViolation);
        }

        await using (var context = new AppDbContext(options))
        {
            await Assert.That(await context.Accounts.Select(row => row.Email).ToListAsync(cancellationToken)).IsEquivalentTo(["grace@example.com"]);
            await Assert.That(await context.Accounts.Where(row => row.Id == id).Select(row => row.Status).SingleAsync(cancellationToken)).IsEqualTo(AccountStatus.Pending);
            await Assert.That(await context.Sessions.CountAsync(cancellationToken)).IsEqualTo(0);
        }

        Account NewAccount(string accountId, string email) => new()
        {
            Id = accountId,
            Email = email,
            Version = server.NewId(),
            CreatedAt = DateTimeOffset.UtcNow,
            Preferences = new("system", 25),
        };
    }
}
