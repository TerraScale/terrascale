using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Management;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AccountDeletionTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Deleting_an_account_refuses_its_token_and_releases_its_email()
    {
        await using var oidc = await OidcHarness.StartSharedManagementAsync(fixture);
        var (user, token) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("erase"));
        await oidc.OkAsync(HttpMethod.Delete, "/v1/terrascale/account", token, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, PlatformCalls.Organizations, token)).IsEqualTo((401, "unauthorized"));
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        await Assert.That(await users.GetAsync(user.InstanceId, user.Id)).IsNull();
        await Assert.That(await users.FindByEmailAsync(user.InstanceId, user.Email)).IsNull();
        await Assert.That((await oidc.CreateUserAsync(user.Email)).Id).IsNotEqualTo(user.Id);
    }
}
