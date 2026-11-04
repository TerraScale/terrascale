using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Slow.Management;

/// <summary>Real hosts and SDK persistence across failed cleanup, shutdown and controlled overdue recovery.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class DeletionCleanupTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Account_cleanup_survives_restart_and_an_overdue_failure_without_touching_reused_email_or_another_tenant()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        var faults = new CleanupFaults(AuthStore.SessionCollection) { InnerHandler = TerraBaseFixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(new HttpClient(faults)));
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var (alice, session, tokens) = await oidc.SignInAndRedeemAsync("deleted@example.test");
        var token = tokens.GetProperty("access_token").GetString()!;
        var tokenIds = await store.QueryAsync(AuthStore.OidcTokenCollection, alice.InstanceId, "/subject", alice.Id)
            .Select(document => document.Document.GetProperty("id").GetString()!).ToListAsync();
        var hash = new string('d', 64);
        await store.CreateChallengeAsync(alice.InstanceId, "recovery", hash, alice.Id, clock.UtcNow.AddHours(1));
        await store.ClaimUniqueAsync(alice.InstanceId, "synthetic-owned", "old", alice.Id);
        await oidc.Server.App.Services.GetRequiredService<Passcodes>().RequestAsync(alice.InstanceId, alice.Email);
        await store.CreateAsync(AuthStore.MfaCollection, alice.InstanceId, alice.Id,
            TerraBaseFixture.Json($$"""{"instanceId":"{{alice.InstanceId}}","userId":"{{alice.Id}}","secretBox":null,"enabled":false,"enrollmentEpoch":0,"enrollmentExpiresAt":null,"lastAcceptedStep":-1,"recoveryHashes":[],"recoveryExpiresAt":null}"""));

        // Same subject id in another tenant must remain outside the job's authority.
        var unrelated = TerraBaseFixture.Json($$"""{"instanceId":"other-tenant","userId":"{{alice.Id}}","tokenHash":"{{hash}}"}""");
        var other = await store.CreateAsync(AuthStore.SessionCollection, "other-tenant", hash, unrelated);
        var requested = clock.UtcNow;
        faults.Fail = true;
        await oidc.OkAsync(HttpMethod.Delete, "/v1/terrascale/account", token, status: HttpStatusCode.NoContent);
        await Assert.That(await users.GetAsync(alice.InstanceId, alice.Id)).IsNull();
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, token)).IsEqualTo((401, "unauthorized"));
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<Sessions>().ResolveAsync(alice.InstanceId, session)).IsNull();
        using (var refreshed = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!))
        {
            await Assert.That(refreshed.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
        }

        var cleanup = oidc.Server.App.Services.GetRequiredService<DeletionCleanup>();
        await cleanup.RunOnceAsync();
        var pending = await JobAsync(store, alice.InstanceId, "account", alice.Id);
        await Assert.That(pending.FailureCode).IsEqualTo("storage_unavailable");
        await Assert.That(pending.AuthLogicalVerifiedAt).IsNull();
        await Assert.That(pending.RequestedAt).IsEqualTo(requested);
        await Assert.That(pending.DueAt).IsEqualTo(requested.AddDays(30));

        // The old job must never look up this address again.
        await oidc.Server.App.Services.GetRequiredService<Passcodes>().RequestSignUpAsync(alice.InstanceId, alice.Email, "synthetic-hash", "synthetic-pending-token");
        var signup = await store.GetAsync(AuthStore.PasscodeCollection, alice.InstanceId, "sign-up:" + alice.Email);
        await Assert.That(signup).IsNotNull();
        var replacement = await users.CreateAsync(alice.InstanceId, alice.Email);
        await Assert.That(replacement!.Id).IsNotEqualTo(alice.Id);

        await oidc.Server.App.StopAsync();
        clock.UtcNow = requested.AddDays(31);
        var restartFaults = new CleanupFaults(AuthStore.SessionCollection) { InnerHandler = TerraBaseFixture.NewHandler(), Fail = true };
        await using var restarted = await oidc.StartReplicaAsync(clock: clock, services: services => services.AddSingleton(new HttpClient(restartFaults)));
        var restartedStore = restarted.App.Services.GetRequiredService<AuthStore>();
        await Assert.That(await ReadinessWait.UntilAsync(async () => (await JobAsync(restartedStore, alice.InstanceId, "account", alice.Id)).Attempts > pending.Attempts, TimeSpan.FromSeconds(10))).IsTrue();
        var overdue = await JobAsync(restartedStore, alice.InstanceId, "account", alice.Id);
        await Assert.That(overdue.AuthLogicalVerifiedAt).IsNull();
        await Assert.That(overdue.DueAt).IsEqualTo(pending.DueAt);

        restartFaults.Fail = false;
        // Failure retries use persisted exponential backoff and jitter; two minutes need not make this job due.
        clock.UtcNow = overdue.NextAttemptAt;
        await restarted.App.Services.GetRequiredService<DeletionCleanup>().RunOnceAsync();
        var finished = await JobAsync(restartedStore, alice.InstanceId, "account", alice.Id);
        await Assert.That(finished.AuthLogicalVerifiedAt).IsEqualTo(clock.UtcNow);
        await Assert.That(finished.FailureCode).IsNull();
        await Assert.That(finished.DueAt).IsEqualTo(pending.DueAt);
        foreach (var (collection, field) in new[]
        {
            (AuthStore.SessionCollection, "/userId"), (AuthStore.PasscodeCollection, "/userId"), (AuthStore.MfaCollection, "/userId"),
            (AuthStore.OidcTokenCollection, "/subject"), (AuthStore.OidcAuthorizationCollection, "/subject"),
            (AuthStore.UniqueCollection, "/ownerId"), (AuthStore.ChallengeCollection, "/subjectId"),
        })
        {
            await Assert.That(await restartedStore.QueryAsync(collection, alice.InstanceId, field, alice.Id).AnyAsync()).IsFalse();
        }

        await Assert.That((await restartedStore.GetAsync(AuthStore.SessionCollection, "other-tenant", hash))!.Version).IsEqualTo(other.Version);
        await Assert.That((await restartedStore.GetAsync(AuthStore.PasscodeCollection, alice.InstanceId, "sign-up:" + alice.Email))!.Version).IsEqualTo(signup!.Version);
        await Assert.That(await restarted.App.Services.GetRequiredService<Users>().GetAsync(alice.InstanceId, replacement.Id)).IsNotNull();
        foreach (var tokenId in tokenIds)
        {
            await Assert.That(await restartedStore.QueryAsync(AuthStore.UniqueCollection, alice.InstanceId, "/ownerId", tokenId).AnyAsync()).IsFalse();
        }

        await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(() => restartedStore.CreateAsync(AuthStore.SessionCollection, alice.InstanceId, hash,
            TerraBaseFixture.Json($$"""{"instanceId":"{{alice.InstanceId}}","userId":"{{alice.Id}}","tokenHash":"{{hash}}"}""")));
    }

    private static async Task<DeletionJob> JobAsync(AuthStore store, string instanceId, string scope, string subjectId) =>
        (await store.GetAsync(AuthStore.DeletionJobCollection, instanceId, DeletionJob.Key(instanceId, scope, subjectId)))!.Document.Deserialize(DeletionJson.Default.DeletionJob)!;

    private sealed class CleanupFaults(params string[] collections) : DelegatingHandler
    {
        private int fail;

        public bool Fail
        {
            get => Volatile.Read(ref this.fail) != 0;
            set => Volatile.Write(ref this.fail, value ? 1 : 0);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (this.Fail && collections.Any(collection => request.RequestUri!.AbsolutePath.EndsWith("/collections/" + collection + "/query", StringComparison.Ordinal)))
            {
                throw new HttpRequestException("Synthetic cleanup outage");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
