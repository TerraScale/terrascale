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

namespace TerraScale.Authentication.Fast.Management;

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

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Organization_and_project_jobs_remove_all_invitation_statuses_claims_and_residual_grants(bool organization)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        var faults = new CleanupFaults(AuthStore.ProjectMemberCollection, AuthStore.MembershipCollection, AuthStore.ApiKeyCollection, AuthStore.ApiKeyUsageCollection,
            AuthStore.InvitationCollection, AuthStore.AuditEventCollection)
        {
            InnerHandler = TerraBaseFixture.NewHandler(),
        };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(new HttpClient(faults)));
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var (alice, token) = await oidc.SignInUserAsync("owner@example.test");
        var (bob, _) = await oidc.SignInUserAsync("collaborator@example.test");
        var org = await oidc.CreateOrganizationAsync(token);
        var project = organization ? AuthStore.NewId() : await oidc.CreateProjectAsync(token, org, "Cleanup");
        if (organization)
        {
            // A residual grant from a previously deleted project must also be retired with its organization.
            await store.CreateAsync(AuthStore.ProjectMemberCollection, "terrascale", project + "/" + bob.Id,
                TerraBaseFixture.Json($$"""{"instanceId":"terrascale","organizationId":"{{org}}","projectId":"{{project}}","userId":"{{bob.Id}}","role":"viewer"}"""));
        }
        else
        {
            await Assert.That(await oidc.GrantAsync(project, bob.Id, "viewer")).IsTrue();
        }

        var scope = organization ? "organization" : "project";
        var subject = organization ? org : project;
        var ids = new List<string>();
        foreach (var status in new[] { "pending", "accepted", "declined", "revoked", "expired" })
        {
            var id = AuthStore.NewId();
            ids.Add(id);
            var invitation = TerraBaseFixture.Json($$"""{"id":"{{id}}","instanceId":"terrascale","organizationId":"{{org}}","projectId":{{(organization ? "null" : "\"" + project + "\"")}},"email":"{{status}}@example.test","role":"viewer","createdBy":"{{alice.Id}}","createdAt":"{{clock.UtcNow:O}}","expiresAt":"{{clock.UtcNow.AddDays(7):O}}","status":"{{status}}","tokenHash":"{{id}}"}""");
            await Assert.That(await store.CreateClaimedAsync(AuthStore.InvitationCollection, "terrascale", id, invitation, "invitation-token", id)).IsTrue();
            await Assert.That(await store.ClaimUniqueAsync("terrascale", "invitation-pending", org + "/" + (organization ? "-" : project) + "/" + status + "@example.test", id)).IsTrue();
        }

        var key = AuthStore.NewId();
        await store.CreateAsync(AuthStore.ApiKeyCollection, "terrascale", key,
            TerraBaseFixture.Json($$"""{"id":"{{key}}","instanceId":"terrascale","organizationId":"{{org}}","projectId":"{{project}}"}"""));
        var usage = TerraBaseFixture.Json($$"""{"keyId":"{{key}}","instanceId":"terrascale","organizationId":"{{org}}","projectId":"{{project}}","lastUsedAt":"{{clock.UtcNow:O}}"}""");
        await store.CreateAsync(AuthStore.ApiKeyUsageCollection, "terrascale", key, usage);
        faults.Fail = true;
        await oidc.OkAsync(HttpMethod.Delete, organization ? $"{Organizations}/{org}" : $"{Organizations}/{org}/projects/{project}", token, status: HttpStatusCode.NoContent);
        await oidc.Server.App.Services.GetRequiredService<DeletionCleanup>().RunOnceAsync();
        await Assert.That((await JobAsync(store, "terrascale", scope, subject)).AuthLogicalVerifiedAt).IsNull();
        faults.Fail = false;
        clock.UtcNow += TimeSpan.FromMinutes(2);
        await oidc.Server.App.Services.GetRequiredService<DeletionCleanup>().RunOnceAsync();
        await Assert.That((await JobAsync(store, "terrascale", scope, subject)).AuthLogicalVerifiedAt).IsNotNull();
        await Assert.That(await store.GetAsync(AuthStore.ApiKeyCollection, "terrascale", key)).IsNull();
        await Assert.That(await store.GetAsync(AuthStore.ApiKeyUsageCollection, "terrascale", key)).IsNull();
        await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(() => store.CreateAsync(AuthStore.ApiKeyUsageCollection, "terrascale", key, usage));
        foreach (var id in ids)
        {
            await Assert.That(await store.GetAsync(AuthStore.InvitationCollection, "terrascale", id)).IsNull();
            await Assert.That(await store.QueryAsync(AuthStore.UniqueCollection, "terrascale", "/ownerId", id).AnyAsync()).IsFalse();
        }

        await Assert.That(await store.QueryAsync(AuthStore.ProjectMemberCollection, "terrascale", "/projectId", project).AnyAsync()).IsFalse();
        if (!organization)
        {
            await Assert.That(await store.GetAsync(AuthStore.MembershipCollection, "terrascale", org + "/" + bob.Id)).IsNull();
            await Assert.That(await store.GetAsync(AuthStore.OrganizationCollection, "terrascale", org)).IsNotNull();
        }

        await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(() => store.CreateAsync(AuthStore.InvitationCollection, "terrascale", AuthStore.NewId(),
            TerraBaseFixture.Json($$"""{"instanceId":"terrascale","organizationId":"{{org}}","projectId":"{{project}}"}""")));
    }

    [Test]
    public async Task An_unfinished_owner_handoff_neither_deletes_the_membership_nor_starves_another_job()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var (owner, token) = await oidc.SignInUserAsync("handoff@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        // Simulate a residual owner after an interrupted broader orchestration; never erase it blindly.
        await users.DeleteAsync(owner.InstanceId, owner.Id);
        var independent = (await users.CreateAsync("terrascale", "independent@example.test"))!;
        await users.DeleteAsync(independent.InstanceId, independent.Id);
        clock.Advance(TimeSpan.FromMinutes(2));
        await Assert.That(await ReadinessWait.UntilAsync(async () =>
            (await JobAsync(store, independent.InstanceId, "account", independent.Id)).AuthLogicalVerifiedAt is not null
            && (await JobAsync(store, owner.InstanceId, "account", owner.Id)).Attempts > 0, TimeSpan.FromSeconds(10))).IsTrue();
        var held = await JobAsync(store, owner.InstanceId, "account", owner.Id);
        await Assert.That(held.FailureCode).IsEqualTo("organization_handoff_required");
        await Assert.That(held.AuthLogicalVerifiedAt).IsNull();
        await Assert.That(await store.GetAsync(AuthStore.MembershipCollection, owner.InstanceId, organization + "/" + owner.Id)).IsNotNull();
        await Assert.ThrowsAsync<ApiException>(() => oidc.Server.App.Services.GetRequiredService<ApiKeys>()
            .CreateAsync(owner, organization, null, "late request", [], null, null, [], CancellationToken.None));
        await Assert.That(await store.QueryAsync(AuthStore.ApiKeyCollection, owner.InstanceId, "/organizationId", organization).AnyAsync()).IsFalse();
        await Assert.That((await JobAsync(store, independent.InstanceId, "account", independent.Id)).AuthLogicalVerifiedAt).IsNotNull();
    }

    [Test]
    public async Task A_bounded_pass_keeps_the_job_pending_until_every_page_is_absent_and_blocks_a_late_claimed_token()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var user = (await users.CreateAsync("terrascale", "pages@example.test"))!;
        var late = AuthStore.NewId();
        var sessionWrites = new List<StoreOperation>();
        for (var index = 0; index < 105; index++)
        {
            var hash = index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);
            sessionWrites.Add(AuthStore.Create(AuthStore.SessionCollection, user.InstanceId, hash,
                TerraBaseFixture.Json($$"""{"instanceId":"{{user.InstanceId}}","userId":"{{user.Id}}","tokenHash":"{{hash}}"}""")));
            if (sessionWrites.Count == 35)
            {
                await Assert.That(await store.TransactAsync(sessionWrites)).IsNull();
                sessionWrites.Clear();
            }
        }

        await users.DeleteAsync(user.InstanceId, user.Id);
        var cleanup = oidc.Server.App.Services.GetRequiredService<DeletionCleanup>();
        await cleanup.RunOnceAsync();
        var first = await JobAsync(store, user.InstanceId, "account", user.Id);
        await Assert.That(first.FailureCode).IsEqualTo("batch_remaining");
        await Assert.That(first.AuthLogicalVerifiedAt).IsNull();
        await Assert.That(await store.QueryAsync(AuthStore.SessionCollection, user.InstanceId, "/userId", user.Id).CountAsync()).IsEqualTo(5);
        await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(() => store.CreateClaimedAsync(AuthStore.OidcTokenCollection, user.InstanceId, late,
            TerraBaseFixture.Json($$"""{"instanceId":"{{user.InstanceId}}","subject":"{{user.Id}}","id":"{{late}}"}"""), "oidc-reference", "late-handle"));
        await Assert.That(await store.FindUniqueOwnerAsync(user.InstanceId, "oidc-reference", "late-handle")).IsNull();
        clock.UtcNow += TimeSpan.FromMinutes(2);
        await cleanup.RunOnceAsync();
        await Assert.That((await JobAsync(store, user.InstanceId, "account", user.Id)).AuthLogicalVerifiedAt).IsNotNull();
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
