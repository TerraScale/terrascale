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

namespace TerraScale.Authentication.Fast.Identity;

/// <summary>Credential persistence on the real TerraBase, with independent application replicas.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TotpTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Enrollment_encrypts_the_secret_and_activation_and_disable_revoke_old_sessions()
    {
        var clock = new StoreTestClock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var caller = await oidc.CreateUserAsync();
        var before = await sessions.CreateAsync(caller);

        var pending = (await mfa.BeginAsync(caller))!;
        await Assert.That(pending.ExpiresAt).IsEqualTo(clock.UtcNow.AddMinutes(10));
        await Assert.That(pending.ProvisioningUri).StartsWith("otpauth://totp/");
        await Assert.That(pending.ProvisioningUri).Contains("secret=" + pending.Secret);
        await Assert.That(pending.ToString()).DoesNotContain(pending.Secret);
        var stored = (await oidc.Database.ListDocumentsAsync(AuthStore.MfaCollection)).Items.Single().Document.GetRawText();
        await Assert.That(stored).DoesNotContain(pending.Secret);

        await Assert.That(await mfa.ConfirmAsync(caller, "bad-code")).IsNull();
        var activated = (await mfa.ConfirmAsync(caller, Code(pending, clock)))!;
        await Assert.That(activated.User.AuthEpoch).IsEqualTo(caller.AuthEpoch + 1);
        await Assert.That(activated.Codes.Count).IsEqualTo(8);
        await Assert.That(activated.Codes.Distinct().Count()).IsEqualTo(8);
        await Assert.That(activated.ExpiresAt).IsEqualTo(clock.UtcNow.AddDays(365));
        await Assert.That(await sessions.ResolveAsync(caller.InstanceId, before.Token)).IsNull();
        await Assert.That(await mfa.ConfirmAsync(caller, Code(pending, clock))).IsNull();
        await Assert.That(await mfa.BeginAsync(activated.User)).IsNull();

        stored = (await oidc.Database.ListDocumentsAsync(AuthStore.MfaCollection)).Items.Single().Document.GetRawText();
        foreach (var recovery in activated.Codes)
        {
            await Assert.That(recovery.Length).IsEqualTo(24);
            await Assert.That(stored).DoesNotContain(recovery);
            await Assert.That(activated.ToString()).DoesNotContain(recovery);
        }

        // The activation step is already consumed, and a stale caller never verifies a new code.
        await Assert.That(await mfa.VerifyAsync(activated.User, Code(pending, clock))).IsFalse();
        clock.UtcNow = clock.UtcNow.AddSeconds(30);
        await Assert.That(await mfa.VerifyAsync(caller, Code(pending, clock))).IsFalse();
        await Assert.That(await mfa.VerifyAsync(activated.User, Code(pending, clock))).IsTrue();
        await Assert.That(await mfa.VerifyAsync(activated.User, Code(pending, clock))).IsFalse();

        var refusal = await Assert.ThrowsAsync<ApiException>(() => sessions.CreateAsync(activated.User));
        await Assert.That(refusal!.Status).IsEqualTo(401);
        // Synthetic weak session: disabling must advance its epoch even though primary authentication refuses it.
        var token = Tokens.RandomToken(48);
        var after = new SessionRecord(caller.InstanceId, caller.Id, Tokens.Sha256Hex(token), activated.User.AuthEpoch, clock.UtcNow, clock.UtcNow + Sessions.Lifetime);
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        await store.CreateAsync(AuthStore.SessionCollection, caller.InstanceId, after.TokenHash,
            JsonSerializer.SerializeToElement(after, IdentityJson.Default.SessionRecord));
        var beforeDisable = (await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, after.TokenHash))!;
        await Assert.That(await mfa.DisableAsync(activated.User, "bad-code")).IsFalse();
        await Assert.That(await mfa.DisableAsync(activated.User, activated.Codes[0])).IsTrue();
        await Assert.That(await sessions.ResolveAsync(caller.InstanceId, token)).IsNull();
        var afterDisable = (await store.GetAsync(AuthStore.SessionCollection, caller.InstanceId, after.TokenHash))!;
        await Assert.That(afterDisable.Version).IsEqualTo(beforeDisable.Version);
        await Assert.That(afterDisable.Document.GetProperty("authEpoch").GetInt64()).IsEqualTo(activated.User.AuthEpoch);
        var disabled = (await users.GetAsync(caller.InstanceId, caller.Id))!;
        await Assert.That(disabled.AuthEpoch).IsEqualTo(caller.AuthEpoch + 2);
        await Assert.That(await mfa.VerifyAsync(disabled, activated.Codes[1])).IsFalse();
        await Assert.That(await mfa.BeginAsync(disabled)).IsNotNull();
    }





    private static string Code(TotpEnrollment enrollment, StoreTestClock clock) =>
        new Totp(Base32Encoding.ToBytes(enrollment.Secret)).ComputeTotp(clock.UtcNow.UtcDateTime);



}
