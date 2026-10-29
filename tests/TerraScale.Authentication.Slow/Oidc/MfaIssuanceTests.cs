using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OtpNet;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Slow.Oidc;

/// <summary>Primary-only authentication cannot issue or use user credentials while MFA is active.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class MfaIssuanceTests(TerraBaseFixture fixture)
{
    private const string Password = "correct horse battery";
    private const string Audience = "https://api.terrascale.tech";
    private const string ServiceId = "01M4EQ2FRJ8S8B29E8TQVWNW4K";
    private const string ServiceSecret = "service-test-secret-0123456789abcdefghij";
    private static readonly string[] ReadScopes = ["documents:read"];

    [Test]
    public async Task Active_totp_sends_hosted_password_code_and_existing_email_sign_up_to_the_second_factor()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await StartAsync(clock);
        var user = await CreateUserAsync(oidc);
        var active = await ActivateAsync(oidc.Server, user, clock);

        using var password = await PasswordAsync(oidc, user.Email, Password);
        await SecondFactorStepAsync(password);
        using (var wrong = await PasswordAsync(oidc, user.Email, "a wrong password"))
        {
            await RefusedAsync(wrong, HttpStatusCode.Unauthorized);
        }

        using (var code = await CodeSignInAsync(oidc, user.Email))
        {
            await SecondFactorStepAsync(code);
        }

        clock.UtcNow += HostedAuth.MailCooldown;
        using var signUp = await ExistingSignUpAsync(oidc, user.Email);
        await SecondFactorStepAsync(signUp);
        await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.SessionCollection)).Items.Count).IsEqualTo(0);
        await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items.Count).IsEqualTo(0);
        var current = (await oidc.Server.App.Services.GetRequiredService<Users>().GetAsync(user.InstanceId, user.Id))!;
        await Assert.That(current.AuthEpoch).IsEqualTo(active.User.AuthEpoch);
        await Assert.That(current.PasswordHash).IsEqualTo(user.PasswordHash);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Active_totp_refuses_pre_activation_and_current_epoch_primary_artifacts_on_another_replica(bool currentEpoch)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await StartAsync(clock);
        var user = await CreateUserAsync(oidc);
        PrimaryArtifacts artifacts;
        IssuedRecoveryCodes active;
        if (currentEpoch)
        {
            active = await ActivateAsync(oidc.Server, user, clock);
            var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
            var factor = (await store.GetAsync(AuthStore.MfaCollection, user.InstanceId, user.Id))!;
            var record = factor.Document.Deserialize(MfaJson.Default.TotpCredentialRecord)!;

            // Real-store fixture: issue primary artifacts at the advanced epoch with a disabled factor, then restore it.
            var disabled = record with { Enabled = false, SecretBox = null, EnrollmentExpiresAt = null, LastAcceptedStep = -1, RecoveryHashes = [], RecoveryExpiresAt = null };
            await Assert.That(await store.CompareAndPutAsync(AuthStore.MfaCollection, user.InstanceId, user.Id,
                JsonSerializer.SerializeToElement(disabled, MfaJson.Default.TotpCredentialRecord), factor.Version)).IsTrue();
            artifacts = await ArtifactsAsync(oidc, active.User);
            var changed = (await store.GetAsync(AuthStore.MfaCollection, user.InstanceId, user.Id))!;
            await Assert.That(await store.CompareAndPutAsync(AuthStore.MfaCollection, user.InstanceId, user.Id, factor.Document, changed.Version)).IsTrue();
        }
        else
        {
            artifacts = await ArtifactsAsync(oidc, user);
            active = await ActivateAsync(oidc.Server, user, clock);
        }

        var epoch = new JsonWebToken(artifacts.AccessToken).GetClaim(OidcIssuer.EpochClaim).Value;
        await Assert.That(epoch).IsEqualTo((currentEpoch ? active.User.AuthEpoch : user.AuthEpoch).ToString(CultureInfo.InvariantCulture));
        await Assert.That((await oidc.Server.App.Services.GetRequiredService<Users>().GetAsync(user.InstanceId, user.Id))!.AuthEpoch).IsEqualTo(active.User.AuthEpoch);
        var session = (await oidc.Server.App.Services.GetRequiredService<AuthStore>()
            .GetAsync(AuthStore.SessionCollection, user.InstanceId, Tokens.Sha256Hex(artifacts.Session)))!.Document.Deserialize(IdentityJson.Default.SessionRecord)!;
        await Assert.That(session.AuthEpoch).IsEqualTo(currentEpoch ? active.User.AuthEpoch : user.AuthEpoch);

        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        await ArtifactsRefusedAsync(oidc, artifacts, HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest, replica.Client);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Pending_and_disabled_factors_allow_the_existing_primary_authentication_flows(bool disabled)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await StartAsync(clock);
        var user = await CreateUserAsync(oidc);
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        if (disabled)
        {
            var active = await ActivateAsync(oidc.Server, user, clock);
            await Assert.That(await mfa.DisableAsync(active.User, active.Codes[0])).IsTrue();
            user = (await oidc.Server.App.Services.GetRequiredService<Users>().GetAsync(user.InstanceId, user.Id))!;
        }
        else
        {
            await Assert.That(await mfa.BeginAsync(user)).IsNotNull();
        }

        await Assert.That(await PasswordAccessTokenAsync(oidc, user.Email)).IsNotEmpty();
        using (var email = await CodeSignInAsync(oidc, user.Email))
        {
            await Assert.That(email.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(OidcHarness.SessionCookie(email)).IsNotNull();
        }

        clock.UtcNow += HostedAuth.MailCooldown;
        var artifacts = await ArtifactsAsync(oidc, user);
        using (var userInfo = await oidc.UserInfoAsync(artifacts.AccessToken))
        {
            await Assert.That(userInfo.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var exchange = await oidc.ExchangeAsync(artifacts.AccessToken, new() { ["organization"] = artifacts.Organization }))
        {
            await Assert.That(exchange.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var code = await oidc.RedeemCodeAsync(artifacts.Code, artifacts.Verifier))
        {
            await Assert.That(code.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var refresh = await oidc.RefreshAsync(artifacts.RefreshToken))
        {
            await Assert.That(refresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        clock.UtcNow += HostedAuth.MailCooldown;
        using var existing = await ExistingSignUpAsync(oidc, user.Email);
        await Assert.That(existing.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(OidcHarness.SessionCookie(existing)).IsNotNull();
        await Assert.That((await oidc.Server.App.Services.GetRequiredService<Users>().GetAsync(user.InstanceId, user.Id))!.PasswordHash).IsEqualTo(user.PasswordHash);
    }

    [Test]
    public async Task Password_reset_advances_the_epoch_without_disabling_or_replacing_the_active_factor()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await StartAsync(clock);
        var user = await CreateUserAsync(oidc);
        var active = await ActivateAsync(oidc.Server, user, clock);
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var before = (await store.GetAsync(AuthStore.MfaCollection, user.InstanceId, user.Id))!;
        const string newPassword = "a different valid password";
        using (var reset = await ResetAsync(oidc, user.Email, newPassword))
        {
            await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(OidcHarness.SessionCookie(reset)).IsNull();
            await Assert.That(await reset.Content.ReadAsStringAsync()).Contains("<h1>Password updated</h1>");
        }

        var current = (await oidc.Server.App.Services.GetRequiredService<Users>().GetAsync(user.InstanceId, user.Id))!;
        await Assert.That(current.AuthEpoch).IsEqualTo(active.User.AuthEpoch + 1);
        await Assert.That(Passwords.Verify(newPassword, current.PasswordHash!)).IsTrue();
        var after = (await store.GetAsync(AuthStore.MfaCollection, user.InstanceId, user.Id))!;
        await Assert.That(after.Version).IsEqualTo(before.Version);
        await Assert.That(after.Document.GetRawText()).IsEqualTo(before.Document.GetRawText());
        await Assert.That(after.Document.GetProperty("enabled").GetBoolean()).IsTrue();
        using var password = await PasswordAsync(oidc, user.Email, newPassword);
        await SecondFactorStepAsync(password);
    }

    [Test]
    [Arguments("ownership")]
    [Arguments("ciphertext")]
    [Arguments("schema")]
    [Arguments("unavailable")]
    public async Task Invalid_or_unavailable_inactive_factor_state_refuses_primary_authentication_and_current_epoch_artifacts(string failure)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await StartAsync(clock);
        var user = await CreateUserAsync(oidc);
        var artifacts = await ArtifactsAsync(oidc, user);
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        await Assert.That(await mfa.BeginAsync(user)).IsNotNull();
        var passcodes = oidc.Server.App.Services.GetRequiredService<Passcodes>();

        if (failure == "unavailable")
        {
            await oidc.Database.DropCollectionAsync(AuthStore.MfaCollection);
        }
        else
        {
            var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
            var factor = (await store.GetAsync(AuthStore.MfaCollection, user.InstanceId, user.Id))!;
            var corrupted = JsonNode.Parse(factor.Document.GetRawText())!.AsObject();
            switch (failure)
            {
                case "ownership":
                    corrupted["instanceId"] = oidc.Tenant.Project.Id;
                    break;
                case "ciphertext":
                    corrupted["secretBox"] = "invalid-ciphertext";
                    break;
                case "schema":
                    corrupted["enabled"] = "false";
                    break;
            }

            await Assert.That(await store.CompareAndPutAsync(AuthStore.MfaCollection, user.InstanceId, user.Id,
                JsonSerializer.SerializeToElement(corrupted), factor.Version)).IsTrue();
        }

        using (var password = await PasswordAsync(oidc, user.Email, Password))
        {
            await RefusedAsync(password, HttpStatusCode.ServiceUnavailable);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(password)).IsEqualTo("temporarily_unavailable");
        }

        var hostedCode = (await passcodes.RequestAsync(user.InstanceId, user.Email))!;
        using (var hosted = await oidc.PostFormAsync("/ui/terrascale/sign-in/verify", new() { ["email"] = user.Email, ["code"] = hostedCode.Code }))
        {
            await RefusedAsync(hosted, HttpStatusCode.ServiceUnavailable);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(hosted)).IsEqualTo("temporarily_unavailable");
        }

        // Exercise the factor guard before the mail cooldown advance expires the one-minute authorization code.
        await ArtifactsRefusedAsync(oidc, artifacts, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        clock.UtcNow += HostedAuth.MailCooldown;
        using (var signUp = await ExistingSignUpAsync(oidc, user.Email))
        {
            await RefusedAsync(signUp, HttpStatusCode.ServiceUnavailable);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(signUp)).IsEqualTo("temporarily_unavailable");
        }

        await Assert.That((await oidc.Server.App.Services.GetRequiredService<Users>().GetAsync(user.InstanceId, user.Id))!.AuthEpoch).IsEqualTo(user.AuthEpoch);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UserInfo_refuses_tokens_after_global_revoke_and_password_reset_without_any_factor(bool passwordReset)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await StartAsync(clock);
        var user = await CreateUserAsync(oidc);
        var access = await PasswordAccessTokenAsync(oidc, user.Email);
        using (var before = await oidc.UserInfoAsync(access))
        {
            await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await OidcHarness.JsonAsync(before)).GetProperty("sub").GetString()).IsEqualTo(user.Id);
        }

        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        if (passwordReset)
        {
            using var reset = await ResetAsync(oidc, user.Email, "a different valid password");
            await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        else
        {
            await Assert.That(await replica.App.Services.GetRequiredService<Sessions>().RevokeAllAsync(user.InstanceId, user.Id)).IsTrue();
        }

        var current = (await oidc.Server.App.Services.GetRequiredService<Users>().GetAsync(user.InstanceId, user.Id))!;
        await Assert.That(current.AuthEpoch).IsEqualTo(user.AuthEpoch + 1);
        await Assert.That(new JsonWebToken(access).GetClaim(OidcIssuer.EpochClaim).Value).IsEqualTo(user.AuthEpoch.ToString(CultureInfo.InvariantCulture));
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<AuthStore>().GetAsync(AuthStore.MfaCollection, user.InstanceId, user.Id)).IsNull();

        foreach (var client in new[] { oidc.Client, replica.Client })
        {
            using var refused = await oidc.UserInfoAsync(access, client);
            await RefusedAsync(refused, HttpStatusCode.Unauthorized);
            var body = await refused.Content.ReadAsStringAsync();
            await Assert.That(body.Contains("\"sub\"", StringComparison.Ordinal)).IsFalse();
            await Assert.That(body.Contains(user.Email, StringComparison.Ordinal)).IsFalse();
        }
    }

    [Test]
    [Arguments("password", "activation")]
    [Arguments("password", "revoke")]
    [Arguments("userinfo", "activation")]
    [Arguments("userinfo", "revoke")]
    public async Task A_primary_request_refuses_replica_activation_or_revocation_during_its_factor_read(string path, string change)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        using var pause = new PausingTransactions(request => request.Method == HttpMethod.Get
            && request.RequestUri!.AbsolutePath.Contains($"/collections/{AuthStore.MfaCollection}/documents/", StringComparison.Ordinal))
        { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(pause);
        await using var oidc = await StartAsync(clock, services => services.AddSingleton(http));
        var user = await CreateUserAsync(oidc);
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<TotpCredentials>().BeginAsync(user)).IsNotNull();
        string? access = null;
        if (path == "userinfo")
        {
            access = await PasswordAccessTokenAsync(oidc, user.Email);
        }

        var tokenCount = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items.Count;
        var sessionCount = (await oidc.Database.ListDocumentsAsync(AuthStore.SessionCollection)).Items.Count;
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var held = await pause.HoldNextAsync(() => path == "password"
            ? PasswordAsync(oidc, user.Email, Password)
            : oidc.UserInfoAsync(access!));
        if (change == "activation")
        {
            await ActivateAsync(replica, user, clock);
        }
        else
        {
            await Assert.That(await replica.App.Services.GetRequiredService<Sessions>().RevokeAllAsync(user.InstanceId, user.Id)).IsTrue();
        }

        using var refused = await pause.ReleaseAsync(held);
        await RefusedAsync(refused, HttpStatusCode.Unauthorized);
        if (path == "userinfo")
        {
            var body = await refused.Content.ReadAsStringAsync();
            await Assert.That(body.Contains("\"sub\"", StringComparison.Ordinal)).IsFalse();
            await Assert.That(body.Contains(user.Email, StringComparison.Ordinal)).IsFalse();
        }

        await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items.Count).IsEqualTo(tokenCount);
        if (path == "password")
        {
            await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.SessionCollection)).Items.Count).IsEqualTo(sessionCount);
        }
    }

    [Test]
    public async Task Activating_a_users_factor_revokes_their_api_keys_but_keeps_service_and_other_users_keys()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await StartAsync(clock);
        var user = await CreateUserAsync(oidc);
        var artifacts = await ArtifactsAsync(oidc, user);
        var created = await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{artifacts.Organization}/api-keys", artifacts.AccessToken,
            new { name = "backend", scopes = ReadScopes }, HttpStatusCode.Created);
        var key = created.GetProperty("key").GetString()!;
        using (var before = await oidc.ApiKeyTokenAsync(key))
        {
            await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var other = await oidc.CreateUserAsync("bob@example.test");
        var otherArtifacts = await ArtifactsAsync(oidc, other);
        var otherCreated = await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{otherArtifacts.Organization}/api-keys", otherArtifacts.AccessToken,
            new { name = "other backend", scopes = ReadScopes }, HttpStatusCode.Created);
        await ActivateAsync(oidc.Server, user, clock);

        foreach (var unavailable in new[] { false, true })
        {
            if (unavailable)
            {
                await oidc.Database.DropCollectionAsync(AuthStore.MfaCollection);
            }

            using var service = await oidc.PostTokenAsync(new() { ["grant_type"] = "client_credentials" }, ServiceId, ServiceSecret);
            await Assert.That(service.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var serviceTokens = await OidcHarness.JsonAsync(service);
            var serviceAccess = await oidc.ValidateAsync(serviceTokens.GetProperty("access_token").GetString()!, Audience);
            await Assert.That(serviceAccess.Subject).IsEqualTo(ServiceId);
            await Assert.That(serviceTokens.TryGetProperty("id_token", out _)).IsFalse();
            await Assert.That(serviceTokens.TryGetProperty("refresh_token", out _)).IsFalse();

            // MFA activation changes the fresh account-state lineage retained by this user's issued key.
            using var refused = await oidc.ApiKeyTokenAsync(key);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            var failure = await OidcHarness.JsonAsync(refused);
            await Assert.That(failure.GetProperty("error").GetString()).IsEqualTo("invalid_client");
            await Assert.That(failure.TryGetProperty("access_token", out _)).IsFalse();

            using var apiKey = await oidc.ApiKeyTokenAsync(otherCreated.GetProperty("key").GetString()!);
            await Assert.That(apiKey.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var apiTokens = await OidcHarness.JsonAsync(apiKey);
            var apiAccess = await oidc.ValidateAsync(apiTokens.GetProperty("access_token").GetString()!, Audience);
            await Assert.That(apiAccess.Subject).IsEqualTo(otherCreated.GetProperty("api_key").GetProperty("id").GetString()!);
            await Assert.That(apiAccess.GetClaim("org_id").Value).IsEqualTo(otherArtifacts.Organization);
            await Assert.That(apiAccess.GetClaim("scope").Value).IsEqualTo("documents:read");
            await Assert.That(apiTokens.TryGetProperty("id_token", out _)).IsFalse();
            await Assert.That(apiTokens.TryGetProperty("refresh_token", out _)).IsFalse();
        }
    }

    private Task<OidcHarness> StartAsync(StoreTestClock clock, Action<IServiceCollection>? services = null) => OidcHarness.StartAsync(fixture, values =>
    {
        values["access-token-audiences"] = Audience;
        values["service-clients"] = $"{ServiceId}:{ServiceSecret}";
    }, clock: clock, services: services);

    private static Task<HttpResponseMessage> PasswordAsync(OidcHarness oidc, string email, string password) =>
        oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in/password", new() { ["email"] = email, ["password"] = password });

    /// <summary>Signs in on the hosted password page and redeems an authorization code for the session.</summary>
    private static async Task<string> PasswordAccessTokenAsync(OidcHarness oidc, string email)
    {
        using var signIn = await PasswordAsync(oidc, email, Password);
        await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var (verifier, challenge) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(OidcHarness.SessionCookie(signIn)!, challenge), verifier);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return (await OidcHarness.JsonAsync(redeemed)).GetProperty("access_token").GetString()!;
    }

    /// <summary>Requests a hosted sign-in code and posts it to the verify page.</summary>
    private static async Task<HttpResponseMessage> CodeSignInAsync(OidcHarness oidc, string email)
    {
        using (var start = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in", new() { ["email"] = email }))
        {
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        return await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in/verify", new() { ["email"] = email, ["code"] = await oidc.Email.NextCodeAsync(email) });
    }

    /// <summary>Signs up on the hosted page with an address that has an account, then posts the emailed sign-in code.</summary>
    private static async Task<HttpResponseMessage> ExistingSignUpAsync(OidcHarness oidc, string email)
    {
        using var start = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up", new() { ["email"] = email, ["password"] = "another valid password" });
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var pending = Regex.Match(await start.Content.ReadAsStringAsync(), "name=\"pending_token\" value=\"([^\"]+)\"").Groups[1].Value;
        await Assert.That(pending).IsNotEmpty();
        return await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up/verify", new()
        {
            ["email"] = email,
            ["code"] = await oidc.Email.NextCodeAsync(email),
            ["pending_token"] = pending,
        });
    }

    /// <summary>Resets the password on the hosted reset pages with the emailed code.</summary>
    private static async Task<HttpResponseMessage> ResetAsync(OidcHarness oidc, string email, string password)
    {
        using (var start = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset", new() { ["email"] = email }))
        {
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        return await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/reset/complete", new()
        {
            ["email"] = email,
            ["code"] = await oidc.Email.NextCodeAsync(email),
            ["password"] = password,
        });
    }

    private static async Task<UserRecord> CreateUserAsync(OidcHarness oidc) =>
        (await oidc.Server.App.Services.GetRequiredService<Users>()
            .CreateAsync(OidcIssuer.InstanceId, "alice@example.test", Passwords.Hash(Password), CancellationToken.None))!;

    private static async Task<IssuedRecoveryCodes> ActivateAsync(AuthTestServer server, UserRecord user, StoreTestClock clock)
    {
        var mfa = server.App.Services.GetRequiredService<TotpCredentials>();
        var enrollment = (await mfa.BeginAsync(user))!;
        var secret = Base32Encoding.ToBytes(enrollment.Secret);
        try
        {
            var active = await mfa.ConfirmAsync(user, new Totp(secret).ComputeTotp(clock.UtcNow.UtcDateTime));
            await Assert.That(active).IsNotNull();
            return active!;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static async Task<PrimaryArtifacts> ArtifactsAsync(OidcHarness oidc, UserRecord user)
    {
        var session = await oidc.SignInAsync(user.Email);
        var (redeemedVerifier, redeemedChallenge) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, redeemedChallenge), redeemedVerifier);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(redeemed);
        var access = tokens.GetProperty("access_token").GetString()!;
        var organization = await oidc.CreateOrganizationAsync(access);
        var (verifier, challenge) = OidcHarness.Pkce();
        var code = await oidc.CodeAsync(session, challenge);
        return new(session, code, verifier, access, tokens.GetProperty("refresh_token").GetString()!, organization);
    }

    private static async Task ArtifactsRefusedAsync(OidcHarness oidc, PrimaryArtifacts artifacts, HttpStatusCode accessStatus, HttpStatusCode grantStatus, HttpClient? client = null)
    {
        var (_, challenge) = OidcHarness.Pkce();
        using (var request = new HttpRequestMessage(HttpMethod.Get, OidcHarness.AuthorizePath(challenge, extra: "&prompt=none")))
        {
            request.Headers.Add("Cookie", $"tsauth_session={artifacts.Session}");
            using var authorize = await (client ?? oidc.Client).SendAsync(request);
            await Assert.That(OidcHarness.SessionCookie(authorize)).IsNull();
            await Assert.That(authorize.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(authorize.Headers.Location!.GetLeftPart(UriPartial.Path)).IsEqualTo(OidcHarness.RedirectUri);
            var query = OidcHarness.Query(authorize.Headers.Location!);
            await Assert.That(query.ContainsKey("code")).IsFalse();
            await Assert.That(query["error"]).IsEqualTo(accessStatus == HttpStatusCode.ServiceUnavailable ? "temporarily_unavailable" : "login_required");
            await Assert.That(query["state"]).IsEqualTo("state-1");
        }

        using (var userInfo = await oidc.UserInfoAsync(artifacts.AccessToken, client))
        {
            await RefusedAsync(userInfo, accessStatus);
            await Assert.That(await userInfo.Content.ReadAsStringAsync()).DoesNotContain("alice@example.test");
            await Assert.That(userInfo.Headers.CacheControl?.NoStore).IsTrue();
            await Assert.That(userInfo.Headers.Pragma.Any(header => header.Name == "no-cache")).IsTrue();
            if (accessStatus == HttpStatusCode.ServiceUnavailable)
            {
                await Assert.That(await OidcHarness.ErrorAsync(userInfo)).IsEqualTo("temporarily_unavailable");
            }
        }

        using (var management = await oidc.CallAsync(HttpMethod.Get, Organizations, artifacts.AccessToken, client: client))
        {
            await RefusedAsync(management, accessStatus);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(management))
                .IsEqualTo(accessStatus == HttpStatusCode.ServiceUnavailable ? "temporarily_unavailable" : "unauthorized");
        }

        using (var exchange = await oidc.ExchangeAsync(artifacts.AccessToken, new() { ["organization"] = artifacts.Organization }, client))
        {
            await RefusedAsync(exchange, grantStatus);
            if (grantStatus == HttpStatusCode.ServiceUnavailable)
            {
                await Assert.That(exchange.Headers.CacheControl?.NoStore).IsTrue();
                await Assert.That(exchange.Headers.Pragma.Any(header => header.Name == "no-cache")).IsTrue();
            }

            await Assert.That(await OidcHarness.ErrorAsync(exchange))
                .IsEqualTo(grantStatus == HttpStatusCode.ServiceUnavailable ? "temporarily_unavailable" : "invalid_grant");
        }

        using (var code = await oidc.RedeemCodeAsync(artifacts.Code, artifacts.Verifier, client: client))
        {
            await RefusedAsync(code, grantStatus);
            if (grantStatus == HttpStatusCode.ServiceUnavailable)
            {
                await Assert.That(code.Headers.CacheControl?.NoStore).IsTrue();
                await Assert.That(code.Headers.Pragma.Any(header => header.Name == "no-cache")).IsTrue();
            }

            await Assert.That(await OidcHarness.ErrorAsync(code))
                .IsEqualTo(grantStatus == HttpStatusCode.ServiceUnavailable ? "temporarily_unavailable" : "invalid_grant");
        }

        using var refresh = await oidc.RefreshAsync(artifacts.RefreshToken, client);
        await RefusedAsync(refresh, grantStatus);
        if (grantStatus == HttpStatusCode.ServiceUnavailable)
        {
            await Assert.That(refresh.Headers.CacheControl?.NoStore).IsTrue();
            await Assert.That(refresh.Headers.Pragma.Any(header => header.Name == "no-cache")).IsTrue();
        }

        await Assert.That(await OidcHarness.ErrorAsync(refresh))
            .IsEqualTo(grantStatus == HttpStatusCode.ServiceUnavailable ? "temporarily_unavailable" : "invalid_grant");
    }

    /// <summary>The primary credential was right: the page asks for the second factor and opens no session yet.</summary>
    private static async Task SecondFactorStepAsync(HttpResponseMessage response)
    {
        await RefusedAsync(response, HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("name=\"challenge\"");
    }

    private static async Task RefusedAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That(OidcHarness.SessionCookie(response)).IsNull();
        var body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).DoesNotContain("access_token");
        await Assert.That(body).DoesNotContain("refresh_token");
        await Assert.That(body).DoesNotContain("id_token");
    }

    private sealed record PrimaryArtifacts(string Session, string Code, string Verifier, string AccessToken, string RefreshToken, string Organization);
}
