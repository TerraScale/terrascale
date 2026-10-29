using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Identity;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class SessionTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Only_the_token_digest_is_stored_and_the_session_resolves_only_in_its_instance()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var (users, sessions) = this.Stack(tenant, clock);
        var user = (await users.CreateAsync("instance-a", "alice@example.test"))!;

        var issued = await sessions.CreateAsync(user);
        await Assert.That(issued.Token.Length).IsEqualTo(48);
        await Assert.That(issued.Token.All(char.IsAsciiLetterOrDigit)).IsTrue();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(issued.Token)));
        await Assert.That(issued.Session.TokenHash).IsEqualTo(digest);
        await Assert.That(issued.Session.ExpiresAt).IsEqualTo(clock.UtcNow + Sessions.Lifetime);
        var stored = (await database.ListDocumentsAsync("sessions")).Items.Single().Document;
        await Assert.That(stored.GetProperty("tokenHash").GetString()).IsEqualTo(digest);
        await Assert.That(stored.GetRawText()).DoesNotContain(issued.Token);

        var (_, independent) = this.Stack(tenant, clock);
        var live = await independent.ResolveAsync("instance-a", issued.Token);
        await Assert.That(live!.Value.User.Id).IsEqualTo(user.Id);
        await Assert.That(live.Value.Session.UserId).IsEqualTo(user.Id);
        await Assert.That(await independent.ResolveAsync("instance-b", issued.Token)).IsNull();
        await Assert.That(await independent.ResolveAsync("instance-a", digest)).IsNull();
    }



    [Test]
    [Arguments(0, false)]
    public async Task Sessions_expire_at_the_exact_absolute_boundary(int secondsFromExpiry, bool live)
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var (users, sessions) = this.Stack(tenant, clock);
        var issued = await sessions.CreateAsync((await users.CreateAsync("instance", "alice@example.test"))!);
        clock.UtcNow = issued.Session.ExpiresAt.AddSeconds(secondsFromExpiry);

        await Assert.That((await sessions.ResolveAsync("instance", issued.Token)).HasValue).IsEqualTo(live);
        await Assert.That(await sessions.RotateAsync("instance", issued.Token) is not null).IsEqualTo(live);
    }


    [Test]
    public async Task Rotation_kills_the_old_token_and_keeps_the_absolute_expiry()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var (users, sessions) = this.Stack(tenant, clock);
        var issued = await sessions.CreateAsync((await users.CreateAsync("instance", "alice@example.test"))!);
        clock.UtcNow += TimeSpan.FromDays(3);

        var rotated = await sessions.RotateAsync("instance", issued.Token);
        await Assert.That(rotated).IsNotNull();
        await Assert.That(rotated!.Token).IsNotEqualTo(issued.Token);
        await Assert.That(rotated.Session.ExpiresAt).IsEqualTo(issued.Session.ExpiresAt);
        await Assert.That(rotated.Session.UserId).IsEqualTo(issued.Session.UserId);
        await Assert.That(await sessions.ResolveAsync("instance", issued.Token)).IsNull();
        await Assert.That(await sessions.RotateAsync("instance", issued.Token)).IsNull();
        await Assert.That(await sessions.ResolveAsync("instance", rotated.Token)).IsNotNull();
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count(item => item.Document.GetProperty("revoked").GetBoolean())).IsEqualTo(1);

        await Assert.That(await sessions.RevokeAsync("instance", rotated.Token)).IsTrue();
        await Assert.That(await sessions.RotateAsync("instance", rotated.Token)).IsNull();
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(2);
    }


    [Test]
    public async Task Revocation_is_idempotent_and_scoped_to_the_token_and_instance()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var (users, sessions) = this.Stack(tenant, TimeProvider.System);
        var user = (await users.CreateAsync("instance", "alice@example.test"))!;
        var kept = await sessions.CreateAsync(user);
        var revoked = await sessions.CreateAsync(user);

        await Assert.That(await sessions.RevokeAsync("instance", "unknown-token")).IsFalse();
        await Assert.That(await sessions.RevokeAsync("instance", null)).IsFalse();
        await Assert.That(await sessions.RevokeAsync("other-instance", revoked.Token)).IsFalse();
        await Assert.That(await sessions.ResolveAsync("instance", revoked.Token)).IsNotNull();

        var (_, independent) = this.Stack(tenant, TimeProvider.System);
        await Assert.That(await independent.RevokeAsync("instance", revoked.Token)).IsTrue();
        await Assert.That(await sessions.RevokeAsync("instance", revoked.Token)).IsTrue();
        await Assert.That(await sessions.ResolveAsync("instance", revoked.Token)).IsNull();
        await Assert.That(await sessions.ResolveAsync("instance", kept.Token)).IsNotNull();
    }

    [Test]
    public async Task Revoking_all_kills_every_existing_session_of_the_user_only()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var (users, sessions) = this.Stack(tenant, TimeProvider.System);
        var alice = (await users.CreateAsync("instance", "alice@example.test"))!;
        var bob = (await users.CreateAsync("instance", "bob@example.test"))!;
        var first = await sessions.CreateAsync(alice);
        var second = await sessions.CreateAsync(alice);
        var bystander = await sessions.CreateAsync(bob);

        await Assert.That(await sessions.RevokeAllAsync("instance", "missing-user")).IsFalse();
        await Assert.That(await sessions.RevokeAllAsync("other-instance", alice.Id)).IsFalse();
        await Assert.That(await sessions.ResolveAsync("instance", first.Token)).IsNotNull();

        var (_, independent) = this.Stack(tenant, TimeProvider.System);
        await Assert.That(await independent.RevokeAllAsync("instance", alice.Id)).IsTrue();
        await Assert.That(await sessions.ResolveAsync("instance", first.Token)).IsNull();
        await Assert.That(await sessions.ResolveAsync("instance", second.Token)).IsNull();
        await Assert.That(await sessions.RotateAsync("instance", second.Token)).IsNull();
        await Assert.That(await sessions.ResolveAsync("instance", bystander.Token)).IsNotNull();
        var current = (await users.GetAsync("instance", alice.Id))!;
        await Assert.That(current.AuthEpoch).IsEqualTo(1);
        var stale = await Assert.ThrowsAsync<ApiException>(() => sessions.CreateAsync(alice));
        await Assert.That(stale!.Status).IsEqualTo(401);

        var fresh = await sessions.CreateAsync(current);
        await Assert.That((await independent.ResolveAsync("instance", fresh.Token))!.Value.User.AuthEpoch).IsEqualTo(1);
    }










    private (Users Users, Sessions Sessions) Stack(TerraBaseTestTenant tenant, TimeProvider clock)
    {
        var store = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), clock);
        var users = new Users(store, clock);
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var totp = new TotpCredentials(store, users, new AttemptLimiter(new TerraBaseDocumentStore(store), clock, options), options, clock);
        return (users, new Sessions(store, users, totp, clock));
    }
}
