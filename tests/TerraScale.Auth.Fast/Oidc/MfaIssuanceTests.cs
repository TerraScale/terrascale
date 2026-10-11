using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OtpNet;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.Headless;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Mfa;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Security;
using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;
using static TerraScale.Auth.Tests.PlatformCalls;

namespace TerraScale.Auth.Fast.Oidc;

/// <summary>Primary-only authentication cannot issue or use user credentials while MFA is active.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class MfaIssuanceTests(TerraBaseFixture fixture)
{
    private const string Password = "correct horse battery";
    private const string Ip = "203.0.113.7";
    private const string Audience = "https://gru.api.terrascale.tech";
    private const string ServiceId = "01M4EQ2FRJ8S8B29E8TQVWNW4K";
    private const string ServiceSecret = "service-test-secret-0123456789abcdefghij";
    private static readonly string[] ReadScopes = ["documents:read"];

    [Test]
    public async Task Active_totp_refuses_password_email_code_existing_email_sign_up_and_hosted_sign_in()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await StartAsync(clock);
        var user = await CreateUserAsync(oidc);
        var active = await ActivateAsync(oidc.Server, user, clock);

        using var password = await oidc.GrantAsync(HeadlessGrants.Password, user.Email, Password, Ip);
        await RefusedAsync(password, HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(password)).IsEqualTo("invalid_grant");
        using var unknown = await oidc.GrantAsync(HeadlessGrants.Password, "unknown@example.test", Password, Ip);
        await Assert.That(await password.Content.ReadAsStringAsync()).IsEqualTo(await unknown.Content.ReadAsStringAsync());

        using (var start = await oidc.HeadlessAsync("email-code/start", new { email = user.Email, ip_address = Ip }))
        {
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }

        using (var email = await oidc.GrantAsync(HeadlessGrants.EmailCode, user.Email, await oidc.Email.NextCodeAsync(user.Email), Ip))
        {
            await RefusedAsync(email, HttpStatusCode.BadRequest);
            await Assert.That(await OidcHarness.ErrorAsync(email)).IsEqualTo("invalid_grant");
        }

        clock.UtcNow += HeadlessAuth.MailCooldown;
        using (var signUp = await oidc.HeadlessAsync("sign-up", new { email = user.Email, password = "another valid password", ip_address = Ip }))
        {
            await Assert.That(signUp.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            var pending = (await OidcHarness.JsonAsync(signUp)).GetProperty("pending_token").GetString()!;
            using var email = await oidc.GrantAsync(HeadlessGrants.EmailCode, user.Email, await oidc.Email.NextCodeAsync(user.Email), Ip, pendingToken: pending);
            await RefusedAsync(email, HttpStatusCode.BadRequest);
            await Assert.That(await OidcHarness.ErrorAsync(email)).IsEqualTo("invalid_grant");
        }

        clock.UtcNow += HeadlessAuth.MailCooldown;
        using (var start = await oidc.PostFormAsync("/ui/terrascale/sign-in", new() { ["email"] = user.Email }))
        {
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using var hosted = await oidc.PostFormAsync("/ui/terrascale/sign-in/verify", new()
        {
            ["email"] = user.Email,
            ["code"] = await oidc.Email.NextCodeAsync(user.Email),
        });
        await RefusedAsync(hosted, HttpStatusCode.Unauthorized);
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

        using (var password = await oidc.GrantAsync(HeadlessGrants.Password, user.Email, Password, Ip))
        {
            await Assert.That(password.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await OidcHarness.JsonAsync(password)).TryGetProperty("access_token", out _)).IsTrue();
        }

        using (var start = await oidc.HeadlessAsync("email-code/start", new { email = user.Email, ip_address = Ip }))
        {
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }

        using (var email = await oidc.GrantAsync(HeadlessGrants.EmailCode, user.Email, await oidc.Email.NextCodeAsync(user.Email), Ip))
        {
            await Assert.That(email.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        clock.UtcNow += HeadlessAuth.MailCooldown;
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

        clock.UtcNow += HeadlessAuth.MailCooldown;
        using var signUp = await oidc.HeadlessAsync("sign-up", new { email = user.Email, password = "another valid password", ip_address = Ip });
        await Assert.That(signUp.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var pending = (await OidcHarness.JsonAsync(signUp)).GetProperty("pending_token").GetString()!;
        using var existing = await oidc.GrantAsync(HeadlessGrants.EmailCode, user.Email, await oidc.Email.NextCodeAsync(user.Email), Ip, pendingToken: pending);
        await Assert.That(existing.StatusCode).IsEqualTo(HttpStatusCode.OK);
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
        using (var start = await oidc.HeadlessAsync("password-reset/start", new { email = user.Email, ip_address = Ip }))
        {
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        }

        const string newPassword = "a different valid password";
        using (var reset = await oidc.HeadlessAsync("password-reset/complete", new
        {
            email = user.Email,
            code = await oidc.Email.NextCodeAsync(user.Email),
            new_password = newPassword,
            ip_address = Ip,
        }))
        {
            await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await Assert.That(OidcHarness.SessionCookie(reset)).IsNull();
            await Assert.That(await reset.Content.ReadAsStringAsync()).IsEmpty();
        }

        var current = (await oidc.Server.App.Services.GetRequiredService<Users>().GetAsync(user.InstanceId, user.Id))!;
        await Assert.That(current.AuthEpoch).IsEqualTo(active.User.AuthEpoch + 1);
        await Assert.That(Passwords.Verify(newPassword, current.PasswordHash!)).IsTrue();
        var after = (await store.GetAsync(AuthStore.MfaCollection, user.InstanceId, user.Id))!;
        await Assert.That(after.Version).IsEqualTo(before.Version);
        await Assert.That(after.Document.GetRawText()).IsEqualTo(before.Document.GetRawText());
        await Assert.That(after.Document.GetProperty("enabled").GetBoolean()).IsTrue();
        using var password = await oidc.GrantAsync(HeadlessGrants.Password, user.Email, newPassword, Ip);
        await RefusedAsync(password, HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(password)).IsEqualTo("invalid_grant");
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
        var headlessCode = (await passcodes.RequestAsync(user.InstanceId, user.Email))!;

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

        using (var password = await oidc.GrantAsync(HeadlessGrants.Password, user.Email, Password, Ip))
        {
            await RefusedAsync(password, HttpStatusCode.ServiceUnavailable);
            await Assert.That(password.Headers.CacheControl?.NoStore).IsTrue();
            await Assert.That(password.Headers.Pragma.Any(header => header.Name == "no-cache")).IsTrue();
            await Assert.That(await OidcHarness.ErrorAsync(password)).IsEqualTo("temporarily_unavailable");
        }

        using (var email = await oidc.GrantAsync(HeadlessGrants.EmailCode, user.Email, headlessCode.Code, Ip))
        {
            await RefusedAsync(email, HttpStatusCode.ServiceUnavailable);
            await Assert.That(email.Headers.CacheControl?.NoStore).IsTrue();
            await Assert.That(email.Headers.Pragma.Any(header => header.Name == "no-cache")).IsTrue();
            await Assert.That(await OidcHarness.ErrorAsync(email)).IsEqualTo("temporarily_unavailable");
        }

        var hostedCode = (await passcodes.RequestAsync(user.InstanceId, user.Email))!;
        using (var hosted = await oidc.PostFormAsync("/ui/terrascale/sign-in/verify", new() { ["email"] = user.Email, ["code"] = hostedCode.Code }))
        {
            await RefusedAsync(hosted, HttpStatusCode.ServiceUnavailable);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(hosted)).IsEqualTo("temporarily_unavailable");
        }

        // Exercise the factor guard before the mail cooldown advance expires the one-minute authorization code.
        await ArtifactsRefusedAsync(oidc, artifacts, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        clock.UtcNow += HeadlessAuth.MailCooldown;
        using (var signUp = await oidc.HeadlessAsync("sign-up", new { email = user.Email, password = "another valid password", ip_address = Ip }))
        {
            await Assert.That(signUp.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            var pending = (await OidcHarness.JsonAsync(signUp)).GetProperty("pending_token").GetString()!;
            using var email = await oidc.GrantAsync(HeadlessGrants.EmailCode, user.Email, await oidc.Email.NextCodeAsync(user.Email), Ip, pendingToken: pending);
            await RefusedAsync(email, HttpStatusCode.ServiceUnavailable);
            await Assert.That(email.Headers.CacheControl?.NoStore).IsTrue();
            await Assert.That(email.Headers.Pragma.Any(header => header.Name == "no-cache")).IsTrue();
            await Assert.That(await OidcHarness.ErrorAsync(email)).IsEqualTo("temporarily_unavailable");
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
        using var signIn = await oidc.GrantAsync(HeadlessGrants.Password, user.Email, Password, Ip);
        await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var access = (await OidcHarness.JsonAsync(signIn)).GetProperty("access_token").GetString()!;
        using (var before = await oidc.UserInfoAsync(access))
        {
            await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await OidcHarness.JsonAsync(before)).GetProperty("sub").GetString()).IsEqualTo(user.Id);
        }

        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        if (passwordReset)
        {
            using var start = await oidc.HeadlessAsync("password-reset/start", new { email = user.Email, ip_address = Ip });
            await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            using var reset = await oidc.HeadlessAsync("password-reset/complete", new
            {
                email = user.Email,
                code = await oidc.Email.NextCodeAsync(user.Email),
                new_password = "a different valid password",
                ip_address = Ip,
            });
            await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
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
        { InnerHandler = fixture.NewHandler() };
        using var http = new HttpClient(pause);
        await using var oidc = await StartAsync(clock, services => services.AddSingleton(http));
        var user = await CreateUserAsync(oidc);
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<TotpCredentials>().BeginAsync(user)).IsNotNull();
        string? access = null;
        if (path == "userinfo")
        {
            using var signIn = await oidc.GrantAsync(HeadlessGrants.Password, user.Email, Password, Ip);
            await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
            access = (await OidcHarness.JsonAsync(signIn)).GetProperty("access_token").GetString()!;
        }

        var tokenCount = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items.Count;
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var held = await pause.HoldNextAsync(() => path == "password"
            ? oidc.GrantAsync(HeadlessGrants.Password, user.Email, Password, Ip)
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
        await RefusedAsync(refused, path == "password" ? HttpStatusCode.BadRequest : HttpStatusCode.Unauthorized);
        if (path == "password")
        {
            await Assert.That(await OidcHarness.ErrorAsync(refused)).IsEqualTo("invalid_grant");
        }
        else
        {
            var body = await refused.Content.ReadAsStringAsync();
            await Assert.That(body.Contains("\"sub\"", StringComparison.Ordinal)).IsFalse();
            await Assert.That(body.Contains(user.Email, StringComparison.Ordinal)).IsFalse();
        }

        await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items.Count).IsEqualTo(tokenCount);
    }

    [Test]
    public async Task Activating_a_users_factor_preserves_service_and_api_key_client_credentials()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await StartAsync(clock);
        var user = await CreateUserAsync(oidc);
        var artifacts = await ArtifactsAsync(oidc, user);
        var created = await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{artifacts.Organization}/api-keys", artifacts.AccessToken,
            new { name = "backend", scopes = ReadScopes }, HttpStatusCode.Created);
        var key = created.GetProperty("key").GetString()!;
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

            using var apiKey = await oidc.ApiKeyTokenAsync(key);
            await Assert.That(apiKey.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var apiTokens = await OidcHarness.JsonAsync(apiKey);
            var apiAccess = await oidc.ValidateAsync(apiTokens.GetProperty("access_token").GetString()!, Audience);
            await Assert.That(apiAccess.Subject).IsEqualTo(created.GetProperty("api_key").GetProperty("id").GetString()!);
            await Assert.That(apiAccess.GetClaim("org_id").Value).IsEqualTo(artifacts.Organization);
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
            if (accessStatus == HttpStatusCode.ServiceUnavailable)
            {
                await Assert.That(userInfo.Headers.CacheControl?.NoStore).IsTrue();
                await Assert.That(userInfo.Headers.Pragma.Any(header => header.Name == "no-cache")).IsTrue();
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
