using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bogus;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OtpNet;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Oidc;

/// <summary>
/// A project's end users sign in with the project's own Google, GitHub, Apple or OIDC client on its hosted pages
/// (project-social-sign-in.md), against contract fakes of each provider.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class SocialSignInTests(TerraBaseFixture fixture)
{
    private readonly Faker faker = new();

    [Test]
    [Arguments("https://accounts.google.com", "accounts.google.com")]
    [Arguments("accounts.google.com", "https://accounts.google.com")]
    public async Task Validated_Google_alias_changes_keep_one_local_identity(string firstIssuer, string returningIssuer)
    {
        using var providers = new FakeSocialProviders { GoogleTokenIssuer = firstIssuer };
        await using var oidc = await OidcHarness.StartAsync(fixture, services: providers.Route);
        var (project, token) = await this.ProjectAsync(oidc);
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/providers/google", token, Configuration("google", providers));
        var email = this.faker.Internet.Email(provider: "example.test").ToLowerInvariant();
        await Assert.That(await SignInAsync(oidc, providers, project, "google", email)).IsNotNull();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var original = (await users.FindByEmailAsync(project, email))!;
        await Assert.That(original.LinkedProviders.Count).IsEqualTo(1);
        await Assert.That(original.LinkedProviders.Single().Issuer).IsEqualTo("https://accounts.google.com");
        providers.GoogleTokenIssuer = returningIssuer;
        var changedEmail = this.faker.Internet.Email(provider: "example.test").ToLowerInvariant();
        foreach (var verifiedEmail in new[] { email, changedEmail })
        {
            using var returning = await SignInResponseAsync(oidc, providers, project, "google", verifiedEmail);
            await Assert.That(returning.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var cookie = SessionCookie(returning, project)!;
            var session = (await oidc.Server.App.Services.GetRequiredService<Sessions>()
                .ResolveAsync(project, cookie[(cookie.IndexOf('=') + 1)..]))!.Value;
            await Assert.That(session.User.Id).IsEqualTo(original.Id);
            await Assert.That(session.User.Email).IsEqualTo(email);
            await UnchangedAsync(users, original);
            await Assert.That(await oidc.Server.App.Services.GetRequiredService<AuthStore>()
                .CountAsync(AuthStore.UserCollection, project)).IsEqualTo(1L);
        }

        await Assert.That(await users.FindByEmailAsync(project, changedEmail)).IsNull();
    }

    [Test]
    public async Task Callbacks_without_the_browsers_state_or_for_unconfigured_providers_are_refused()
    {
        using var providers = new FakeSocialProviders();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: providers.Route);
        var (project, token) = await this.ProjectAsync(oidc);
        using (var unconfigured = await oidc.Client.GetAsync($"/ui/{project}/social/google"))
        {
            await Assert.That(unconfigured.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        using (var platform = await oidc.Client.GetAsync($"/ui/{OidcIssuer.Slug}/social/google"))
        {
            await Assert.That(platform.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/providers/google", token, Configuration("google", providers));
        var (location, cookie) = await StartAsync(oidc, project, "google");
        var code = providers.Approve(location, this.faker.Internet.Email().ToLowerInvariant());
        var state = QueryHelpers.ParseQuery(location.Query)["state"].ToString();

        // Another browser (no cookie), a wrong state and a replayed cookie are all refused without a session.
        foreach (var (stateValue, cookieValue) in new[] { (state, (string?)null), ("forged", cookie) })
        {
            using var refused = await CallbackAsync(oidc, project, "google", code, stateValue, cookieValue);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(SessionCookie(refused, project)).IsNull();
        }

        using var signedIn = await CallbackAsync(oidc, project, "google", code, state, cookie);
        await Assert.That(SessionCookie(signedIn, project)).IsNotNull();
        using var replayed = await CallbackAsync(oidc, project, "google", code, state, cookie);
        await Assert.That(replayed.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Arguments("google", false)]
    [Arguments("github", false)]
    [Arguments("apple", false)]
    [Arguments("oidc", false)]
    [Arguments("google", true)]
    [Arguments("github", true)]
    [Arguments("apple", true)]
    [Arguments("oidc", true)]
    public async Task Unknown_subject_never_attaches_to_an_existing_email(string provider, bool bound)
    {
        using var providers = new FakeSocialProviders();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: providers.Route);
        var (project, token) = await this.ProjectAsync(oidc);
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/providers/{provider}", token, Configuration(provider, providers));
        var email = this.faker.Internet.Email(provider: "example.test");
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        if (bound)
        {
            await Assert.That(await SignInAsync(oidc, providers, project, provider, email)).IsNotNull();
        }

        var existing = (await users.FindByEmailAsync(project, email) ?? await users.CreateAsync(project, email))!;
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var sessions = await store.CountAsync(AuthStore.SessionCollection, project);
        using var refused = await SignInResponseAsync(oidc, providers, project, provider, email, subject: "67890");
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(SessionCookie(refused, project)).IsNull();
        await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("Try an existing sign-in method");
        await UnchangedAsync(users, existing);
        await Assert.That(await store.CountAsync(AuthStore.UserCollection, project)).IsEqualTo(1L);
        await Assert.That(await store.CountAsync(AuthStore.SessionCollection, project)).IsEqualTo(sessions);
        // Rejected binding did not reserve the subject or create an orphan user.
        await Assert.That(await SignInAsync(oidc, providers, project, provider, this.faker.Internet.Email(), subject: "67890")).IsNotNull();
    }

    [Test]
    [Arguments("google", null)]
    [Arguments("google", "")]
    [Arguments("apple", null)]
    [Arguments("apple", "")]
    [Arguments("oidc", null)]
    [Arguments("oidc", "")]
    [Arguments("oidc", " ")]
    [Arguments("oidc", "too-long")]
    [Arguments("github", "0")]
    [Arguments("github", "-1")]
    public async Task Invalid_subject_is_refused_without_creating_an_account(string provider, string? subject)
    {
        using var providers = new FakeSocialProviders();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: providers.Route);
        var (project, token) = await this.ProjectAsync(oidc);
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/providers/{provider}", token, Configuration(provider, providers));
        using var refused = await SignInResponseAsync(oidc, providers, project, provider, this.faker.Internet.Email(), subject: subject == "too-long" ? new string('s', 257) : subject);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(SessionCookie(refused, project)).IsNull();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        await Assert.That(await store.CountAsync(AuthStore.UserCollection, project)).IsEqualTo(0L);
        await Assert.That(await store.CountAsync(AuthStore.SessionCollection, project)).IsEqualTo(0L);
    }

    [Test]
    public async Task Replacing_provider_credentials_refuses_a_pending_flow()
    {
        using var providers = new FakeSocialProviders();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: providers.Route);
        var (project, token) = await this.ProjectAsync(oidc);
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/providers/google", token, Configuration("google", providers));
        var (location, cookie) = await StartAsync(oidc, project, "google");
        var code = providers.Approve(location, this.faker.Internet.Email());
        // Re-sealing even the same supplied secret replaces its credential generation.
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/providers/google", token, Configuration("google", providers));
        using var refused = await CallbackAsync(oidc, project, "google", code, QueryHelpers.ParseQuery(location.Query)["state"].ToString(), cookie);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(SessionCookie(refused, project)).IsNull();
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<AuthStore>().CountAsync(AuthStore.UserCollection, project)).IsEqualTo(0L);
    }

    [Test]
    public async Task Bound_identity_with_local_MFA_completes_through_the_hosted_second_step()
    {
        using var providers = new FakeSocialProviders();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: providers.Route);
        var (project, token) = await this.ProjectAsync(oidc);
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/providers/google", token, Configuration("google", providers));
        var email = this.faker.Internet.Email(provider: "example.test").ToLowerInvariant();
        await Assert.That(await SignInAsync(oidc, providers, project, "google", email)).IsNotNull();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var user = (await users.FindByEmailAsync(project, email))!;
        var mfa = oidc.Server.App.Services.GetRequiredService<TotpCredentials>();
        var enrollment = (await mfa.BeginAsync(user))!;
        var active = (await mfa.ConfirmAsync(user, Code(enrollment.Secret, clock.GetUtcNow())))!;
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var sessions = await store.CountAsync(AuthStore.SessionCollection, project);

        // The callback stops at the factor: no session and no account change, even with a changed upstream email.
        using var step = await SignInResponseAsync(oidc, providers, project, "google", this.faker.Internet.Email());
        await Assert.That(step.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(SessionCookie(step, project)).IsNull();
        await Assert.That(await store.CountAsync(AuthStore.SessionCollection, project)).IsEqualTo(sessions);
        await UnchangedAsync(users, active.User);
        var challenge = WebUtility.HtmlDecode(ChallengePattern().Match(await step.Content.ReadAsStringAsync()).Groups[1].Value);
        await Assert.That(challenge).IsNotEmpty();

        clock.Advance(TimeSpan.FromSeconds(31));
        using (var wrong = await oidc.PostFormAsync($"/ui/{project}/sign-in/mfa", new() { ["challenge"] = challenge, ["code"] = Code(enrollment.Secret, clock.GetUtcNow().AddHours(1)) }))
        {
            await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(SessionCookie(wrong, project)).IsNull();
        }

        using var right = await oidc.PostFormAsync($"/ui/{project}/sign-in/mfa", new() { ["challenge"] = challenge, ["code"] = Code(enrollment.Secret, clock.GetUtcNow()) });
        var cookie = SessionCookie(right, project);
        await Assert.That(cookie).IsNotNull();
        var resolved = (await oidc.Server.App.Services.GetRequiredService<Sessions>().ResolveAsync(project, cookie![(cookie.IndexOf('=') + 1)..]))!.Value;
        await Assert.That(resolved.User.Id).IsEqualTo(user.Id);
        await Assert.That(resolved.User.Email).IsEqualTo(email);

        clock.Advance(TimeSpan.FromSeconds(31));
        using var replay = await oidc.PostFormAsync($"/ui/{project}/sign-in/mfa", new() { ["challenge"] = challenge, ["code"] = Code(enrollment.Secret, clock.GetUtcNow()) });
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(SessionCookie(replay, project)).IsNull();
    }

    private static string Code(string secret, DateTimeOffset at) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(at.UtcDateTime);

    [GeneratedRegex("name=\"challenge\" value=\"([^\"]+)\"")]
    private static partial Regex ChallengePattern();

    private static async Task UnchangedAsync(Users users, UserRecord expected) =>
        await Assert.That(JsonSerializer.Serialize((await users.GetAsync(expected.InstanceId, expected.Id))!, IdentityJson.Default.UserRecord))
            .IsEqualTo(JsonSerializer.Serialize(expected, IdentityJson.Default.UserRecord));

    private static object Configuration(string provider, FakeSocialProviders providers) => provider switch
    {
        "apple" => new { client_id = "test.example.app", client_secret = providers.AppleKey.ExportPkcs8PrivateKeyPem(), team_id = "ABCDE12345", key_id = "KEY1234567" },
        "oidc" => new { client_id = "project-client", client_secret = providers.ClientSecret, issuer = FakeSocialProviders.OidcIssuer },
        _ => (object)new { client_id = "project-client", client_secret = providers.ClientSecret },
    };

    // Starts at the hosted page, approves at the provider and returns the project session cookie, or null.
    private static async Task<string?> SignInAsync(OidcHarness oidc, FakeSocialProviders providers, string project, string provider, string email, bool verified = true, string? subject = "12345")
    {
        using var callback = await SignInResponseAsync(oidc, providers, project, provider, email, verified, subject);
        return SessionCookie(callback, project);
    }

    private static async Task<HttpResponseMessage> SignInResponseAsync(OidcHarness oidc, FakeSocialProviders providers, string project, string provider, string email, bool verified = true, string? subject = "12345")
    {
        var (location, cookie) = await StartAsync(oidc, project, provider);
        var code = providers.Approve(location, email, verified, subject);
        return await CallbackAsync(oidc, project, provider, code, QueryHelpers.ParseQuery(location.Query)["state"].ToString(), cookie);
    }

    private static async Task<(Uri Location, string Cookie)> StartAsync(OidcHarness oidc, string project, string provider)
    {
        using var start = await oidc.Client.GetAsync($"/ui/{project}/social/{provider}");
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var cookie = start.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith($"tsauth_social_{project}=", StringComparison.Ordinal));
        await Assert.That(cookie).Contains("samesite=none");
        return (start.Headers.Location!, cookie.Split(';')[0]);
    }

    // Apple posts the callback (form_post); the others redirect with a query.
    private static Task<HttpResponseMessage> CallbackAsync(OidcHarness oidc, string project, string provider, string code, string state, string? cookie)
    {
        var path = $"/ui/{project}/social/{provider}/callback";
        var fields = new Dictionary<string, string> { ["code"] = code, ["state"] = state };
        var request = provider == "apple"
            ? new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) }
            : new HttpRequestMessage(HttpMethod.Get, QueryHelpers.AddQueryString(path, fields!));
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return oidc.Client.SendAsync(request);
    }

    private static string? SessionCookie(HttpResponseMessage response, string project) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(cookie => cookie.Split(';')[0]).FirstOrDefault(cookie => cookie.StartsWith($"tsauth_session_{project}=", StringComparison.Ordinal))
            : null;

    private async Task<(string Project, string Token)> ProjectAsync(OidcHarness oidc)
    {
        var (_, token) = await oidc.SignInUserAsync(this.faker.Internet.Email(provider: "example.test").ToLowerInvariant());
        var organization = await oidc.CreateOrganizationAsync(token, this.faker.Company.CompanyName());
        return (await oidc.CreateProjectAsync(token, organization, this.faker.Commerce.ProductName()), token);
    }
}
