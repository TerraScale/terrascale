using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Management;

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
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
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
        foreach (var (collection, field) in new[] { (AuthStore.SessionCollection, "/userId"), (AuthStore.MembershipCollection, "/userId"), (AuthStore.ProjectMemberCollection, "/userId"), (AuthStore.OidcTokenCollection, "/subject"), (AuthStore.OidcAuthorizationCollection, "/subject") })
        {
            await Assert.That(await store.QueryAsync(collection, "terrascale", field, alice.Id).CountAsync()).IsEqualTo(0);
        }

        // Its sole organization is gone with its project; the shared one stays, without it.
        await Assert.That(await store.GetAsync(AuthStore.OrganizationCollection, "terrascale", own)).IsNull();
        await Assert.That(await store.GetAsync(AuthStore.ProjectCollection, "terrascale", project)).IsNull();
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

    [Test]
    public async Task The_last_owner_of_an_organization_with_other_members_cannot_delete_its_account()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, runClosureWorker: false);
        var (alice, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var own = await oidc.CreateOrganizationAsync(aliceToken, "Alone");
        var shared = await oidc.CreateOrganizationAsync(aliceToken, "Shared");
        await Assert.That(await oidc.AddMemberAsync(shared, bob.Id, "member")).IsTrue();

        await Assert.That(await oidc.FailAsync(HttpMethod.Delete, Account, aliceToken)).IsEqualTo((409, "last_owner"));
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        await Assert.That(await store.GetAsync(AuthStore.DeletionJobCollection, "terrascale", DeletionJob.Key("terrascale", "account", alice.Id))).IsNull();

        // Nothing was deleted, not even the organization it owns alone.
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, Organizations, aliceToken)).GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{own}", aliceToken);
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{shared}/members", bobToken)).GetProperty("items").GetArrayLength()).IsEqualTo(2);

        // With a second owner it can go.
        await oidc.OkAsync(HttpMethod.Put, $"{Organizations}/{shared}/members/{bob.Id}/role", aliceToken, new { role = "owner" });
        await oidc.OkAsync(HttpMethod.Delete, Account, aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{shared}/members", bobToken)).GetProperty("items").GetArrayLength()).IsEqualTo(1);
    }

}
