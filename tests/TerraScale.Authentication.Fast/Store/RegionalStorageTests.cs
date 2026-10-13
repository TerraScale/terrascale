using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Store;

/// <summary>
/// ts-auth stores in its own region's TerraBase and in another region's when its own does not answer. Each region is a
/// relay to the suite's TerraBase, so the relays show where ts-auth connected and the database shows what it stored.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RegionalStorageTests(TerraBaseFixture fixture)
{
    private const string Instance = "regional-instance";

    [Test]
    public async Task Storage_stays_in_its_own_region_while_it_answers()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own.Address, other);

        await auth.App.Services.GetRequiredService<Users>().CreateAsync(Instance, "own@example.test");

        await Assert.That((await database.ListDocumentsAsync("users")).Items.Count).IsEqualTo(1);
        await Assert.That(own.Accepted).IsGreaterThan(0);
        await Assert.That(other.Accepted).IsEqualTo(0);
    }

    [Test]
    public async Task Storage_moves_to_another_region_when_its_own_stops_answering()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, own.Address, other);
        var users = auth.App.Services.GetRequiredService<Users>();
        await users.CreateAsync(Instance, "before@example.test");
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, TimeSpan.FromSeconds(15))).IsTrue();

        own.Stop();
        // A request already on the dropped connection fails with it; the next connection goes to the other region.
        await Assert.That(await ReadinessWait.UntilAsync(() => FindsAsync(users, "before@example.test"), TimeSpan.FromSeconds(15))).IsTrue();
        await users.CreateAsync(Instance, "after@example.test");

        await Assert.That(other.Accepted).IsGreaterThan(0);
        await Assert.That((await database.ListDocumentsAsync("users")).Items.Count).IsEqualTo(2);
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, TimeSpan.FromSeconds(15))).IsTrue();
    }

    [Test]
    public async Task Storage_uses_another_region_when_its_own_has_no_started_machine()
    {
        // Fly lists started machines only, so a region without one does not resolve; neither does this name off Fly.
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await StartAsync(tenant, new Uri($"http://sjc.ts-auth-test-{Guid.NewGuid():N}.internal:8081/"), other);

        await auth.App.Services.GetRequiredService<Users>().CreateAsync(Instance, "elsewhere@example.test");

        await Assert.That((await database.ListDocumentsAsync("users")).Items.Count).IsEqualTo(1);
        await Assert.That(other.Accepted).IsGreaterThan(0);
    }

    private static async Task<bool> FindsAsync(Users users, string email)
    {
        try
        {
            return await users.FindByEmailAsync(Instance, email) is not null;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    // The operator's signing-key provisioning runs first, so /readyz shows whether storage serves.
    private static Task<AuthTestServer> StartAsync(TerraBaseTestTenant tenant, Uri ownRegion, TcpRelay other) =>
        AuthTestServer.StartAsync(
            services: services => TerraBaseFixture.UseRegionalStorage(services, tenant, ownRegion, other),
            beforeStart: services => SigningKeyProvisioning.ProvisionAsync(services, CancellationToken.None));
}
