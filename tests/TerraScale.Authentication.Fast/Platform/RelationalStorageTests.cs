using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Storage;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Platform;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RelationalStorageTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Replicas_starting_on_an_empty_database_migrate_it_once_and_seed_the_platform_organization_without_errors()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync(provision: false);
        await Assert.That(await database.TableNamesAsync()).IsEmpty();
        var logs = new CapturingLogs();

        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var starting = Enumerable.Range(0, 2).Select(_ => AuthTestServer.StartAsync(settings: values => values["master-key"] = masterKey,
            services: services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                fixture.UseStorage(services, tenant);
            })).ToArray();
        await using var first = await starting[0];
        await using var second = await starting[1];
        await Task.WhenAll(first.App.Services.GetRequiredService<AuthSchema>().Ready, second.App.Services.GetRequiredService<AuthSchema>().Ready)
            .WaitAsync(TimeSpan.FromSeconds(8));

        await using var db = database.Context();
        await Assert.That(await db.Database.GetPendingMigrationsAsync()).IsEmpty();
        await Assert.That((await db.Database.GetAppliedMigrationsAsync()).Count()).IsEqualTo(db.Database.GetMigrations().Count());
        var organization = await db.Organizations.SingleAsync();
        await Assert.That(organization.Id).IsEqualTo(PlatformBootstrap.OrganizationId);
        await Assert.That(organization.Projects).IsEqualTo(2);
        await Assert.That(await db.Projects.Select(project => project.Id).OrderBy(id => id).ToListAsync())
            .IsEquivalentTo([PlatformBootstrap.AuthProjectId, PlatformBootstrap.DashboardProjectId]);
        var owner = await db.Users.SingleAsync();
        await Assert.That(owner.Email).IsEqualTo(AuthTestServer.BootstrapOwnerEmail);
        await Assert.That((await db.Memberships.SingleAsync()).Role).IsEqualTo(Roles.Owner);
        await Assert.That(logs.AtLeast(LogLevel.Error)).IsEmpty();
    }

    [Test]
    public async Task Background_workers_wait_for_the_schema_so_startup_on_an_empty_database_logs_no_warning()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync(provision: false);
        var logs = new CapturingLogs();

        await using var server = await AuthTestServer.StartAsync(services: services =>
        {
            services.AddSingleton<ILoggerProvider>(logs);
            fixture.UseStorage(services, tenant);
        });
        await server.App.Services.GetRequiredService<AuthSchema>().Ready.WaitAsync(TimeSpan.FromSeconds(8));

        await Assert.That(logs.AtLeast(LogLevel.Warning)).IsEmpty();
    }

    [Test]
    public async Task Concurrent_sign_ups_of_one_email_create_one_account_and_the_others_see_it_taken()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await AuthTestServer.StartAsync(services: services => target.Register(services));
        var users = server.App.Services.GetRequiredService<Users>();

        var created = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => users.CreateAsync("unique-instance", "Taken@Example.test")));

        await Assert.That(created.Count(user => user is not null)).IsEqualTo(1);
        var stored = await target.Database.ReadAsync(db => db.Users.Where(user => user.InstanceId == "unique-instance").ToListAsync());
        await Assert.That(stored.Select(user => (user.Id, user.Email))).IsEquivalentTo([(created.Single(user => user is not null)!.Id, "taken@example.test")]);
        var duplicate = stored[0] with { Id = AuthIds.New(), AuthStamp = AuthIds.New() };
        await Assert.That(await server.App.Services.GetRequiredService<AuthData>().SaveAsync(Change.Insert(duplicate), CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task Two_writers_of_one_version_commit_once_and_the_later_one_conflicts()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await AuthTestServer.StartAsync(services: services => target.Register(services));
        var data = server.App.Services.GetRequiredService<AuthData>();
        var user = (await server.App.Services.GetRequiredService<Users>().CreateAsync("conflict-instance", "writer@example.test"))!;
        var read = (await server.App.Services.GetRequiredService<Users>().GetAsync("conflict-instance", user.Id))!;

        var first = data.SaveAsync(Change.Update(read with { Disabled = true }), CancellationToken.None);
        var second = data.SaveAsync(Change.Update(read with { PasswordHash = "replaced" }), CancellationToken.None);

        await Assert.That((await Task.WhenAll(first, second)).Count(saved => saved)).IsEqualTo(1);
        var stored = await target.Database.ReadAsync(db => db.Users.SingleAsync(row => row.Id == user.Id));
        await Assert.That(stored.Disabled ^ stored.PasswordHash == "replaced").IsTrue();
        await Assert.That(stored.Version).IsNotEqualTo(read.Version);
    }
}
