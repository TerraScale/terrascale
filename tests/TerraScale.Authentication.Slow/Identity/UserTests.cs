using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Identity;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class UserTests(TerraBaseFixture fixture)
{

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
