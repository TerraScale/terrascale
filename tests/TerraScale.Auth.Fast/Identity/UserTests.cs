using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Fast.Identity;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class UserTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task One_account_owns_each_normalized_email_per_instance()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var users = new Users(new AuthStore(database, clock), clock);
        var other = new Users(new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), clock), clock);

        await Assert.That(Users.NormalizeEmail("  Alice@Example.TEST  ")).IsEqualTo("alice@example.test");
        var alice = await users.CreateAsync("instance-a", "  Alice@Example.TEST  ");
        await Assert.That(alice).IsNotNull();
        await Assert.That(alice!.Email).IsEqualTo("alice@example.test");
        await Assert.That(alice.InstanceId).IsEqualTo("instance-a");
        await Assert.That(alice.AuthEpoch).IsEqualTo(0);
        await Assert.That(alice.CreatedAt).IsEqualTo(clock.UtcNow);
        await Assert.That(TerraScale.DotNet.TerraScaleIds.IsValid(alice.Id)).IsTrue();
        await Assert.That(await other.CreateAsync("instance-a", "alice@example.test")).IsNull();
        await Assert.That(await other.CreateAsync("instance-a", "ALICE@EXAMPLE.TEST")).IsNull();
        await Assert.That((await database.ListDocumentsAsync("users")).Items.Count).IsEqualTo(1);
        await Assert.That((await database.ListDocumentsAsync("uniques")).Items.Count).IsEqualTo(1);

        await Assert.That((await other.FindByEmailAsync("instance-a", "ALICE@example.test"))!.Id).IsEqualTo(alice.Id);
        await Assert.That((await other.GetAsync("instance-a", alice.Id))!.Email).IsEqualTo("alice@example.test");
        await Assert.That(await other.FindByEmailAsync("instance-b", "alice@example.test")).IsNull();
        await Assert.That(await other.GetAsync("instance-b", alice.Id)).IsNull();

        var elsewhere = await other.CreateAsync("instance-b", "Alice@example.test");
        await Assert.That(elsewhere).IsNotNull();
        await Assert.That(elsewhere!.Id).IsNotEqualTo(alice.Id);
        await Assert.That((await users.FindByEmailAsync("instance-b", "alice@example.test"))!.Id).IsEqualTo(elsewhere.Id);
        await Assert.That((await users.FindByEmailAsync("instance-a", "alice@example.test"))!.Id).IsEqualTo(alice.Id);
        await Assert.That((await database.ListDocumentsAsync("users")).Items.Count).IsEqualTo(2);
        await Assert.That((await database.ListDocumentsAsync("uniques")).Items.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_254_character_email_is_accepted()
    {
        var (_, database) = await fixture.CreateDatabaseAsync();
        var users = new Users(new AuthStore(database, TimeProvider.System), TimeProvider.System);
        var email = new string('a', 241) + "@example.test";

        await Assert.That(email.Length).IsEqualTo(254);
        await Assert.That((await users.CreateAsync("instance", email))!.Email).IsEqualTo(email);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("alice smith@example.test")]
    [Arguments("alice@example .test")]
    [Arguments("alice.example.test")]
    [Arguments("alice@bob@example.test")]
    [Arguments("@example.test")]
    [Arguments("alice@localhost")]
    [Arguments("alice@.")]
    [Arguments("alice@.test")]
    [Arguments("alice@example.")]
    [Arguments("alice@example..test")]
    [Arguments("ali\u0000ce@example.test")]
    [Arguments("alice\u007f@example.test")]
    [Arguments("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@example.test")]
    public async Task Implausible_emails_are_rejected_without_writing(string email)
    {
        var (_, database) = await fixture.CreateDatabaseAsync();
        var users = new Users(new AuthStore(database, TimeProvider.System), TimeProvider.System);

        await Assert.That(Users.NormalizeEmail(email)).IsNull();
        await Assert.ThrowsAsync<ArgumentException>(() => users.CreateAsync("instance", email));
        await Assert.That(await users.FindByEmailAsync("instance", email)).IsNull();
        await Assert.That((await database.ListDocumentsAsync("users")).Items.Count).IsEqualTo(0);
        await Assert.That((await database.ListDocumentsAsync("uniques")).Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Concurrent_creation_from_independent_stores_has_exactly_one_owner()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 16).Select(async index =>
        {
            var users = new Users(new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), TimeProvider.System), TimeProvider.System);
            await start.Task;
            return await users.CreateAsync("instance", index % 2 == 0 ? "Contended@Example.test" : "contended@example.test");
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);

        await Assert.That(results.Count(user => user is not null)).IsEqualTo(1);
        await Assert.That((await database.ListDocumentsAsync("users")).Items.Count).IsEqualTo(1);
        await Assert.That((await database.ListDocumentsAsync("uniques")).Items.Count).IsEqualTo(1);
        var independent = new Users(new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), TimeProvider.System), TimeProvider.System);
        await Assert.That((await independent.FindByEmailAsync("instance", "contended@example.test"))!.Id).IsEqualTo(results.Single(user => user is not null)!.Id);
    }
}
