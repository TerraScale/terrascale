using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Store;

/// <summary>After its own region's TerraBase answers again, ts-auth reads there again within one connection lifetime (30 s).</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RegionalStorageRecoveryTests(TerraBaseFixture fixture)
{
    private const string Instance = "regional-recovery";

    [Test]
    public async Task Get_reads_return_to_their_own_region_once_it_answers_again()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, "auth-local");
        await new Users(new AuthStore(local, TimeProvider.System), TimeProvider.System).CreateAsync(Instance, "home@example.test");
        await using var gru = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await AuthTestServer.StartAsync(
            services: services => TerraBaseFixture.UseRegionalStorage(services, tenant, own.Address, other, gru.Address, localDatabase: "auth-local"),
            routes: app => app.MapGet("/__test/home", async (Users users) =>
                await users.FindByEmailAsync(Instance, "home@example.test") is not null ? Results.Ok() : Results.NotFound()));

        own.Stop();
        await Assert.That(await ReadinessWait.UntilAsync(async () => await AnswersAsync(auth) && other.Accepted > 0, TimeSpan.FromSeconds(15))).IsTrue();
        own.Restart();
        var away = own.Accepted;

        await Assert.That(await ReadinessWait.UntilAsync(async () => await AnswersAsync(auth) && own.Accepted > away, TimeSpan.FromSeconds(60))).IsTrue();
    }

    // Whether a GET read went through; one on a connection that was just dropped fails with it (503).
    private static async Task<bool> AnswersAsync(AuthTestServer auth)
    {
        using var response = await auth.Client.GetAsync("/__test/home");
        return response.StatusCode == HttpStatusCode.OK;
    }
}
