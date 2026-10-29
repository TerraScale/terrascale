using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Identity;

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

}
