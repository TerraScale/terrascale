using System.Net;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Store;

/// <summary>After its own region's TerraBase answers again, ts-auth reads discovery there again within one connection lifetime (30 s).</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RegionalStorageRecoveryTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Discovery_reads_return_to_their_own_region_once_it_answers_again()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var local = await fixture.CreateCopyAsync(tenant, "auth-local");
        var project = AuthStore.NewId();
        await new AuthStore(local, TimeProvider.System).CreateAsync(AuthStore.ProjectCollection, OidcIssuer.InstanceId, project, TerraBaseFixture.Json("{}"));
        await using var gru = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var own = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var other = TcpRelay.Start(fixture.Server.InternalAddress);
        await using var auth = await AuthTestServer.StartAsync(
            services: services => TerraBaseFixture.UseRegionalStorage(services, tenant, own.Address, other, gru.Address, localDatabase: "auth-local"));

        own.Stop();
        await Assert.That(await ReadinessWait.UntilAsync(async () => await AnswersAsync(auth, project) && other.Accepted > 0, TimeSpan.FromSeconds(15))).IsTrue();
        own.Restart();
        var away = own.Accepted;

        await Assert.That(await ReadinessWait.UntilAsync(async () => await AnswersAsync(auth, project) && own.Accepted > away, TimeSpan.FromSeconds(60))).IsTrue();
    }

    // Whether the project's discovery document was served; a request on a connection that was just dropped fails (503).
    private static async Task<bool> AnswersAsync(AuthTestServer auth, string project)
    {
        using var response = await auth.Client.GetAsync($"/{project}/.well-known/openid-configuration");
        return response.StatusCode == HttpStatusCode.OK;
    }
}
