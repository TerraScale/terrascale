using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Store;

/// <summary>After its own region's TerraBase answers again, ts-auth stores there again within one connection lifetime (30 s).</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RegionalStorageRecoveryTests(TerraBaseFixture fixture)
{
    private const string Instance = "regional-recovery";

    [Test]
    public async Task Storage_returns_to_its_own_region_once_it_answers_again()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await AuthTestServer.StartAsync(services: services => TerraBaseFixture.UseRegionalStorage(services, tenant, own.Address, other));
        var users = auth.App.Services.GetRequiredService<Users>();
        await users.CreateAsync(Instance, "home@example.test");

        own.Stop();
        await Assert.That(await ReadinessWait.UntilAsync(async () => await AnswersAsync(users) && other.Accepted > 0, TimeSpan.FromSeconds(15))).IsTrue();
        own.Restart();
        var away = own.Accepted;

        await Assert.That(await ReadinessWait.UntilAsync(async () => await AnswersAsync(users) && own.Accepted > away, TimeSpan.FromSeconds(60))).IsTrue();
        await users.CreateAsync(Instance, "back@example.test");
        await Assert.That((await database.ListDocumentsAsync("users")).Items.Count).IsEqualTo(2);
    }

    // Whether a storage read went through; one on a connection that was just dropped fails with it.
    private static async Task<bool> AnswersAsync(Users users)
    {
        try
        {
            return await users.FindByEmailAsync(Instance, "home@example.test") is not null;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}
