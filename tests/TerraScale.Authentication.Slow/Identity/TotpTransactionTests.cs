using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using System.Text.Json;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Slow.Identity;

/// <summary>Internal caller-authorization composition on real TerraBase; no public authentication boundary is exercised.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TotpTransactionTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Each_credential_transition_consumes_its_caller_session_in_the_same_commit()
    {
        var clock = new StoreTestClock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var caller = await oidc.CreateUserAsync();
        var userBefore = await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id);

        var begin = await AuthorizationAsync(store, caller, clock);
        var pending = await mfa.BeginAsync(caller, begin.Authorization);
        await Assert.That(pending).IsNotNull();
        await Assert.That(await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, begin.Session.Session.TokenHash)).IsNull();
        await Assert.That(await sessions.ResolveAsync(caller.InstanceId, begin.Session.Token)).IsNull();
        var credential = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!;
        var stored = credential.Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
        await Assert.That(stored.Enabled).IsFalse();
        await Assert.That(stored.EnrollmentEpoch).IsEqualTo(caller.AuthEpoch);
        await Assert.That(stored.EnrollmentExpiresAt).IsEqualTo(pending!.ExpiresAt);
        await UnchangedAsync(userBefore, await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id));

        var confirm = await AuthorizationAsync(store, caller, clock);
        var active = await mfa.ConfirmAsync(caller, Code(pending, clock), confirm.Authorization);
        await Assert.That(active).IsNotNull();
        await Assert.That(await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, confirm.Session.Session.TokenHash)).IsNull();
        await Assert.That(active!.User.AuthEpoch).IsEqualTo(caller.AuthEpoch + 1);
        var activated = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!;
        stored = activated.Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
        await Assert.That(activated.Version).IsNotEqualTo(credential.Version);
        await Assert.That(stored.Enabled).IsTrue();
        await Assert.That(stored.RecoveryHashes).IsEquivalentTo(active.Codes.Select(Tokens.Sha256Hex));
        await Assert.That(stored.LastAcceptedStep).IsEqualTo(clock.UtcNow.ToUnixTimeSeconds() / 30);
        var activatedUser = (await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id))!;
        await Assert.That(activatedUser.Version).IsNotEqualTo(userBefore!.Version);
        await Assert.That(activatedUser.Document.GetProperty("authEpoch").GetInt64()).IsEqualTo(active.User.AuthEpoch);

        clock.UtcNow = clock.UtcNow.AddSeconds(30);
        var verify = await AuthorizationAsync(store, active.User, clock);
        await Assert.That(await mfa.VerifyAsync(active.User, Code(pending, clock), verify.Authorization)).IsTrue();
        await Assert.That(await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, verify.Session.Session.TokenHash)).IsNull();
        var verified = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!;
        stored = verified.Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
        await Assert.That(verified.Version).IsNotEqualTo(activated.Version);
        await Assert.That(stored.LastAcceptedStep).IsEqualTo(clock.UtcNow.ToUnixTimeSeconds() / 30);
        await UnchangedAsync(activatedUser, await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id));

        var recover = await AuthorizationAsync(store, active.User, clock);
        await Assert.That(await mfa.VerifyAsync(active.User, active.Codes[0], recover.Authorization)).IsTrue();
        await Assert.That(await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, recover.Session.Session.TokenHash)).IsNull();
        stored = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!
            .Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
        await Assert.That(stored.RecoveryHashes).IsEquivalentTo(active.Codes.Skip(1).Select(Tokens.Sha256Hex));
        await UnchangedAsync(activatedUser, await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id));

        var disable = await AuthorizationAsync(store, active.User, clock);
        await Assert.That(await mfa.DisableAsync(active.User, active.Codes[1], disable.Authorization)).IsTrue();
        await Assert.That(await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, disable.Session.Session.TokenHash)).IsNull();
        stored = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!
            .Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
        await Assert.That(stored.Enabled).IsFalse();
        await Assert.That(stored.SecretBox).IsNull();
        await Assert.That(stored.RecoveryHashes.Count).IsEqualTo(0);
        var disabledUser = (await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id))!;
        await Assert.That(disabledUser.Version).IsNotEqualTo(activatedUser.Version);
        await Assert.That(disabledUser.Document.GetProperty("authEpoch").GetInt64()).IsEqualTo(active.User.AuthEpoch + 1);
    }

    [Test]
    [Arguments("begin", false)]
    [Arguments("begin", true)]
    [Arguments("confirm", false)]
    [Arguments("confirm", true)]
    [Arguments("verify", false)]
    [Arguments("verify", true)]
    [Arguments("disable", false)]
    [Arguments("disable", true)]
    public async Task A_competing_delete_or_stale_session_version_rolls_back_all_credential_and_user_writes(string transition, bool stale)
    {
        var clock = new StoreTestClock();
        var pause = new PausingTransactions(CredentialTransactions.IsCredentialTransaction) { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(new CredentialTransactions { InnerHandler = pause });
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(http));
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var competingStore = replica.App.Services.GetRequiredService<AuthStore>();
        var competingSessions = replica.App.Services.GetRequiredService<Sessions>();
        var (caller, code) = await PrepareAsync(oidc, mfa, clock, transition);
        var authorization = await AuthorizationAsync(store, caller, clock);
        var credentialBefore = await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id);
        var userBefore = await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id);

        var operating = await pause.HoldNextAsync(() => OperateAsync(mfa, caller, transition, code, authorization.Authorization));
        TerraBaseDocument? sessionAfterCompetition;
        bool accepted;
        try
        {
            if (stale)
            {
                await Assert.That(await competingSessions.RevokeAsync(caller.InstanceId, authorization.Session.Token)).IsTrue();
                sessionAfterCompetition = (await competingStore.GetAsync(AuthStore.SessionCollection, caller.InstanceId, authorization.Session.Session.TokenHash))!;
                await Assert.That(sessionAfterCompetition.Version).IsNotEqualTo(authorization.Document.Version);
            }
            else
            {
                await Assert.That(await competingStore.TransactAsync([authorization.Authorization.Operation])).IsNull();
                sessionAfterCompetition = null;
            }
        }
        finally
        {
            accepted = await pause.ReleaseAsync(operating);
        }

        await Assert.That(accepted).IsFalse();
        await UnchangedAsync(credentialBefore, await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id));
        await UnchangedAsync(userBefore, await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id));
        await UnchangedAsync(sessionAfterCompetition, await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, authorization.Session.Session.TokenHash));

        var fresh = await AuthorizationAsync(store, caller, clock);
        await Assert.That(await OperateAsync(mfa, caller, transition, code, fresh.Authorization)).IsTrue();
        await Assert.That(await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, fresh.Session.Session.TokenHash)).IsNull();
    }

    [Test]
    [Arguments("confirm")]
    [Arguments("verify")]
    [Arguments("disable")]
    public async Task An_invalid_factor_leaves_the_caller_session_and_credential_unchanged_for_a_valid_retry(string transition)
    {
        var clock = new StoreTestClock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var (caller, code) = await PrepareAsync(oidc, mfa, clock, transition);
        var authorization = await AuthorizationAsync(store, caller, clock);
        var credentialBefore = await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id);
        var userBefore = await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id);

        await Assert.That(await OperateAsync(mfa, caller, transition, "invalid-factor", authorization.Authorization)).IsFalse();
        await UnchangedAsync(credentialBefore, await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id));
        await UnchangedAsync(userBefore, await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id));
        await UnchangedAsync(authorization.Document, await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, authorization.Session.Session.TokenHash));
        await Assert.That((await sessions.ResolveAsync(caller.InstanceId, authorization.Session.Token)).HasValue).IsEqualTo(transition is "begin" or "confirm");
        await Assert.That(await OperateAsync(mfa, caller, transition, code, authorization.Authorization)).IsTrue();
        await Assert.That(await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, authorization.Session.Session.TokenHash)).IsNull();
    }

    [Test]
    public async Task Independent_replicas_racing_distinct_recovery_codes_for_one_caller_session_leave_the_losing_code_unspent()
    {
        var clock = new StoreTestClock();
        var pause = new PausingTransactions(CredentialTransactions.IsCredentialTransaction) { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(new CredentialTransactions { InnerHandler = pause });
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(http));
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var first = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var second = replica.App.Services.GetRequiredService<TotpCredentials>();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var caller = await oidc.CreateUserAsync();
        var pending = (await first.BeginAsync(caller))!;
        var active = (await first.ConfirmAsync(caller, Code(pending, clock)))!;
        var authorization = await AuthorizationAsync(store, active.User, clock);
        var userBefore = await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id);

        var losing = await pause.HoldNextAsync(() => first.VerifyAsync(active.User, active.Codes[0], authorization.Authorization));
        bool loserAccepted;
        try
        {
            await Assert.That(await second.VerifyAsync(active.User, active.Codes[1], authorization.Authorization)).IsTrue();
        }
        finally
        {
            loserAccepted = await pause.ReleaseAsync(losing);
        }

        await Assert.That(loserAccepted).IsFalse();
        await Assert.That(await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, authorization.Session.Session.TokenHash)).IsNull();
        var credentialAfter = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!;
        var stored = credentialAfter.Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
        await Assert.That(stored.RecoveryHashes).IsEquivalentTo(active.Codes.Where(code => code != active.Codes[1]).Select(Tokens.Sha256Hex));
        await UnchangedAsync(userBefore, await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id));

        // A fresh credential read cannot make the consumed caller session reusable.
        await Assert.That(await first.VerifyAsync(active.User, active.Codes[0], authorization.Authorization)).IsFalse();
        await UnchangedAsync(credentialAfter, await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id));
        var fresh = await AuthorizationAsync(store, active.User, clock);
        await Assert.That(await first.VerifyAsync(active.User, active.Codes[0], fresh.Authorization)).IsTrue();
        await Assert.That(await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, fresh.Session.Session.TokenHash)).IsNull();
        stored = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!
            .Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
        await Assert.That(stored.RecoveryHashes).IsEquivalentTo(active.Codes.Skip(2).Select(Tokens.Sha256Hex));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Revocation_between_read_and_commit_leaves_the_caller_session_and_recovery_state_unchanged(bool disable)
    {
        var clock = new StoreTestClock();
        var pause = new PausingTransactions(CredentialTransactions.IsCredentialTransaction) { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(new CredentialTransactions { InnerHandler = pause });
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(http));
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var revokingSessions = replica.App.Services.GetRequiredService<Sessions>();
        var users = replica.App.Services.GetRequiredService<Users>();
        var caller = await oidc.CreateUserAsync();
        var pending = (await mfa.BeginAsync(caller))!;
        var active = (await mfa.ConfirmAsync(caller, Code(pending, clock)))!;
        var authorization = await AuthorizationAsync(store, active.User, clock);
        var credentialBefore = await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id);

        var operating = await pause.HoldNextAsync(() => disable
            ? mfa.DisableAsync(active.User, active.Codes[0], authorization.Authorization)
            : mfa.VerifyAsync(active.User, active.Codes[0], authorization.Authorization));
        TerraBaseDocument? revokedUser;
        bool accepted;
        try
        {
            await Assert.That(await revokingSessions.RevokeAllAsync(caller.InstanceId, caller.Id)).IsTrue();
            revokedUser = await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id);
        }
        finally
        {
            accepted = await pause.ReleaseAsync(operating);
        }

        await Assert.That(accepted).IsFalse();
        await UnchangedAsync(credentialBefore, await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id));
        await UnchangedAsync(authorization.Document, await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, authorization.Session.Session.TokenHash));
        await UnchangedAsync(revokedUser, await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id));
        await Assert.That(await sessions.ResolveAsync(caller.InstanceId, authorization.Session.Token)).IsNull();

        var current = (await users.GetAsync(caller.InstanceId, caller.Id))!;
        await Assert.That(current.AuthEpoch).IsEqualTo(active.User.AuthEpoch + 1);
        var fresh = await AuthorizationAsync(store, current, clock);
        await Assert.That(await mfa.VerifyAsync(current, active.Codes[0], fresh.Authorization)).IsTrue();
        await Assert.That(await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, fresh.Session.Session.TokenHash)).IsNull();
        await UnchangedAsync(authorization.Document, await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, authorization.Session.Session.TokenHash));
        var stored = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!
            .Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
        await Assert.That(stored.RecoveryHashes).IsEquivalentTo(active.Codes.Skip(1).Select(Tokens.Sha256Hex));
    }

    [Test]
    [Arguments("begin", false)]
    [Arguments("begin", true)]
    [Arguments("confirm", false)]
    [Arguments("confirm", true)]
    [Arguments("verify", false)]
    [Arguments("verify", true)]
    [Arguments("disable", false)]
    [Arguments("disable", true)]
    public async Task Authorization_at_its_exact_expiry_is_refused_without_submission_even_after_async_reads(string transition, bool duringRead)
    {
        var clock = new StoreTestClock();
        var pause = new PausingTransactions(request => request.Method == HttpMethod.Get
            && request.RequestUri!.AbsolutePath.Contains($"/collections/{AuthStore.MfaCollection}/documents/", StringComparison.Ordinal))
        { InnerHandler = TerraBaseFixture.NewHandler() };
        var transport = new CredentialTransactions { InnerHandler = pause };
        using var http = new HttpClient(transport);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(http));
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var caller = await oidc.CreateUserAsync();
        var pending = transition == "begin" ? null : (await mfa.BeginAsync(caller))!;
        IssuedRecoveryCodes? active = null;
        if (transition is "verify" or "disable")
        {
            active = (await mfa.ConfirmAsync(caller, Code(pending!, clock)))!;
            caller = active.User;
        }

        var authorization = await AuthorizationAsync(store, caller, clock);
        var expiresAt = authorization.Authorization.ExpiresAt;
        // The factor is valid at the resumed read's time; only caller authorization has expired.
        var code = transition == "confirm" ? Code(pending!, expiresAt) : active?.Codes[0] ?? string.Empty;
        var credentialBefore = await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id);
        var userBefore = await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id);
        var submittedBefore = transport.CredentialRequests;
        bool accepted;
        if (duringRead)
        {
            var operating = await pause.HoldNextAsync(() => OperateAsync(mfa, caller, transition, code, authorization.Authorization));
            clock.UtcNow = expiresAt;
            accepted = await pause.ReleaseAsync(operating);
        }
        else
        {
            clock.UtcNow = expiresAt;
            accepted = await OperateAsync(mfa, caller, transition, code, authorization.Authorization);
        }

        await Assert.That(accepted).IsFalse();
        await Assert.That(transport.CredentialRequests).IsEqualTo(submittedBefore);
        await UnchangedAsync(credentialBefore, await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id));
        await UnchangedAsync(userBefore, await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id));
        await UnchangedAsync(authorization.Document, await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, authorization.Session.Session.TokenHash));
        await Assert.That((await sessions.ResolveAsync(caller.InstanceId, authorization.Session.Token)).HasValue).IsEqualTo(transition is "begin" or "confirm");

        var fresh = await AuthorizationAsync(store, caller, clock);
        await Assert.That(await OperateAsync(mfa, caller, transition, code, fresh.Authorization)).IsTrue();
        await Assert.That(await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, fresh.Session.Session.TokenHash)).IsNull();
        await UnchangedAsync(authorization.Document, await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, authorization.Session.Session.TokenHash));
    }

    [Test]
    public async Task A_begin_acknowledged_after_enrollment_expiry_returns_the_committed_enrollment()
    {
        var clock = new StoreTestClock();
        var transport = new CredentialTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(transport);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(http));
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var caller = await oidc.CreateUserAsync();
        var authorization = await AuthorizationAsync(store, caller, clock);
        var evaluatedAt = clock.UtcNow;
        transport.AfterNextCommit(() => clock.UtcNow = evaluatedAt.AddMinutes(11));

        var pending = await mfa.BeginAsync(caller, authorization.Authorization);
        await Assert.That(pending).IsNotNull();
        await Assert.That(pending!.ExpiresAt).IsEqualTo(authorization.Authorization.ExpiresAt);
        await Assert.That(pending.ExpiresAt).IsLessThan(clock.UtcNow);
        await Assert.That(await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, authorization.Session.Session.TokenHash)).IsNull();
        var credentialAfter = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!;
        var stored = credentialAfter.Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
        await Assert.That(stored.EnrollmentExpiresAt).IsEqualTo(pending.ExpiresAt);
        await Assert.That(await mfa.ConfirmAsync(caller, Code(pending, clock))).IsNull();
        await UnchangedAsync(credentialAfter, await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id));

        // Rewind only to check that the returned secret is the persisted enrollment's secret.
        clock.UtcNow = evaluatedAt;
        await Assert.That(await mfa.ConfirmAsync(caller, Code(pending, clock))).IsNotNull();
    }

    [Test]
    [Arguments("begin")]
    [Arguments("confirm")]
    [Arguments("verify")]
    [Arguments("disable")]
    public async Task Losing_the_real_credential_commit_response_returns_no_success_but_preserves_state_and_refuses_replay(string transition)
    {
        var clock = new StoreTestClock();
        var transport = new CredentialTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(transport);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(http));
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var (caller, code) = await PrepareAsync(oidc, mfa, clock, transition);
        var authorization = await AuthorizationAsync(store, caller, clock);
        var credentialBefore = await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id);
        var userBefore = (await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id))!;
        var lost = false;
        transport.AfterNextCommit(() =>
        {
            lost = true;
            throw new HttpRequestException("Synthetic loss of an acknowledged credential transaction response.");
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => OperateAsync(mfa, caller, transition, code, authorization.Authorization));
        await Assert.That(lost).IsTrue();
        await Assert.That(await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, authorization.Session.Session.TokenHash)).IsNull();
        var credentialAfter = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!;
        var stored = credentialAfter.Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
        if (credentialBefore is not null)
        {
            await Assert.That(credentialAfter.Version).IsNotEqualTo(credentialBefore.Version);
        }

        var userAfter = (await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id))!;
        if (transition is "confirm" or "disable")
        {
            await Assert.That(userAfter.Version).IsNotEqualTo(userBefore.Version);
            await Assert.That(userAfter.Document.GetProperty("authEpoch").GetInt64()).IsEqualTo(caller.AuthEpoch + 1);
        }
        else
        {
            await UnchangedAsync(userBefore, userAfter);
        }

        switch (transition)
        {
            case "begin":
                await Assert.That(stored.Enabled).IsFalse();
                await Assert.That(stored.SecretBox).IsNotNull();
                await Assert.That(stored.EnrollmentExpiresAt).IsEqualTo(authorization.Authorization.ExpiresAt);
                break;
            case "confirm":
                await Assert.That(stored.Enabled).IsTrue();
                await Assert.That(stored.RecoveryHashes.Count).IsEqualTo(8);
                await Assert.That(stored.LastAcceptedStep).IsEqualTo(clock.UtcNow.ToUnixTimeSeconds() / 30);
                break;
            case "verify":
                var original = credentialBefore!.Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
                await Assert.That(stored.Enabled).IsTrue();
                await Assert.That(stored.RecoveryHashes).IsEquivalentTo(original.RecoveryHashes.Where(hash => hash != Tokens.Sha256Hex(code)));
                await Assert.That(stored.LastAcceptedStep).IsEqualTo(original.LastAcceptedStep);
                break;
            case "disable":
                await Assert.That(stored.Enabled).IsFalse();
                await Assert.That(stored.SecretBox).IsNull();
                await Assert.That(stored.RecoveryHashes.Count).IsEqualTo(0);
                break;
        }

        await Assert.That(await OperateAsync(mfa, caller, transition, code, authorization.Authorization)).IsFalse();
        await UnchangedAsync(credentialAfter, await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id));
        await UnchangedAsync(userAfter, await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id));
    }

    private static async Task<(IssuedSession Session, TerraBaseDocument Document, MfaAuthorization Authorization)> AuthorizationAsync(AuthStore store, UserRecord caller, StoreTestClock clock)
    {
        // Synthetic weak sessions exercise authorization composition, including users with an active factor.
        var token = Tokens.RandomToken(48);
        var key = Tokens.Sha256Hex(token);
        var session = new IssuedSession(token, new SessionRecord(caller.InstanceId, caller.Id, key, caller.AuthEpoch, clock.UtcNow, clock.UtcNow + Sessions.Lifetime)
        {
            AuthStamp = caller.AuthStamp,
            Bindings = caller.AuthenticationBindings,
        });
        await store.CreateAsync(AuthStore.SessionCollection, caller.InstanceId, key,
            JsonSerializer.SerializeToElement(session.Session, IdentityJson.Default.SessionRecord));
        var document = (await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, key))!;
        var record = document.Document.Deserialize(IdentityJson.Default.SessionRecord)!;
        await Assert.That(record.InstanceId).IsEqualTo(caller.InstanceId);
        await Assert.That(record.UserId).IsEqualTo(caller.Id);
        await Assert.That(record.AuthEpoch).IsEqualTo(caller.AuthEpoch);
        await Assert.That(record.AuthStamp).IsEqualTo(caller.AuthStamp);
        await Assert.That(record.Bindings).IsEquivalentTo(caller.AuthenticationBindings);
        await Assert.That(record.TokenHash).IsEqualTo(key);
        return (session, document, new MfaAuthorization(AuthStore.Delete(AuthStore.SessionCollection, caller.InstanceId, key, document.Version), clock.UtcNow.AddMinutes(5)));
    }

    private static async Task<(UserRecord Caller, string Code)> PrepareAsync(OidcHarness oidc, TotpCredentials mfa, StoreTestClock clock, string transition)
    {
        var caller = await oidc.CreateUserAsync();
        if (transition == "begin")
        {
            return (caller, string.Empty);
        }

        var pending = (await mfa.BeginAsync(caller))!;
        if (transition == "confirm")
        {
            return (caller, Code(pending, clock));
        }

        var active = (await mfa.ConfirmAsync(caller, Code(pending, clock)))!;
        return (active.User, active.Codes[0]);
    }

    private static async Task<bool> OperateAsync(TotpCredentials mfa, UserRecord caller, string transition, string code, MfaAuthorization authorization) =>
        transition switch
        {
            "begin" => await mfa.BeginAsync(caller, authorization) is not null,
            "confirm" => await mfa.ConfirmAsync(caller, code, authorization) is not null,
            "verify" => await mfa.VerifyAsync(caller, code, authorization),
            "disable" => await mfa.DisableAsync(caller, code, authorization),
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        };

    private static async Task UnchangedAsync(TerraBaseDocument? before, TerraBaseDocument? after)
    {
        if (before is null)
        {
            await Assert.That(after).IsNull();
            return;
        }

        await Assert.That(after).IsNotNull();
        await Assert.That(after!.Version).IsEqualTo(before.Version);
        await Assert.That(JsonElement.DeepEquals(after.Document, before.Document)).IsTrue();
    }

    private static string Code(TotpEnrollment enrollment, StoreTestClock clock) =>
        Code(enrollment, clock.UtcNow);

    private static string Code(TotpEnrollment enrollment, DateTimeOffset at) =>
        new Totp(Base32Encoding.ToBytes(enrollment.Secret)).ComputeTotp(at.UtcDateTime);

    /// <summary>Classifies real SDK requests before PausingTransactions and alters only acknowledged credential responses.</summary>
    private sealed class CredentialTransactions : DelegatingHandler
    {
        private static readonly HttpRequestOptionsKey<bool> CredentialTransaction = new(AuthStore.MfaCollection);
        private Action? afterNextCommit;

        public int CredentialRequests { get; private set; }

        public static bool IsCredentialTransaction(HttpRequestMessage request) =>
            request.Options.TryGetValue(CredentialTransaction, out var credential) && credential;

        public void AfterNextCommit(Action action) => this.afterNextCommit = action;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                request.Options.Set(CredentialTransaction, body.RootElement.GetProperty("ops").EnumerateArray()
                    .Any(operation => operation.GetProperty("collection").GetString() == AuthStore.MfaCollection));
                if (IsCredentialTransaction(request))
                {
                    this.CredentialRequests++;
                }
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode && IsCredentialTransaction(request) && Interlocked.Exchange(ref this.afterNextCommit, null) is { } committed)
            {
                try
                {
                    committed();
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }

            return response;
        }
    }
}
