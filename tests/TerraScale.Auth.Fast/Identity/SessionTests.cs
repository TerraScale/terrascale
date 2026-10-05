using System.Security.Cryptography;
using System.Text;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;
using TerraScale.DotNet.Testing;

namespace TerraScale.Auth.Fast.Identity;

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
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not-a-session-token")]
    [Arguments("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Unknown_tokens_do_not_resolve(string? token)
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var (users, sessions) = this.Stack(tenant, TimeProvider.System);
        var issued = await sessions.CreateAsync((await users.CreateAsync("instance", "alice@example.test"))!);

        await Assert.That(await sessions.ResolveAsync("instance", token)).IsNull();
        await Assert.That(await sessions.ResolveAsync("instance", issued.Token + new string('a', 209))).IsNull();
        await Assert.That(await sessions.ResolveAsync("instance", issued.Token[..47] + (issued.Token[47] == 'a' ? 'b' : 'a'))).IsNull();
        await Assert.That(await sessions.ResolveAsync("instance", issued.Token)).IsNotNull();
    }

    [Test]
    [Arguments(-1, true)]
    [Arguments(0, false)]
    [Arguments(1, false)]
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
    public async Task A_resolution_or_rotation_that_completes_after_expiry_grants_nothing()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var (users, sessions) = this.Stack(tenant, clock);
        var issued = await sessions.CreateAsync((await users.CreateAsync("instance", "alice@example.test"))!);
        var expires = issued.Session.ExpiresAt;

        // The first check sees a live session; the user lookup completes at the expiry instant.
        var (_, resolving) = this.Stack(tenant, new ExpiringClock(expires.AddSeconds(-1), expires, liveObservations: 1));
        await Assert.That(await resolving.ResolveAsync("instance", issued.Token)).IsNull();

        // Both checks pass, then revocation and the successor write complete at the expiry instant.
        var (_, rotating) = this.Stack(tenant, new ExpiringClock(expires.AddSeconds(-1), expires, liveObservations: 2));
        await Assert.That(await rotating.RotateAsync("instance", issued.Token)).IsNull();
        await Assert.That(await sessions.ResolveAsync("instance", issued.Token)).IsNull();
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(2);
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
    public async Task Concurrent_rotations_from_independent_stores_produce_exactly_one_successor()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var (users, sessions) = this.Stack(tenant, TimeProvider.System);
        var issued = await sessions.CreateAsync((await users.CreateAsync("instance", "alice@example.test"))!);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            var (_, independent) = this.Stack(tenant, TimeProvider.System);
            await start.Task;
            return await independent.RotateAsync("instance", issued.Token);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);

        await Assert.That(results.Count(result => result is not null)).IsEqualTo(1);
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(2);
        await Assert.That(await sessions.ResolveAsync("instance", issued.Token)).IsNull();
        await Assert.That(await sessions.ResolveAsync("instance", results.Single(result => result is not null)!.Token)).IsNotNull();
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

        var fresh = await sessions.CreateAsync(current);
        await Assert.That((await independent.ResolveAsync("instance", fresh.Token))!.Value.User.AuthEpoch).IsEqualTo(1);
    }

    [Test]
    public async Task Pruning_deletes_only_revoked_and_expired_sessions_across_instances()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var (users, sessions) = this.Stack(tenant, clock);
        var alice = (await users.CreateAsync("instance-a", "alice@example.test"))!;
        var bob = (await users.CreateAsync("instance-b", "bob@example.test"))!;
        var expiredA = await sessions.CreateAsync(alice);
        await sessions.CreateAsync(bob);
        clock.UtcNow += TimeSpan.FromDays(7);
        var signedOut = await sessions.CreateAsync(bob);
        await sessions.RevokeAsync("instance-b", signedOut.Token);
        var successor = (await sessions.RotateAsync("instance-a", (await sessions.CreateAsync(alice)).Token))!;
        // Both first sessions reach their expiry instant; the rest are revoked or live for another week.
        clock.UtcNow = expiredA.Session.ExpiresAt;

        // Batches of one: the call keeps draining past full batches until the backlog is gone.
        var (_, independent) = this.Stack(tenant, clock);
        await Assert.That(await independent.PruneAsync(batchSize: 1)).IsEqualTo(4);
        await Assert.That(await independent.PruneAsync(batchSize: 100)).IsEqualTo(0);
        var remaining = (await database.ListDocumentsAsync("sessions")).Items.Single().Document;
        await Assert.That(remaining.GetProperty("tokenHash").GetString()).IsEqualTo(successor.Session.TokenHash);
        await Assert.That(await sessions.ResolveAsync("instance-a", successor.Token)).IsNotNull();
        await Assert.That(await sessions.RevokeAsync("instance-b", signedOut.Token)).IsFalse();
    }

    [Test]
    public async Task Competing_pruners_drain_a_multi_batch_backlog_and_keep_live_sessions()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var (users, sessions) = this.Stack(tenant, clock);
        var user = (await users.CreateAsync("instance", "alice@example.test"))!;
        for (var index = 0; index < 30; index++)
        {
            await sessions.RevokeAsync("instance", (await sessions.CreateAsync(user)).Token);
        }

        var live = await sessions.CreateAsync(user);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pruners = Enumerable.Range(0, 3).Select(async _ =>
        {
            var (_, replica) = this.Stack(tenant, clock);
            await start.Task;
            return await replica.PruneAsync(batchSize: 4);
        }).ToArray();
        start.SetResult();

        await Assert.That((await Task.WhenAll(pruners)).Sum()).IsEqualTo(30);
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Single().Document.GetProperty("tokenHash").GetString()).IsEqualTo(live.Session.TokenHash);
        await Assert.That(await sessions.ResolveAsync("instance", live.Token)).IsNotNull();
    }

    private sealed class ExpiringClock(DateTimeOffset live, DateTimeOffset expired, int liveObservations) : TimeProvider
    {
        private int observations;

        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref this.observations) <= liveObservations ? live : expired;
    }

    private (Users Users, Sessions Sessions) Stack(TerraBaseTestTenant tenant, TimeProvider clock)
    {
        var store = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), clock);
        var users = new Users(store, clock);
        return (users, new Sessions(store, users, clock));
    }
}
