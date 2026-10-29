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

namespace TerraScale.Authentication.Slow.Identity;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class SessionTests(TerraBaseFixture fixture)
{

    [Test]
    public async Task Rotation_at_the_lineage_budget_requires_reauthentication_without_mutating_the_live_session()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var (users, sessions) = this.Stack(tenant, TimeProvider.System);
        var user = (await users.CreateAsync("instance", "alice@example.test"))!;
        var current = await sessions.CreateAsync(user);
        while (current.Session.Bindings.Count < AuthStore.MaxCredentialBindings)
        {
            current = (await sessions.RotateAsync("instance", current.Token))!;
        }

        var before = (await database.ListDocumentsAsync(AuthStore.SessionCollection, limit: 1000)).Items.Select(item => item.Version).Order(StringComparer.Ordinal).ToArray();
        ApiException? rejected = null;
        try
        {
            await sessions.RotateAsync("instance", current.Token);
        }
        catch (ApiException error)
        {
            rejected = error;
        }

        await Assert.That(rejected?.Status).IsEqualTo(401);
        await Assert.That(rejected?.Code).IsEqualTo("reauthentication_required");
        await Assert.That(await sessions.ResolveAsync("instance", current.Token)).IsNotNull();
        var after = (await database.ListDocumentsAsync(AuthStore.SessionCollection, limit: 1000)).Items;
        await Assert.That(after.Select(item => item.Version).Order(StringComparer.Ordinal).SequenceEqual(before)).IsTrue();
        await Assert.That(after.All(item => item.Document.GetProperty("bindings").GetArrayLength() <= AuthStore.MaxCredentialBindings)).IsTrue();
        var fresh = await sessions.CreateAsync((await users.GetAsync("instance", user.Id))!);
        await Assert.That(fresh.Session.Bindings.Count).IsEqualTo(1);
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
    public async Task Active_factors_refuse_primary_issuance_and_current_epoch_weak_sessions()
    {
        var clock = new StoreTestClock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var caller = await oidc.CreateUserAsync();
        var pending = (await mfa.BeginAsync(caller))!;
        var active = (await mfa.ConfirmAsync(caller, Code(pending, clock)))!;

        var refusal = await Assert.ThrowsAsync<ApiException>(() => sessions.CreateAsync(active.User));
        await Assert.That(refusal!.Status).IsEqualTo(401);
        await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.SessionCollection)).Items.Count).IsEqualTo(0);

        // A stored primary-only session at the current epoch must still be rejected for an active factor.
        var token = Tokens.RandomToken(48);
        var weak = new SessionRecord(caller.InstanceId, caller.Id, Tokens.Sha256Hex(token), active.User.AuthEpoch, clock.UtcNow, clock.UtcNow + Sessions.Lifetime);
        await store.CreateAsync(AuthStore.SessionCollection, caller.InstanceId, weak.TokenHash,
            JsonSerializer.SerializeToElement(weak, IdentityJson.Default.SessionRecord));
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var independent = replica.App.Services.GetRequiredService<Sessions>();
        await Assert.That(await independent.ResolveAsync(caller.InstanceId, token)).IsNull();
        await Assert.That(await independent.RotateAsync(caller.InstanceId, token)).IsNull();
        await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.SessionCollection)).Items.Count).IsEqualTo(1);
        await Assert.That(await independent.RevokeAsync(caller.InstanceId, token)).IsTrue();
    }

    [Test]
    public async Task Pending_and_disabled_factors_accept_primary_sessions_at_the_current_epoch()
    {
        var clock = new StoreTestClock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var caller = await oidc.CreateUserAsync();
        var pending = (await mfa.BeginAsync(caller))!;
        var issued = await sessions.CreateAsync(caller);
        await Assert.That(await sessions.ResolveAsync(caller.InstanceId, issued.Token)).IsNotNull();
        var rotated = (await sessions.RotateAsync(caller.InstanceId, issued.Token))!;
        await Assert.That(await sessions.ResolveAsync(caller.InstanceId, rotated.Token)).IsNotNull();

        var active = (await mfa.ConfirmAsync(caller, Code(pending, clock)))!;
        await Assert.That(await sessions.ResolveAsync(caller.InstanceId, rotated.Token)).IsNull();
        await Assert.That(await mfa.DisableAsync(active.User, active.Codes[0])).IsTrue();
        var disabled = (await users.GetAsync(caller.InstanceId, caller.Id))!;
        issued = await sessions.CreateAsync(disabled);
        await Assert.That(await sessions.ResolveAsync(caller.InstanceId, issued.Token)).IsNotNull();
        await Assert.That(await sessions.RotateAsync(caller.InstanceId, issued.Token)).IsNotNull();
    }

    [Test]
    public async Task Corrupt_factor_state_never_issues_resolves_or_rotates_primary_sessions()
    {
        var clock = new StoreTestClock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var caller = await oidc.CreateUserAsync();
        var issued = await sessions.CreateAsync(caller);
        await store.CreateAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id, TerraBaseFixture.Json("{\"enabled\":false}"));

        var issuance = await Assert.ThrowsAsync<ApiException>(() => sessions.CreateAsync(caller));
        var resolution = await Assert.ThrowsAsync<ApiException>(() => sessions.ResolveAsync(caller.InstanceId, issued.Token));
        var rotation = await Assert.ThrowsAsync<ApiException>(() => sessions.RotateAsync(caller.InstanceId, issued.Token));
        await Assert.That(issuance!.Status).IsEqualTo(503);
        await Assert.That(resolution!.Status).IsEqualTo(503);
        await Assert.That(rotation!.Status).IsEqualTo(503);
        await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.SessionCollection)).Items.Single().Document.GetProperty("revoked").GetBoolean()).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Revocation_during_factor_lookup_invalidates_resolution_and_rotation(bool rotate)
    {
        var clock = new StoreTestClock();
        var pause = new PausingTransactions(request => request.Method == HttpMethod.Get
            && request.RequestUri!.AbsolutePath.Contains($"/collections/{AuthStore.MfaCollection}/documents/", StringComparison.Ordinal))
        { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(pause);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(http));
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var independent = replica.App.Services.GetRequiredService<Sessions>();
        var caller = await oidc.CreateUserAsync();
        var issued = await sessions.CreateAsync(caller);
        var resolving = await pause.HoldNextAsync(async () => rotate
            ? await sessions.RotateAsync(caller.InstanceId, issued.Token) is not null
            : (await sessions.ResolveAsync(caller.InstanceId, issued.Token)).HasValue);
        bool accepted;
        try
        {
            await Assert.That(await independent.RevokeAllAsync(caller.InstanceId, caller.Id)).IsTrue();
        }
        finally
        {
            accepted = await pause.ReleaseAsync(resolving);
        }

        await Assert.That(accepted).IsFalse();
        await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.SessionCollection)).Items.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("activate", false)]
    [Arguments("activate", true)]
    [Arguments("reset", false)]
    [Arguments("reset", true)]
    [Arguments("revoke", false)]
    [Arguments("revoke", true)]
    public async Task An_epoch_change_before_issuance_commit_prevents_creation_and_rotation(string change, bool rotate)
    {
        var clock = new StoreTestClock();
        var pause = new PausingTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(pause);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(http));
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var mfa = replica.App.Services.GetRequiredService<TotpCredentials>();
        var users = replica.App.Services.GetRequiredService<Users>();
        var independent = replica.App.Services.GetRequiredService<Sessions>();
        var caller = await oidc.CreateUserAsync();
        var pending = change == "activate" ? (await mfa.BeginAsync(caller))! : null;
        var original = rotate ? await sessions.CreateAsync(caller) : null;
        var issuing = await pause.HoldNextAsync(async () =>
        {
            if (rotate)
            {
                return await sessions.RotateAsync(caller.InstanceId, original!.Token);
            }

            try
            {
                return await sessions.CreateAsync(caller);
            }
            catch (ApiException error) when (error.Status == 401 && error.Code == "invalid_credentials")
            {
                return null;
            }
        });
        IssuedSession? result;
        try
        {
            switch (change)
            {
                case "activate":
                    await Assert.That(await mfa.ConfirmAsync(caller, Code(pending!, clock))).IsNotNull();
                    break;
                case "reset":
                    await Assert.That(await users.SetPasswordAsync(caller, "synthetic-password-hash")).IsTrue();
                    break;
                case "revoke":
                    await Assert.That(await independent.RevokeAllAsync(caller.InstanceId, caller.Id)).IsTrue();
                    break;
            }
        }
        finally
        {
            result = await pause.ReleaseAsync(issuing);
        }

        await Assert.That(result).IsNull();
        await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.SessionCollection)).Items.Count).IsEqualTo(rotate ? 1 : 0);
        if (original is not null)
        {
            await Assert.That(await sessions.ResolveAsync(caller.InstanceId, original.Token)).IsNull();
        }
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

    private static string Code(TotpEnrollment enrollment, StoreTestClock clock) =>
        new Totp(Base32Encoding.ToBytes(enrollment.Secret)).ComputeTotp(clock.UtcNow.UtcDateTime);

    private sealed class ExpiringClock(DateTimeOffset live, DateTimeOffset expired, int liveObservations) : TimeProvider
    {
        private int observations;

        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref this.observations) <= liveObservations ? live : expired;
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
