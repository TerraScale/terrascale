using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Identity;

/// <summary>Credential persistence on the real TerraBase, with independent application replicas.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TotpTests(TerraBaseFixture fixture)
{

    [Test]
    public async Task Independent_replicas_have_one_winner_for_activation_totp_and_each_recovery_code()
    {
        var clock = new StoreTestClock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var first = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var second = replica.App.Services.GetRequiredService<TotpCredentials>();
        var caller = await oidc.CreateUserAsync();
        var pending = (await first.BeginAsync(caller))!;
        var confirmed = await Task.WhenAll(first.ConfirmAsync(caller, Code(pending, clock)), second.ConfirmAsync(caller, Code(pending, clock)));
        await Assert.That(confirmed.Count(result => result is not null)).IsEqualTo(1);
        var active = confirmed.Single(result => result is not null)!;

        clock.UtcNow = clock.UtcNow.AddSeconds(30);
        var accepted = await Task.WhenAll(first.VerifyAsync(active.User, Code(pending, clock)), second.VerifyAsync(active.User, Code(pending, clock)));
        await Assert.That(accepted.Count(result => result)).IsEqualTo(1);
        await Assert.That(await second.VerifyAsync(active.User, Code(pending, clock))).IsFalse();
        var recovered = await Task.WhenAll(first.VerifyAsync(active.User, active.Codes[0]), second.VerifyAsync(active.User, active.Codes[0]));
        await Assert.That(recovered.Count(result => result)).IsEqualTo(1);
        await Assert.That(await first.VerifyAsync(active.User, active.Codes[0])).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Confirmation_returns_durable_recovery_codes_when_a_validated_proof_expires_during_commit(bool enrollmentExpires)
    {
        var clock = new StoreTestClock();
        var pause = new PausingTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock,
            services: services => services.AddSingleton(new HttpClient(pause)));
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var caller = await oidc.CreateUserAsync();
        var pending = (await mfa.BeginAsync(caller))!;
        var evaluatedAt = enrollmentExpires ? pending.ExpiresAt.AddSeconds(-1) : clock.UtcNow.AddSeconds(29);
        var completedAt = evaluatedAt.AddSeconds(61);
        clock.UtcNow = evaluatedAt;
        var proof = Code(pending, clock);
        var step = evaluatedAt.ToUnixTimeSeconds() / 30;
        if (!enrollmentExpires)
        {
            while (completedAt < pending.ExpiresAt && Accepts(pending, proof, completedAt))
            {
                completedAt = completedAt.AddSeconds(30);
            }

            await Assert.That(completedAt).IsLessThan(pending.ExpiresAt);
            await Assert.That(Accepts(pending, proof, completedAt)).IsFalse();
        }

        var credentialBefore = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!;
        var userBefore = (await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id))!;

        // Already expired proofs must leave the credential and user untouched.
        clock.UtcNow = completedAt;
        await Assert.That(await mfa.ConfirmAsync(caller, proof)).IsNull();
        await Assert.That((await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!.Version).IsEqualTo(credentialBefore.Version);
        await Assert.That((await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id))!.Version).IsEqualTo(userBefore.Version);

        clock.UtcNow = evaluatedAt;
        var confirming = await pause.HoldNextAsync(() => mfa.ConfirmAsync(caller, proof));
        clock.UtcNow = completedAt;
        var active = await pause.ReleaseAsync(confirming);
        await Assert.That(active).IsNotNull();
        await Assert.That(active!.User.AuthEpoch).IsEqualTo(caller.AuthEpoch + 1);
        await Assert.That(active.Codes.Count).IsEqualTo(8);
        await Assert.That(active.Codes.Distinct().Count()).IsEqualTo(8);
        await Assert.That(active.ExpiresAt).IsEqualTo(evaluatedAt.AddDays(365));
        var credentialAfter = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!;
        var stored = credentialAfter.Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
        await Assert.That(credentialAfter.Version).IsNotEqualTo(credentialBefore.Version);
        await Assert.That(stored.Enabled).IsTrue();
        await Assert.That(stored.SecretBox).IsEqualTo(credentialBefore.Document.GetProperty("secretBox").GetString());
        await Assert.That(stored.EnrollmentExpiresAt).IsNull();
        await Assert.That(stored.LastAcceptedStep).IsEqualTo(step);
        await Assert.That(stored.RecoveryExpiresAt).IsEqualTo(active.ExpiresAt);
        await Assert.That(stored.RecoveryHashes).IsEquivalentTo(active.Codes.Select(Tokens.Sha256Hex));
        var userAfter = (await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id))!;
        await Assert.That(userAfter.Version).IsNotEqualTo(userBefore.Version);
        await Assert.That(userAfter.Document.GetProperty("authEpoch").GetInt64()).IsEqualTo(active.User.AuthEpoch);

        await Assert.That(await mfa.ConfirmAsync(active.User, Code(pending, clock))).IsNull();
        await Assert.That((await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!.Version).IsEqualTo(credentialAfter.Version);
        await Assert.That(await mfa.VerifyAsync(active.User, active.Codes[0])).IsTrue();
        await Assert.That(await mfa.VerifyAsync(active.User, active.Codes[0])).IsFalse();
        var consumed = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!
            .Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
        await Assert.That(consumed.RecoveryHashes).IsEquivalentTo(active.Codes.Skip(1).Select(Tokens.Sha256Hex));
        await Assert.That((await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id))!.Version).IsEqualTo(userAfter.Version);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Verification_and_disable_report_success_when_a_validated_proof_expires_during_commit(bool disable, bool recovery)
    {
        var clock = new StoreTestClock();
        var pause = new PausingTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock,
            services: services => services.AddSingleton(new HttpClient(pause)));
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var caller = await oidc.CreateUserAsync();
        var pending = (await mfa.BeginAsync(caller))!;
        var active = (await mfa.ConfirmAsync(caller, Code(pending, clock)))!;
        var evaluatedAt = recovery ? active.ExpiresAt.AddSeconds(-1) : clock.UtcNow.AddSeconds(30);
        var completedAt = recovery ? active.ExpiresAt : evaluatedAt.AddSeconds(60);
        clock.UtcNow = evaluatedAt;
        var proof = recovery ? active.Codes[0] : Code(pending, clock);
        if (!recovery)
        {
            while (completedAt < pending.ExpiresAt && Accepts(pending, proof, completedAt))
            {
                completedAt = completedAt.AddSeconds(30);
            }

            await Assert.That(completedAt).IsLessThan(pending.ExpiresAt);
            await Assert.That(Accepts(pending, proof, completedAt)).IsFalse();
        }

        var credentialBefore = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!;
        var original = credentialBefore.Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
        var userBefore = (await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id))!;

        clock.UtcNow = completedAt;
        await Assert.That(await (disable ? mfa.DisableAsync(active.User, proof) : mfa.VerifyAsync(active.User, proof))).IsFalse();
        await Assert.That((await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!.Version).IsEqualTo(credentialBefore.Version);
        await Assert.That((await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id))!.Version).IsEqualTo(userBefore.Version);

        clock.UtcNow = evaluatedAt;
        var operating = await pause.HoldNextAsync(() => disable ? mfa.DisableAsync(active.User, proof) : mfa.VerifyAsync(active.User, proof));
        clock.UtcNow = completedAt;
        await Assert.That(await pause.ReleaseAsync(operating)).IsTrue();
        var credentialAfter = (await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!;
        var stored = credentialAfter.Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;
        await Assert.That(credentialAfter.Version).IsNotEqualTo(credentialBefore.Version);
        var userAfter = (await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id))!;
        var currentUser = (await users.GetAsync(caller.InstanceId, caller.Id))!;
        await Assert.That(currentUser.AuthEpoch).IsEqualTo(active.User.AuthEpoch + (disable ? 1 : 0));
        await Assert.That(userAfter.Document.GetProperty("authEpoch").GetInt64()).IsEqualTo(currentUser.AuthEpoch);
        if (disable)
        {
            await Assert.That(userAfter.Version).IsNotEqualTo(userBefore.Version);
            await Assert.That(stored.Enabled).IsFalse();
            await Assert.That(stored.SecretBox).IsNull();
            await Assert.That(stored.EnrollmentExpiresAt).IsNull();
            await Assert.That(stored.LastAcceptedStep).IsEqualTo(-1);
            await Assert.That(stored.RecoveryHashes.Count).IsEqualTo(0);
            await Assert.That(stored.RecoveryExpiresAt).IsNull();
        }
        else
        {
            await Assert.That(userAfter.Version).IsEqualTo(userBefore.Version);
            await Assert.That(stored.Enabled).IsTrue();
            await Assert.That(stored.SecretBox).IsEqualTo(original.SecretBox);
            await Assert.That(stored.LastAcceptedStep).IsEqualTo(recovery ? original.LastAcceptedStep : evaluatedAt.ToUnixTimeSeconds() / 30);
            await Assert.That(stored.RecoveryHashes).IsEquivalentTo(recovery
                ? original.RecoveryHashes.Where(hash => hash != Tokens.Sha256Hex(proof)) : original.RecoveryHashes);
            await Assert.That(stored.RecoveryExpiresAt).IsEqualTo(original.RecoveryExpiresAt);
        }

        // Rewind to prove replay rejection even while the proof's time window is valid.
        clock.UtcNow = evaluatedAt;
        await Assert.That(await (disable ? mfa.DisableAsync(currentUser, proof) : mfa.VerifyAsync(currentUser, proof))).IsFalse();
        await Assert.That((await store.GetAsync(AuthStore.MfaCollection, caller.InstanceId, caller.Id))!.Version).IsEqualTo(credentialAfter.Version);
        await Assert.That((await store.GetAsync(AuthStore.UserCollection, caller.InstanceId, caller.Id))!.Version).IsEqualTo(userAfter.Version);
    }

    [Test]
    public async Task Expired_superseded_and_pre_reset_enrollments_are_refused_and_active_mfa_survives_password_reset()
    {
        var clock = new StoreTestClock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var passwords = oidc.Server.App.Services.GetRequiredService<PasswordHasher>();
        var caller = await oidc.CreateUserAsync();
        var pending = (await mfa.BeginAsync(caller))!;
        clock.UtcNow = pending.ExpiresAt;
        await Assert.That(await mfa.ConfirmAsync(caller, Code(pending, clock))).IsNull();

        pending = (await mfa.BeginAsync(caller))!;
        var hash = await passwords.HashAsync("synthetic password for reset", CancellationToken.None);
        await Assert.That(await users.SetPasswordAsync(caller, hash)).IsTrue();
        await Assert.That(await mfa.ConfirmAsync(caller, Code(pending, clock))).IsNull();
        caller = (await users.GetAsync(caller.InstanceId, caller.Id))!;
        await Assert.That(await mfa.ConfirmAsync(caller, Code(pending, clock))).IsNull();

        pending = (await mfa.BeginAsync(caller))!;
        var superseded = (await mfa.BeginAsync(caller))!;
        // Independent secrets can produce the same code in any accepted step.
        while (clock.UtcNow < superseded.ExpiresAt && Accepts(superseded, Code(pending, clock), clock.UtcNow))
        {
            clock.UtcNow = clock.UtcNow.AddSeconds(30);
        }

        await Assert.That(clock.UtcNow).IsLessThan(superseded.ExpiresAt);
        await Assert.That(Accepts(superseded, Code(pending, clock), clock.UtcNow)).IsFalse();
        await Assert.That(await mfa.ConfirmAsync(caller, Code(pending, clock))).IsNull();
        var active = (await mfa.ConfirmAsync(caller, Code(superseded, clock)))!;
        await Assert.That(await users.SetPasswordAsync(active.User, hash)).IsTrue();
        caller = (await users.GetAsync(caller.InstanceId, caller.Id))!;
        await Assert.That(await mfa.BeginAsync(caller)).IsNull();
        clock.UtcNow = clock.UtcNow.AddSeconds(30);
        await Assert.That(await mfa.VerifyAsync(active.User, Code(superseded, clock))).IsFalse();
        await Assert.That(await mfa.VerifyAsync(caller, Code(superseded, clock))).IsTrue();
        clock.UtcNow = active.ExpiresAt;
        await Assert.That(await mfa.VerifyAsync(caller, active.Codes[0])).IsFalse();
    }

    private static string Code(TotpEnrollment enrollment, StoreTestClock clock) =>
        new Totp(Base32Encoding.ToBytes(enrollment.Secret)).ComputeTotp(clock.UtcNow.UtcDateTime);

    private static bool Accepts(TotpEnrollment enrollment, string code, DateTimeOffset at) =>
        new Totp(Base32Encoding.ToBytes(enrollment.Secret)).VerifyTotp(at.UtcDateTime, code, out _, new VerificationWindow(previous: 1, future: 1));

    [Test]
    public async Task An_epoch_change_between_reads_and_commit_refuses_activation_and_preserves_unspent_proofs()
    {
        var clock = new StoreTestClock();
        var pause = new PausingTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock,
            services: services => services.AddSingleton(new HttpClient(pause)));
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var users = replica.App.Services.GetRequiredService<Users>();
        var sessions = replica.App.Services.GetRequiredService<Sessions>();
        var caller = await oidc.CreateUserAsync();
        var pending = (await mfa.BeginAsync(caller))!;

        var activating = await pause.HoldNextAsync(() => mfa.ConfirmAsync(caller, Code(pending, clock)));
        await sessions.RevokeAllAsync(caller.InstanceId, caller.Id);
        await Assert.That(await pause.ReleaseAsync(activating)).IsNull();
        caller = (await users.GetAsync(caller.InstanceId, caller.Id))!;
        await Assert.That(await mfa.ConfirmAsync(caller, Code(pending, clock))).IsNull();

        pending = (await mfa.BeginAsync(caller))!;
        var active = (await mfa.ConfirmAsync(caller, Code(pending, clock)))!;
        clock.UtcNow = clock.UtcNow.AddSeconds(30);
        var verifying = await pause.HoldNextAsync(() => mfa.VerifyAsync(active.User, Code(pending, clock)));
        await sessions.RevokeAllAsync(caller.InstanceId, caller.Id);
        await Assert.That(await pause.ReleaseAsync(verifying)).IsFalse();
        caller = (await users.GetAsync(caller.InstanceId, caller.Id))!;
        await Assert.That(await mfa.VerifyAsync(caller, Code(pending, clock))).IsTrue();
        await Assert.That(await mfa.VerifyAsync(caller, Code(pending, clock))).IsFalse();
    }

    [Test]
    public async Task Cross_account_proofs_and_misbound_ciphertext_fail_closed_and_attempt_limits_are_durable()
    {
        var clock = new StoreTestClock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var first = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var second = replica.App.Services.GetRequiredService<TotpCredentials>();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var caller = await oidc.CreateUserAsync();
        var other = (await users.CreateAsync("01ARZ3NDEKTSV4RRFFQ69G5FAV", "bob@example.test"))!;
        var pending = (await first.BeginAsync(caller))!;
        var active = (await first.ConfirmAsync(caller, Code(pending, clock)))!;
        await Assert.That(await first.VerifyAsync(other, active.Codes[0])).IsFalse();

        // A copied credential with rewritten ownership still cannot open its record-bound secret.
        var original = (await oidc.Database.ListDocumentsAsync(AuthStore.MfaCollection)).Items.Single().Document;
        var copied = JsonNode.Parse(original.GetRawText())!.AsObject();
        copied["instanceId"] = other.InstanceId;
        copied["userId"] = other.Id;
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        await store.CreateAsync(AuthStore.MfaCollection, other.InstanceId, other.Id, JsonSerializer.SerializeToElement(copied));
        var corrupt = await Assert.ThrowsAsync<ApiException>(() => second.VerifyAsync(other, Code(pending, clock)));
        await Assert.That(corrupt!.Status).IsEqualTo(503);

        // The successful confirmation also charges the account budget: the next nine attempts spend it.
        for (var index = 0; index < 9; index++)
        {
            await Assert.That(await first.VerifyAsync(active.User, "wrong-code")).IsFalse();
        }

        var limited = await Assert.ThrowsAsync<ApiException>(() => second.VerifyAsync(active.User, active.Codes[0]));
        await Assert.That(limited!.Status).IsEqualTo(429);
        clock.UtcNow = clock.UtcNow.AddMinutes(15);
        await Assert.That(await second.VerifyAsync(active.User, active.Codes[0])).IsTrue();
    }
}
