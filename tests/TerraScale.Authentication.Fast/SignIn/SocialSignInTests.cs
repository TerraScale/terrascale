using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bogus;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Social;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.SignIn;

/// <summary>Hosted provider sign-in through HTTP, real TerraBase and a controlled provider backchannel.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class SocialSignInTests(TerraBaseFixture fixture)
{
    [Test]
    [Arguments("github")]
    [Arguments("google")]
    public async Task Verified_provider_creates_one_account_and_reuses_its_stable_subject(string provider)
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
        var second = await StartAsync(oidc.Client, provider);
        using var signedIn = await CompleteAsync(oidc.Client, provider, second);
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await users.FindByEmailAsync(OidcIssuer.InstanceId, fake.Email))!.Id).IsEqualTo(user.Id);
        await Assert.That((await users.GetAsync(OidcIssuer.InstanceId, user.Id))!.AuthEpoch).IsEqualTo(user.AuthEpoch);
        // Callback replays cannot exchange a second time or issue a session.
        var calls = fake.Exchanges;
        using var replay = await CompleteAsync(oidc.Client, provider, second);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(fake.Exchanges).IsEqualTo(calls);
    }

    [Test]
    [Arguments("github")]
    [Arguments("google")]
    public async Task Existing_email_code_account_is_linked_instead_of_duplicated(string provider)
    {
        using var fake = new Provider(provider);
        await using var oidc = await OidcHarness.StartAsync(fixture, Credentials, services: fake.Route);
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var existing = (await users.CreateAsync(OidcIssuer.InstanceId, fake.Email))!;
        var start = await StartAsync(oidc.Client, provider);
        using var response = await CompleteAsync(oidc.Client, provider, start);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var linked = (await users.FindByEmailAsync(OidcIssuer.InstanceId, fake.Email))!;
        await Assert.That(linked.Id).IsEqualTo(existing.Id);
        await Assert.That(linked.LinkedProviders.Single().Provider).IsEqualTo(provider);
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
        public string Email { get; } = new Faker().Internet.Email();
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
                    : provider == SocialProviders.GitHub ? new JsonObject { ["id"] = 1234, ["email"] = "untrusted-public@example.test" }
                    : new JsonObject { ["sub"] = "google-subject-1234", ["email"] = this.Email, ["email_verified"] = this.Verified };
            }

            return new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
