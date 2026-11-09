using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Slow.Management;

/// <summary><c>DELETE /v1/terrascale/account</c>: a signed-in user deletes its own account, over real HTTP on the real TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AccountDeletionTests(TerraBaseFixture fixture)
{
    private const string Account = "/v1/terrascale/account";

    [Test]
    public async Task Deleting_an_account_deletes_its_own_organizations_leaves_shared_ones_and_frees_the_email()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false, clock: clock);
        var (alice, session, tokens) = await oidc.SignInAndRedeemAsync("alice@example.test");
        var aliceToken = tokens.GetProperty("access_token").GetString()!;
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var own = await oidc.CreateOrganizationAsync(aliceToken, "Alone");
        var project = await oidc.CreateProjectAsync(aliceToken, own, "Solo");
        var shared = await oidc.CreateOrganizationAsync(bobToken, "Shared");
        var sharedProject = await oidc.CreateProjectAsync(bobToken, shared, "Team");
        await Assert.That(await oidc.AddMemberAsync(shared, alice.Id, "admin")).IsTrue();
        await Assert.That(await oidc.GrantAsync(sharedProject, alice.Id, "admin")).IsTrue();

        await oidc.OkAsync(HttpMethod.Delete, Account, aliceToken, status: HttpStatusCode.NoContent);

        // The token, the refresh token and the session stop working; nothing keyed by the user is left.
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, aliceToken)).IsEqualTo((401, "unauthorized"));
        using (var refreshed = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!))
        {
            await Assert.That(refreshed.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(await oidc.Server.App.Services.GetRequiredService<Sessions>().ResolveAsync("terrascale", session)).IsNull();
        var left = await oidc.Database.ReadAsync(async db =>
            await db.Sessions.CountAsync(row => row.UserId == alice.Id) + await db.Memberships.CountAsync(row => row.UserId == alice.Id)
            + await db.ProjectMembers.CountAsync(row => row.UserId == alice.Id) + await db.OidcTokens.CountAsync(row => row.Subject == alice.Id)
            + await db.OidcAuthorizations.CountAsync(row => row.Subject == alice.Id));
        await Assert.That(left).IsEqualTo(0);

        // Its sole organization is gone with its project; the shared one stays, without it.
        await Assert.That(await oidc.Database.ReadAsync(db => db.Organizations.AnyAsync(row => row.Id == own))).IsFalse();
        await Assert.That(await oidc.Database.ReadAsync(db => db.Projects.AnyAsync(row => row.Id == project))).IsFalse();
        var members = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{shared}/members", bobToken)).GetProperty("items");
        await Assert.That(members.EnumerateArray().Select(member => member.GetProperty("user_id").GetString()!).ToArray()).IsEquivalentTo([bob.Id]);
        await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{shared}/projects/{sharedProject}", bobToken);

        // The email is unknown, and signs up again as a new account.
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<Users>().FindByEmailAsync("terrascale", "alice@example.test")).IsNull();
        clock.Advance(HostedAuth.MailCooldown);
        var (again, againToken) = await oidc.SignInUserAsync("alice@example.test");
        await Assert.That(again.Id).IsNotEqualTo(alice.Id);
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, Organizations, againToken)).GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }
}
