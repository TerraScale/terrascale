using System.Net;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogus;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OtpNet;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Social;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.SignIn;

/// <summary>Hosted provider sign-in through HTTP, real TerraBase and a controlled provider backchannel.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class SocialSignInTests(TerraBaseFixture fixture)
{
    [Test]
    [Arguments("github", false)]
    [Arguments("google", false)]
    [Arguments("github", true)]
    [Arguments("google", true)]
    public async Task Bound_subject_keeps_its_local_identity_when_verified_email_changes(string provider, bool collision)
    {
        using var fake = new Provider(provider);
        await using var oidc = await OidcHarness.StartAsync(fixture, Credentials, services: fake.Route);
        var page = await oidc.Client.GetStringAsync("/ui/terrascale/sign-up");
        await Assert.That(page).Contains("Continue with GitHub");
        await Assert.That(page).Contains("Continue with Google");
        var next = "/terrascale/oauth/authorize?client_id=dashboard";
        var start = await StartAsync(oidc.Client, provider, next);
        var authorization = QueryHelpers.ParseQuery(start.Location.Query);
        await Assert.That(authorization["code_challenge_method"].ToString()).IsEqualTo("S256");
        await Assert.That(authorization["redirect_uri"].ToString()).IsEqualTo($"{OidcHarness.PublicOrigin}/signin/{provider}");
        using var response = await CompleteAsync(oidc.Client, provider, start);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(OidcHarness.SessionCookie(response)).IsNotNull();
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Continue");
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var user = (await users.FindByEmailAsync(OidcIssuer.InstanceId, fake.Email))!;
        await Assert.That(user.LinkedProviders.Count).IsEqualTo(1);
        await Assert.That(WebEncoders.Base64UrlEncode(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(fake.Verifier!)))).IsEqualTo(authorization["code_challenge"].ToString());
        var originalEmail = fake.Email;
        fake.Email = new Faker().Internet.Email();
        var other = collision ? await ActivateAsync(oidc, (await users.CreateAsync(OidcIssuer.InstanceId, fake.Email))!) : null;
        var second = await StartAsync(oidc.Client, provider);
        using var signedIn = await CompleteAsync(oidc.Client, provider, second);
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var (verifier, challenge) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(OidcHarness.SessionCookie(signedIn)!, challenge), verifier);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var userInfo = await oidc.UserInfoAsync((await OidcHarness.JsonAsync(redeemed)).GetProperty("access_token").GetString()!);
        var profile = await OidcHarness.JsonAsync(userInfo);
        await Assert.That(profile.GetProperty("sub").GetString()).IsEqualTo(user.Id);
        await Assert.That(profile.GetProperty("email").GetString()).IsEqualTo(Users.NormalizeEmail(originalEmail));
        await UnchangedAsync(users, user);
        await Assert.That((await users.FindByEmailAsync(OidcIssuer.InstanceId, fake.Email))?.Id).IsEqualTo(other?.Id);
        if (other is not null)
        {
            await UnchangedAsync(users, other);
        }

        // Callback replays cannot exchange a second time or issue a session.
        var calls = fake.Exchanges;
        using var replay = await CompleteAsync(oidc.Client, provider, second);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(fake.Exchanges).IsEqualTo(calls);
    }

    [Test]
    [Arguments("github", false)]
    [Arguments("google", false)]
    [Arguments("github", true)]
    [Arguments("google", true)]
    public async Task Unknown_subject_with_existing_email_is_refused_without_attaching_a_credential(string provider, bool alreadyBound)
    {
        using var fake = new Provider(provider);
        await using var oidc = await OidcHarness.StartAsync(fixture, Credentials, services: fake.Route);
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        if (alreadyBound)
        {
            using var initial = await CompleteAsync(oidc.Client, provider, await StartAsync(oidc.Client, provider));
            await Assert.That(initial.StatusCode).IsEqualTo(HttpStatusCode.OK);
            fake.Subject = provider == SocialProviders.GitHub ? "1235" : fake.Subject.ToLowerInvariant();
        }

        var existing = (await users.FindByEmailAsync(OidcIssuer.InstanceId, fake.Email)
            ?? await users.CreateAsync(OidcIssuer.InstanceId, fake.Email))!;
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var sessions = await store.CountAsync(AuthStore.SessionCollection, OidcIssuer.InstanceId);
        var accounts = await store.CountAsync(AuthStore.UserCollection, OidcIssuer.InstanceId);
        var start = await StartAsync(oidc.Client, provider);
        using var response = await CompleteAsync(oidc.Client, provider, start);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(OidcHarness.SessionCookie(response)).IsNull();
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("use your existing sign-in method");
        await UnchangedAsync(users, existing);
        await Assert.That((await users.FindByEmailAsync(OidcIssuer.InstanceId, fake.Email))!.Id).IsEqualTo(existing.Id);
        await Assert.That(await store.CountAsync(AuthStore.UserCollection, OidcIssuer.InstanceId)).IsEqualTo(accounts);
        await Assert.That(await store.CountAsync(AuthStore.SessionCollection, OidcIssuer.InstanceId)).IsEqualTo(sessions);
        // A refused attempt must not reserve the subject for the existing account or an orphan account.
        fake.Email = new Faker().Internet.Email();
        using var fresh = await CompleteAsync(oidc.Client, provider, await StartAsync(oidc.Client, provider));
        await Assert.That(fresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await users.FindByEmailAsync(OidcIssuer.InstanceId, fake.Email))!.Id).IsNotEqualTo(existing.Id);
        await UnchangedAsync(users, existing);
    }

    [Test]
    [Arguments("github")]
    [Arguments("google")]
    public async Task Unverified_email_is_refused_without_creating_an_account(string provider)
    {
        using var fake = new Provider(provider) { Verified = false };
        await using var oidc = await OidcHarness.StartAsync(fixture, Credentials, services: fake.Route);
        var start = await StartAsync(oidc.Client, provider);
        using var response = await CompleteAsync(oidc.Client, provider, start);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(OidcHarness.SessionCookie(response)).IsNull();
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<Users>().FindByEmailAsync(OidcIssuer.InstanceId, fake.Email)).IsNull();
    }

    [Test]
    public async Task State_is_browser_bound_tamper_proof_and_provider_bound()
    {
        using var fake = new Provider("github");
        await using var oidc = await OidcHarness.StartAsync(fixture, Credentials, services: fake.Route);
        var start = await StartAsync(oidc.Client, "github", "https://evil.example");
        using var noCookie = await CompleteAsync(oidc.Client, "github", start with { Cookie = "unrelated=value" });
        using var otherProvider = await CompleteAsync(oidc.Client, "google", start);
        using var tampered = await CompleteAsync(oidc.Client, "github", start with { State = start.State + "x" });
        await Assert.That(noCookie.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(otherProvider.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(tampered.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(fake.Exchanges).IsEqualTo(0);
        using var success = await CompleteAsync(oidc.Client, "github", start);
        await Assert.That(success.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await success.Content.ReadAsStringAsync()).DoesNotContain("evil.example");
    }

    [Test]
    public async Task Another_replica_accepts_state_using_the_same_vault_credentials()
    {
        using var fake = new Provider("google");
        await using var oidc = await OidcHarness.StartAsync(fixture, Credentials, services: fake.Route);
        await using var replica = await oidc.StartReplicaAsync(Credentials, services: fake.Route);
        var start = await StartAsync(oidc.Client, "google");
        using var response = await CompleteAsync(replica.Client, "google", start);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(OidcHarness.SessionCookie(response)).IsNotNull();
    }

    [Test]
    public async Task Absent_credentials_hide_buttons_and_cross_origin_start_is_refused()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await Assert.That(await oidc.Client.GetStringAsync("/ui/terrascale/sign-in")).DoesNotContain("Continue with GitHub");
        await Assert.That(await oidc.Client.GetStringAsync("/ui/terrascale/sign-up")).DoesNotContain("Continue with Google");
        using var form = new HttpRequestMessage(HttpMethod.Post, "/ui/terrascale/social/github") { Content = new FormUrlEncodedContent([]) };
        form.Headers.Add("Origin", "https://evil.example");
        using var response = await oidc.Client.SendAsync(form);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var missing = await StartResponseAsync(oidc.Client, "github");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Expired_state_and_disabled_accounts_never_open_a_session()
    {
        using var fake = new Provider("github");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, Credentials, clock: clock, services: fake.Route);
        var expired = await StartAsync(oidc.Client, "github");
        clock.Advance(TimeSpan.FromMinutes(11));
        using var refused = await CompleteAsync(oidc.Client, "github", expired);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(fake.Exchanges).IsEqualTo(0);
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var user = (await users.CreateAsync(OidcIssuer.InstanceId, fake.Email))!;
        await users.SetDisabledAsync(OidcIssuer.InstanceId, user.Id, true);
        var start = await StartAsync(oidc.Client, "github");
        using var response = await CompleteAsync(oidc.Client, "github", start);
        await Assert.That(OidcHarness.SessionCookie(response)).IsNull();
        await Assert.That((await users.GetAsync(OidcIssuer.InstanceId, user.Id))!.LinkedProviders.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Profile_lists_providers_and_unlinks_only_after_a_fresh_email_code()
    {
        using var fake = new Provider("github");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, Credentials, services: fake.Route, clock: clock);
        var start = await StartAsync(oidc.Client, "github");
        using var signedIn = await CompleteAsync(oidc.Client, "github", start);
        var session = OidcHarness.SessionCookie(signedIn)!;
        var (verifier, challenge) = OidcHarness.Pkce();
        using var tokenResponse = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, challenge), verifier);
        var tokens = await OidcHarness.JsonAsync(tokenResponse);
        var access = tokens.GetProperty("access_token").GetString()!;
        const string path = "/v1/terrascale/account/providers";
        var body = await oidc.OkAsync(HttpMethod.Get, path, access);
        await Assert.That(body.GetProperty("providers")[0].GetString()).IsEqualTo("github");
        await Assert.That(body.ToString()).DoesNotContain("1234");
        await Assert.That(body.ToString()).DoesNotContain("https://github.com");
        using var unauthorized = await oidc.CallAsync(HttpMethod.Get, path, null);
        await Assert.That(unauthorized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var badCode = await oidc.CallAsync(HttpMethod.Delete, path + "/github", access, new { code = "invalid" });
        await Assert.That(badCode.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var issue = await oidc.PostFormAsync("/ui/terrascale/sign-in", new() { ["email"] = fake.Email });
        var code = await oidc.Email.NextCodeAsync(fake.Email);
        using var removed = await oidc.CallAsync(HttpMethod.Delete, path + "/github", access, new { code });
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var revoked = await oidc.CallAsync(HttpMethod.Get, path, access);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        var user = (await oidc.Server.App.Services.GetRequiredService<Users>().FindByEmailAsync(OidcIssuer.InstanceId, fake.Email))!;
        await Assert.That(user.LinkedProviders.Count).IsEqualTo(0);
        clock.Advance(HostedAuth.MailCooldown);
        await Assert.That(await oidc.SignInAsync(fake.Email)).IsNotNull();
    }

    [Test]
    public async Task Platform_credentials_and_state_are_never_accepted_by_project_sign_in()
    {
        using var fake = new Provider("google");
        await using var oidc = await OidcHarness.StartAsync(fixture, Credentials, services: fake.Route);
        var (_, token) = await oidc.SignInUserAsync(new Faker().Internet.Email());
        var organization = await oidc.CreateOrganizationAsync(token, "Social test");
        var project = await oidc.CreateProjectAsync(token, organization, "Social project");
        var services = oidc.Server.App.Services;
        var options = services.GetRequiredService<AuthOptions>();
        var settings = services.GetRequiredService<ProjectAuthSettings>();
        await settings.UpdateAsync(project, current => current with
        {
            SignUp = false,
            Name = "Test project",
            Providers = [SocialProviders.Seal(options, project, "google", null, "project-google", "synthetic-project-secret", null, null, null)],
        });
        var page = await oidc.Client.GetStringAsync($"/ui/{project}/sign-in");
        await Assert.That(page).Contains("Continue with Google");
        await Assert.That(page).DoesNotContain("Continue with GitHub");
        await Assert.That(page).Contains("Test project");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/ui/{project}/social/google") { Content = new FormUrlEncodedContent([]) };
        request.Headers.Add("Origin", OidcHarness.PublicOrigin);
        using var start = await oidc.Client.SendAsync(request);
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(page).DoesNotContain($"action=\"/ui/{project}/social/");
        var started = await StartAsync(oidc.Client, "google");
        var query = QueryHelpers.ParseQuery(started.Location.Query);
        await Assert.That(query["client_id"].ToString()).IsEqualTo("google-test");
        await Assert.That(query["redirect_uri"].ToString()).IsEqualTo($"{OidcHarness.PublicOrigin}/signin/google");
        using var callback = new HttpRequestMessage(HttpMethod.Get, QueryHelpers.AddQueryString($"/ui/{project}/social/google/callback", new Dictionary<string, string?> { ["state"] = started.State, ["code"] = "synthetic-code" }));
        callback.Headers.Add("Cookie", started.Cookie);
        using var refused = await oidc.Client.SendAsync(callback);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(fake.Exchanges).IsEqualTo(0);
        await Assert.That(await services.GetRequiredService<Users>().FindByEmailAsync(project, fake.Email)).IsNull();
        await Assert.That(await services.GetRequiredService<Users>().FindByEmailAsync(OidcIssuer.InstanceId, fake.Email)).IsNull();
        using var platform = await CompleteAsync(oidc.Client, "google", started);
        await Assert.That(platform.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await services.GetRequiredService<Users>().FindByEmailAsync(project, fake.Email)).IsNull();
        await Assert.That(await services.GetRequiredService<Users>().FindByEmailAsync(OidcIssuer.InstanceId, fake.Email)).IsNotNull();
    }

    [Test]
    [Arguments("issuer")]
    [Arguments("client")]
    [Arguments("structure")]
    [Arguments("tenant")]
    public async Task Trusted_identity_components_do_not_alias_another_binding(string difference)
    {
        using var fake = new Provider("google") { Subject = "part:subject" };
        await using var oidc = await OidcHarness.StartAsync(fixture, Credentials, services: fake.Route);
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var identity = new LinkedProvider("google", "https://accounts.google.com", "google-test", fake.Subject);
        var shadow = difference switch
        {
            "issuer" => identity with { Issuer = "https://other.example.test" },
            "client" => identity with { ClientId = "other-client" },
            "structure" => identity with { ClientId = "google-test:part", Subject = "subject" },
            _ => identity,
        };
        var tenant = difference == "tenant" ? AuthStore.NewId() : OidcIssuer.InstanceId;
        var other = (await users.FromProviderAsync(tenant, shadow, new Faker().Internet.Email(), true, default))!;
        using var signedIn = await CompleteAsync(oidc.Client, "google", await StartAsync(oidc.Client, "google"));
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var current = (await users.FindByEmailAsync(OidcIssuer.InstanceId, fake.Email))!;
        await Assert.That(current.Id).IsNotEqualTo(other.Id);
        await Assert.That(current.LinkedProviders.Single()).IsEqualTo(identity);
        await UnchangedAsync(users, other);
    }

    [Test]
    [Arguments("github")]
    [Arguments("google")]
    public async Task Unbound_subject_cannot_sign_in_to_an_existing_account_with_an_active_factor(string provider)
    {
        using var fake = new Provider(provider);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, Credentials, clock: clock, services: fake.Route);
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var (user, _) = await ActivateWithSecretAsync(oidc, (await users.CreateAsync(OidcIssuer.InstanceId, fake.Email))!);
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var sessions = await store.CountAsync(AuthStore.SessionCollection, OidcIssuer.InstanceId);
        var factor = (await store.GetAsync(AuthStore.MfaCollection, OidcIssuer.InstanceId, user.Id))!;
        using var refused = await CompleteAsync(oidc.Client, provider, await StartAsync(oidc.Client, provider));
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(OidcHarness.SessionCookie(refused)).IsNull();
        await Assert.That(await refused.Content.ReadAsStringAsync()).DoesNotContain("name=\"challenge\"");
        await UnchangedAsync(users, user);
        await Assert.That((await store.GetAsync(AuthStore.MfaCollection, OidcIssuer.InstanceId, user.Id))!.Version).IsEqualTo(factor.Version);
        await Assert.That(await store.CountAsync(AuthStore.SessionCollection, OidcIssuer.InstanceId)).IsEqualTo(sessions);
    }

    [Test]
    [Arguments("github")]
    [Arguments("google")]
    public async Task Bound_subject_with_an_active_factor_completes_through_the_hosted_second_step(string provider)
    {
        using var fake = new Provider(provider);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, Credentials, clock: clock, services: fake.Route);
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        using (var initial = await CompleteAsync(oidc.Client, provider, await StartAsync(oidc.Client, provider)))
        {
            await Assert.That(initial.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var (user, secret) = await ActivateWithSecretAsync(oidc, (await users.FindByEmailAsync(OidcIssuer.InstanceId, fake.Email))!);
        var originalEmail = fake.Email;
        fake.Email = new Faker().Internet.Email();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var sessions = await store.CountAsync(AuthStore.SessionCollection, OidcIssuer.InstanceId);

        // The callback stops at the factor: no cookie, no session, no account change.
        using var step = await CompleteAsync(oidc.Client, provider, await StartAsync(oidc.Client, provider));
        await Assert.That(step.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(OidcHarness.SessionCookie(step)).IsNull();
        await Assert.That(await store.CountAsync(AuthStore.SessionCollection, OidcIssuer.InstanceId)).IsEqualTo(sessions);
        await UnchangedAsync(users, user);
        var challenge = WebUtility.HtmlDecode(ChallengePattern().Match(await step.Content.ReadAsStringAsync()).Groups[1].Value);
        await Assert.That(challenge).IsNotEmpty();

        clock.Advance(TimeSpan.FromSeconds(31));
        using (var wrong = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new() { ["challenge"] = challenge, ["code"] = Code(secret, clock.GetUtcNow().AddHours(1)) }))
        {
            await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(wrong)).IsNull();
        }

        using var right = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new() { ["challenge"] = challenge, ["code"] = Code(secret, clock.GetUtcNow()) });
        var cookie = OidcHarness.SessionCookie(right);
        await Assert.That(cookie).IsNotNull();
        var (verifier, pkce) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(cookie!, pkce), verifier);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var userInfo = await oidc.UserInfoAsync((await OidcHarness.JsonAsync(redeemed)).GetProperty("access_token").GetString()!);
        var profile = await OidcHarness.JsonAsync(userInfo);
        await Assert.That(profile.GetProperty("sub").GetString()).IsEqualTo(user.Id);
        await Assert.That(profile.GetProperty("email").GetString()).IsEqualTo(Users.NormalizeEmail(originalEmail));

        // The challenge completes once.
        clock.Advance(TimeSpan.FromSeconds(31));
        using var replay = await oidc.PostFormAsync("/ui/terrascale/sign-in/mfa", new() { ["challenge"] = challenge, ["code"] = Code(secret, clock.GetUtcNow()) });
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(OidcHarness.SessionCookie(replay)).IsNull();
    }

    [Test]
    public async Task Empty_google_subject_is_refused_without_creating_an_account()
    {
        using var fake = new Provider("google") { Subject = string.Empty };
        await using var oidc = await OidcHarness.StartAsync(fixture, Credentials, services: fake.Route);
        using var refused = await CompleteAsync(oidc.Client, "google", await StartAsync(oidc.Client, "google"));
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(OidcHarness.SessionCookie(refused)).IsNull();
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<Users>().FindByEmailAsync(OidcIssuer.InstanceId, fake.Email)).IsNull();
    }

    private static async Task<UserRecord> ActivateAsync(OidcHarness oidc, UserRecord user) => (await ActivateWithSecretAsync(oidc, user)).User;

    private static async Task<(UserRecord User, string Secret)> ActivateWithSecretAsync(OidcHarness oidc, UserRecord user)
    {
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var enrollment = (await mfa.BeginAsync(user))!;
        var secret = Base32Encoding.ToBytes(enrollment.Secret);
        try
        {
            var clock = oidc.Server.App.Services.GetRequiredService<TimeProvider>();
            var active = await mfa.ConfirmAsync(user, new Totp(secret).ComputeTotp(clock.GetUtcNow().UtcDateTime));
            await Assert.That(active).IsNotNull();
            return (active!.User, enrollment.Secret);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static string Code(string secret, DateTimeOffset at) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(at.UtcDateTime);

    [GeneratedRegex("name=\"challenge\" value=\"([^\"]+)\"")]
    private static partial Regex ChallengePattern();

    private static async Task UnchangedAsync(Users users, UserRecord expected) =>
        await Assert.That(JsonSerializer.Serialize((await users.GetAsync(expected.InstanceId, expected.Id))!, IdentityJson.Default.UserRecord))
            .IsEqualTo(JsonSerializer.Serialize(expected, IdentityJson.Default.UserRecord));

    private static void Credentials(Dictionary<string, string> settings)
    {
        settings["github-oauth"] = "{\"client_id\":\"github-test\",\"client_secret\":\"synthetic-github-secret\"}";
        settings["google-oauth"] = "{\"client_id\":\"google-test\",\"client_secret\":\"synthetic-google-secret\"}";
    }

    private static async Task<HttpResponseMessage> StartResponseAsync(HttpClient client, string provider, string? next = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/ui/terrascale/social/{provider}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["next"] = next ?? string.Empty }),
        };
        request.Headers.Add("Origin", OidcHarness.PublicOrigin);
        return await client.SendAsync(request);
    }

    private static async Task<Started> StartAsync(HttpClient client, string provider, string? next = null)
    {
        using var response = await StartResponseAsync(client, provider, next);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
        return new(response.Headers.Location!, QueryHelpers.ParseQuery(response.Headers.Location!.Query)["state"].ToString(), response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
    }

    private static async Task<HttpResponseMessage> CompleteAsync(HttpClient client, string provider, Started start)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, QueryHelpers.AddQueryString($"/signin/{provider}", new Dictionary<string, string?> { ["state"] = start.State, ["code"] = "synthetic-code" }));
        request.Headers.Add("Cookie", start.Cookie);
        return await client.SendAsync(request);
    }

    private sealed record Started(Uri Location, string State, string Cookie);

    private sealed class Provider(string provider) : HttpMessageHandler
    {
        public string Email { get; set; } = new Faker().Internet.Email();
        public string Subject { get; set; } = provider == SocialProviders.GitHub ? "1234" : "Google-Subject-1234";
        public bool Verified { get; init; } = true;
        public int Exchanges { get; private set; }
        public string? Verifier { get; private set; }

        public void Route(IServiceCollection services) => services.AddHttpClient(PlatformSocialSignIn.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => this);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            JsonNode body;
            if (request.Method == HttpMethod.Post)
            {
                var fields = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                this.Verifier = fields["code_verifier"].ToString();
                this.Exchanges++;
                body = new JsonObject { ["access_token"] = "synthetic-provider-access", ["token_type"] = "Bearer" };
            }
            else
            {
                body = request.RequestUri!.AbsolutePath == "/user/emails"
                    ? new JsonArray(new JsonObject { ["email"] = this.Email, ["primary"] = true, ["verified"] = this.Verified })
                    : provider == SocialProviders.GitHub ? new JsonObject { ["id"] = long.Parse(this.Subject, System.Globalization.CultureInfo.InvariantCulture), ["email"] = "untrusted-public@example.test" }
                    : new JsonObject { ["sub"] = this.Subject, ["email"] = this.Email, ["email_verified"] = this.Verified };
            }

            return new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
