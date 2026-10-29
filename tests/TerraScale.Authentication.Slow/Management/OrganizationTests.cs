using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Slow.Management;

/// <summary>
/// The platform management API (TS-598): organizations, members, projects and project roles, over real HTTP on the
/// real TerraBase, as signed-in users.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class OrganizationTests(TerraBaseFixture fixture)
{
    private const string ServiceId = "01M4EQ2FRJ8S8B29E8TQVWNW4K";
    private const string ServiceSecret = "service-test-secret-0123456789abcdefghij";

    [Test]
    public async Task Project_lists_use_the_fresh_inherited_role_after_an_admin_is_demoted()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, ownerToken) = await oidc.SignInUserAsync("owner@example.test");
        var (member, memberToken) = await oidc.SignInUserAsync("member@example.test");
        var organization = await oidc.CreateOrganizationAsync(ownerToken);
        await oidc.AddMemberAsync(organization, member.Id, "admin");
        var project = await oidc.CreateProjectAsync(ownerToken, organization, "Production");
        var path = $"{Organizations}/{organization}/projects";
        await oidc.OkAsync(HttpMethod.Put, $"{path}/{project}/members/{member.Id}", ownerToken, new { role = "member" });

        foreach (var token in new[] { ownerToken, memberToken })
        {
            var page = await oidc.OkAsync(HttpMethod.Get, path, token);
            await Assert.That(page.GetProperty("items")[0].GetProperty("role").GetString()).IsEqualTo("admin");
        }

        await oidc.OkAsync(HttpMethod.Put, $"{Organizations}/{organization}/members/{member.Id}/role", ownerToken, new { role = "viewer" });
        var demoted = await oidc.OkAsync(HttpMethod.Get, path, memberToken);
        await Assert.That(demoted.GetProperty("items")[0].GetProperty("role").GetString()).IsEqualTo("member");
        await oidc.OkAsync(HttpMethod.Delete, $"{path}/{project}/members/{member.Id}", ownerToken, status: HttpStatusCode.NoContent);
        var inherited = await oidc.OkAsync(HttpMethod.Get, path, memberToken);
        await Assert.That(inherited.GetProperty("items")[0].GetProperty("role").GetString()).IsEqualTo("viewer");
    }

    [Test]
    public async Task Signing_out_everywhere_closes_the_api_to_every_token_issued_before()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, token) = await oidc.SignInUserAsync("alice@example.test");
        await oidc.OkAsync(HttpMethod.Get, Organizations, token);

        // Sign-out everywhere and a password reset both advance the epoch the token carries.
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<TerraScale.Authentication.Features.Identity.Sessions>().RevokeAllAsync("terrascale", user.Id)).IsTrue();
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, token)).IsEqualTo((401, "unauthorized"));

        clock.Advance(HostedAuth.MailCooldown);
        var session = await oidc.SignInAsync("alice@example.test");
        var (verifier, challenge) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, challenge), verifier);
        var fresh = (await OidcHarness.JsonAsync(redeemed)).GetProperty("access_token").GetString()!;
        await oidc.OkAsync(HttpMethod.Get, Organizations, fresh);
    }

    [Test]
    public async Task An_access_token_whose_entry_is_revoked_is_refused_while_its_sign_in_stays()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        await oidc.OkAsync(HttpMethod.Get, Organizations, token);

        // The token names its own entry (oi_tkn_id) besides its sign-in (oi_au_id); revoking only the entry closes it.
        var claims = JsonSerializer.Deserialize<JsonElement>(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Decode(token.Split('.')[1]));
        await using var scope = oidc.Server.App.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictTokenManager>();
        var entry = await tokens.FindByIdAsync(claims.GetProperty("oi_tkn_id").GetString()!);
        await Assert.That(await tokens.TryRevokeAsync(entry!)).IsTrue();
        var authorizations = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictAuthorizationManager>();
        var authorization = await authorizations.FindByIdAsync(claims.GetProperty("oi_au_id").GetString()!);
        await Assert.That(await authorizations.HasStatusAsync(authorization!, OpenIddict.Abstractions.OpenIddictConstants.Statuses.Valid)).IsTrue();

        await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, token)).IsEqualTo((401, "unauthorized"));
    }

    [Test]
    public async Task A_sign_in_without_a_refresh_token_ends_when_its_access_token_is_revoked()
    {
        // No offline_access: no refresh token, and only the access token's own entry to revoke.
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync("erin@example.test");
        var session = await oidc.SignInAsync("erin@example.test");
        var (verifier, challenge) = OidcHarness.Pkce();
        using var authorize = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, scope: "openid email"), session);
        using var granted = await oidc.RedeemCodeAsync(OidcHarness.Query(authorize.Headers.Location!)["code"]!, verifier);
        await Assert.That(granted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(granted);
        await Assert.That(tokens.TryGetProperty("refresh_token", out _)).IsFalse();
        var access = tokens.GetProperty("access_token").GetString()!;
        await oidc.OkAsync(HttpMethod.Get, Organizations, access);

        using (var revoked = await oidc.RevokeAsync(access, "access_token"))
        {
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, access)).IsEqualTo((401, "unauthorized"));
    }

    [Test]
    public async Task A_sign_in_revoked_on_one_replica_is_refused_at_once_on_another()
    {
        // Each replica reads the sign-in from TerraBase on every call; nothing it saw earlier is reused.
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await using var replica = await oidc.StartReplicaAsync();
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync("alice@example.test");
        var access = tokens.GetProperty("access_token").GetString()!;
        await oidc.OkAsync(HttpMethod.Get, Organizations, access);
        await oidc.OkAsync(HttpMethod.Get, Organizations, access);

        using (var revoke = new HttpRequestMessage(HttpMethod.Post, "/terrascale/oauth/revoke")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = tokens.GetProperty("refresh_token").GetString()!, ["token_type_hint"] = "refresh_token" }),
        })
        {
            revoke.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"01M4EQ2FRJ8S8B29E8TQVWNW4J:{OidcHarness.ClientSecret}")));
            using var revoked = await replica.Client.SendAsync(revoke);
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, access)).IsEqualTo((401, "unauthorized"));
    }

    [Test]
    public async Task Lists_page_in_id_order()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        var created = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            created.Add(await oidc.CreateOrganizationAsync(token, $"Org {index}"));
        }

        var seen = new List<string>();
        string? after = null;
        do
        {
            var page = await oidc.OkAsync(HttpMethod.Get, $"{Organizations}?limit=2{(after is null ? string.Empty : $"&after={Uri.EscapeDataString(after)}")}", token);
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!));
            after = page.GetProperty("next").GetString();
        }
        while (after is not null);

        await Assert.That(seen.SequenceEqual(created.Order(StringComparer.Ordinal))).IsTrue();
        foreach (var limit in new[] { "0", "101", "x", "-1" })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}?limit={limit}", token)).IsEqualTo((400, "invalid_request"));
        }

        // A cursor is TerraBase's, bound to its list: another list's or a forged one is refused.
        var members = await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{created[0]}/members?limit=1", token);
        await Assert.That(members.GetProperty("next").ValueKind).IsEqualTo(JsonValueKind.Null);
        var first = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}?limit=1", token)).GetProperty("next").GetString()!;
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{created[0]}/projects?after={Uri.EscapeDataString(first)}", token)).IsEqualTo((400, "invalid_request"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}?after=forged", token)).IsEqualTo((400, "invalid_request"));
    }

    [Test]
    public async Task A_change_paused_before_its_commit_loses_to_what_happened_meanwhile()
    {
        // The primary's TerraBase client holds the next transaction after every read of the change; a replica commits the
        // competing change; then the held transaction goes through. It must fail and be decided again on fresh reads.
        var pause = new PausingTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, services: services => services.AddSingleton(new HttpClient(pause)));
        await using var replica = await oidc.StartReplicaAsync();
        var (_, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (carol, carolToken) = await oidc.SignInUserAsync("carol@example.test");
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        await oidc.AddMemberAsync(id, carol.Id, "admin");
        await oidc.AddMemberAsync(id, bob.Id, "member");
        var project = await oidc.CreateProjectAsync(aliceToken, id, "Production");

        // A caller demoted after its check cannot finish with its old authority.
        var rename = await pause.HoldNextAsync(() => oidc.FailAsync(HttpMethod.Patch, $"{Organizations}/{id}", carolToken, new { name = "Carol's" }));
        await OnReplicaAsync(oidc, replica, HttpMethod.Put, $"{Organizations}/{id}/members/{carol.Id}/role", aliceToken, new { role = "viewer" });
        await Assert.That(await pause.ReleaseAsync(rename)).IsEqualTo((403, "forbidden_role"));
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}", aliceToken)).GetProperty("name").GetString()).IsEqualTo("Acme");

        // Two owners leaving at once: the one held loses, so one owner stays.
        await OnReplicaAsync(oidc, replica, HttpMethod.Put, $"{Organizations}/{id}/members/{carol.Id}/role", aliceToken, new { role = "owner" });
        var demotion = await pause.HoldNextAsync(() => oidc.FailAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{carol.Id}/role", carolToken, new { role = "admin" }));
        await Assert.That((await OnReplicaAsync(oidc, replica, HttpMethod.Post, $"{Organizations}/{id}/leave", aliceToken)).Status).IsEqualTo(204);
        await Assert.That(await pause.ReleaseAsync(demotion)).IsEqualTo((409, "last_owner"));
        var owners = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/members", carolToken)).GetProperty("items").EnumerateArray()
            .Where(member => member.GetProperty("role").GetString() == "owner").Select(member => member.GetProperty("user_id").GetString()!).ToArray();
        await Assert.That(owners).IsEquivalentTo([carol.Id]);

        // A removal that read the member's grants before a new grant committed is decided again and removes that one too.
        var removal = await pause.HoldNextAsync(() => oidc.FailAsync(HttpMethod.Delete, $"{Organizations}/{id}/members/{bob.Id}", carolToken));
        await Assert.That((await OnReplicaAsync(oidc, replica, HttpMethod.Put, $"{Organizations}/{id}/projects/{project}/members/{bob.Id}", carolToken, new { role = "admin" })).Status).IsEqualTo(200);
        await Assert.That((await pause.ReleaseAsync(removal)).Status).IsEqualTo(204);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{project}", bobToken)).IsEqualTo((404, "not_found"));
        var grants = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{project}/members", carolToken)).GetProperty("items");
        await Assert.That(grants.EnumerateArray().Any(member => member.GetProperty("user_id").GetString() == bob.Id)).IsFalse();
    }

    [Test]
    public async Task A_role_change_paused_while_its_organization_is_deleted_changes_nothing()
    {
        var pause = new PausingTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, services: services => services.AddSingleton(new HttpClient(pause)));
        var (_, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, _) = await oidc.SignInUserAsync("bob@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        await oidc.AddMemberAsync(id, bob.Id, "viewer");
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();

        // The deletion commits after the change's reads and before its memberships are cleaned up.
        var change = await pause.HoldNextAsync(() => oidc.FailAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{bob.Id}/role", aliceToken, new { role = "member" }));
        var organization = (await store.GetAsync(AuthStore.OrganizationCollection, "terrascale", id))!;
        await Assert.That(await store.TransactAsync([AuthStore.Delete(AuthStore.OrganizationCollection, "terrascale", id, organization.Version)])).IsNull();
        await Assert.That(await pause.ReleaseAsync(change)).IsEqualTo((404, "not_found"));
        await Assert.That((await store.GetAsync(AuthStore.MembershipCollection, "terrascale", $"{id}/{bob.Id}"))!.Document.GetProperty("role").GetString()).IsEqualTo("viewer");
    }

    [Test]
    public async Task Every_grant_rewrites_the_membership_that_a_removal_deletes()
    {
        // A removal deletes the membership at the version it read with the grants it found; a grant made after that
        // read, even on another project of a collaborator, rewrites the membership, so the removal fails and retries.
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (dave, daveToken) = await oidc.SignInUserAsync("dave@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        var first = await oidc.CreateProjectAsync(aliceToken, id, "Production");
        var second = await oidc.CreateProjectAsync(aliceToken, id, "Staging");
        await oidc.GrantAsync(first, dave.Id, "viewer");
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var before = (await store.GetAsync(AuthStore.MembershipCollection, "terrascale", $"{id}/{dave.Id}"))!.Version;

        await oidc.GrantAsync(second, dave.Id, "member");
        var after = (await store.GetAsync(AuthStore.MembershipCollection, "terrascale", $"{id}/{dave.Id}"))!.Version;
        await Assert.That(after).IsNotEqualTo(before);

        // Removed, the collaborator keeps nothing in the organization.
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/members/{dave.Id}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}", daveToken)).IsEqualTo((404, "not_found"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{second}", daveToken)).IsEqualTo((404, "not_found"));
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, Organizations, daveToken)).GetProperty("items").GetArrayLength()).IsEqualTo(0);

        // Removing a collaborator's last project role removes the collaborator.
        await oidc.GrantAsync(first, dave.Id, "viewer");
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{first}/members/{dave.Id}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await store.GetAsync(AuthStore.MembershipCollection, "terrascale", $"{id}/{dave.Id}")).IsNull();

        // So does deleting a collaborator's last project, and only the last one.
        await oidc.GrantAsync(first, dave.Id, "viewer");
        await oidc.GrantAsync(second, dave.Id, "viewer");
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{first}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await store.GetAsync(AuthStore.ProjectMemberCollection, "terrascale", $"{first}/{dave.Id}")).IsNull();
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, Organizations, daveToken)).GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{second}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await store.GetAsync(AuthStore.MembershipCollection, "terrascale", $"{id}/{dave.Id}")).IsNull();
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, Organizations, daveToken)).GetProperty("items").GetArrayLength()).IsEqualTo(0);

        // A role left on a deleted project (its clean-up failed) does not keep a collaborator whose last live role goes.
        var third = await oidc.CreateProjectAsync(aliceToken, id, "Preview");
        var fourth = await oidc.CreateProjectAsync(aliceToken, id, "Demo");
        await oidc.GrantAsync(third, dave.Id, "viewer");
        await oidc.GrantAsync(fourth, dave.Id, "viewer");
        var leftover = (await store.GetAsync(AuthStore.ProjectMemberCollection, "terrascale", $"{third}/{dave.Id}"))!.Document;
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{third}", aliceToken, status: HttpStatusCode.NoContent);
        // The project's deletion job fences the store, so the store refuses to bring the role back; a clean-up that failed
        // before the deletion leaves it behind in storage itself.
        await Assert.That(await store.TransactAsync([AuthStore.Create(AuthStore.ProjectMemberCollection, "terrascale", $"{third}/{dave.Id}", leftover)])).IsEqualTo(0);
        await oidc.Server.App.Services.GetRequiredService<TerraBaseDatabase>().PutDocumentAsync(AuthStore.ProjectMemberCollection,
            AuthStore.NaturalKeyId("terrascale", AuthStore.ProjectMemberCollection, $"{third}/{dave.Id}"), leftover, TerraBasePrecondition.Absent);
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{fourth}/members/{dave.Id}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await store.GetAsync(AuthStore.MembershipCollection, "terrascale", $"{id}/{dave.Id}")).IsNull();
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, Organizations, daveToken)).GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    private static async Task<(int Status, string Body)> OnReplicaAsync(OidcHarness oidc, AuthTestServer replica, HttpMethod method, string path, string token, object? body = null)
    {
        using var response = await oidc.CallAsync(method, path, token, body, client: replica.Client);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

}
