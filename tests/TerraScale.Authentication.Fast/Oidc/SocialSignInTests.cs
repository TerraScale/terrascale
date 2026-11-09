using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Storage;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>
/// A project's end users sign in with the project's own Google, GitHub, Apple or OIDC client on its hosted pages
/// (project-social-sign-in.md), against contract fakes of each provider.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class SocialSignInTests(TerraBaseFixture fixture)
{
    private readonly Faker faker = new();

    [Test]
    [Arguments("google")]
    public async Task A_verified_provider_creates_only_its_project_identity_and_rejects_unverified_email(string provider)
    {
        using var providers = new FakeSocialProviders();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: providers.Route);
        var (project, token) = await this.ProjectAsync(oidc);
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/providers/{provider}", token, Configuration(provider, providers));

        using var page = await oidc.Client.GetAsync($"/ui/{project}/sign-in");
        await Assert.That(await page.Content.ReadAsStringAsync()).Contains($"/ui/{project}/social/{provider}");

        // The provider creates an account in the project tenant. The full returning-user journey runs in Slow.
        var email = this.faker.Internet.Email(provider: "example.test").ToLowerInvariant();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        await Assert.That(await SignInAsync(oidc, providers, project, provider, email)).IsNotNull();
        var created = (await users.FindByEmailAsync(project, email))!;
        await Assert.That(created).IsNotNull();
        var identity = created.LinkedProviders.Single();
        await Assert.That(identity.Subject).IsEqualTo("12345");
        await Assert.That(identity.ClientId).IsEqualTo(provider == "apple" ? "test.example.app" : "project-client");
        await Assert.That(identity.Issuer).IsEqualTo(provider switch
        {
            "google" => "https://accounts.google.com",
            "apple" => "https://appleid.apple.com",
            "github" => "https://github.com",
            _ => FakeSocialProviders.OidcIssuer,
        });
        await Assert.That(await users.FindByEmailAsync(OidcIssuer.InstanceId, email)).IsNull();

        // An address the provider did not verify signs nobody in.
        await Assert.That(await SignInAsync(oidc, providers, project, provider, "unverified-" + email, verified: false, subject: "67890")).IsNull();
        await Assert.That(await users.FindByEmailAsync(project, "unverified-" + email)).IsNull();

    }

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
        var (_, token) = await oidc.CreateUserTokenAsync(this.faker.Internet.Email(provider: "example.test").ToLowerInvariant());
        var organization = await oidc.CreateOrganizationAsync(token, this.faker.Company.CompanyName());
        return (await oidc.CreateProjectAsync(token, organization, this.faker.Commerce.ProductName()), token);
    }

    private static async Task<string> SeededGoogleProjectAsync(OidcHarness oidc, FakeSocialProviders providers)
    {
        var services = oidc.Server.App.Services;
        var options = services.GetRequiredService<AuthOptions>();
        var project = await services.GetRequiredService<AuthData>().SeedProjectAsync(AuthIds.New(), "Social");
        var sealedProvider = SocialProviders.Seal(options, project.Id, SocialProviders.Google, null, "project-client", providers.ClientSecret, null, null, null);
        _ = await services.GetRequiredService<ProjectAuthSettings>().UpdateAsync(project.Id, current =>
            current with { Providers = [.. (current.Providers ?? []).Where(other => other.Provider != SocialProviders.Google), sealedProvider] });
        return project.Id;
    }

    [Test]
    public async Task A_password_user_connects_a_provider_after_reauthentication_and_a_second_account_cannot_take_it()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var providers = new FakeSocialProviders();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: providers.Route);
        var project = await SeededGoogleProjectAsync(oidc, providers);

        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var email = this.faker.Internet.Email(provider: "example.test").ToLowerInvariant();
        var user = (await users.CreateAsync(project, email, "argon2id", CancellationToken.None))!;

        var staleSession = await sessions.CreateAsync(user);
        clock.Advance(TimeSpan.FromMinutes(16));
        using (var stale = await PostAsync(oidc, project, staleSession.Token))
        {
            await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(await stale.Content.ReadAsStringAsync()).Contains("Sign in again");
        }

        var providerEmail = this.faker.Internet.Email(provider: "example.test").ToLowerInvariant();
        var session = await sessions.CreateAsync(user);
        string code;
        string state;
        using (var connect = await PostAsync(oidc, project, session.Token))
        {
            await Assert.That(connect.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            var social = connect.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith($"tsauth_social_{project}=", StringComparison.Ordinal)).Split(';')[0];
            code = providers.Approve(connect.Headers.Location!, providerEmail, subject: "12345");
            state = QueryHelpers.ParseQuery(connect.Headers.Location!.Query)["state"].ToString();
            using var callback = await CallbackAsync(oidc, project, "google", code, state, $"{social}; tsauth_session_{project}={session.Token}");
            await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(callback.Headers.Location!.ToString()).IsEqualTo($"/ui/{project}/security");
        }

        var linked = (await users.GetAsync(project, user.Id))!;
        await Assert.That(linked.Email).IsEqualTo(email);
        await Assert.That(linked.LinkedProviders.Single().Subject).IsEqualTo("12345");

        var rival = (await users.CreateAsync(project, this.faker.Internet.Email(provider: "example.test").ToLowerInvariant(), "argon2id", CancellationToken.None))!;
        var rivalSession = await sessions.CreateAsync(rival);
        using (var connect = await PostAsync(oidc, project, rivalSession.Token))
        {
            await Assert.That(connect.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            var social = connect.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith($"tsauth_social_{project}=", StringComparison.Ordinal)).Split(';')[0];
            code = providers.Approve(connect.Headers.Location!, providerEmail, subject: "12345");
            state = QueryHelpers.ParseQuery(connect.Headers.Location!.Query)["state"].ToString();
            using var callback = await CallbackAsync(oidc, project, "google", code, state, $"{social}; tsauth_session_{project}={rivalSession.Token}");
            await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(await callback.Content.ReadAsStringAsync()).Contains("already connected to another account");
        }

        await Assert.That((await users.GetAsync(project, rival.Id))!.LinkedProviders).IsEmpty();
        await Assert.That((await users.FindByProviderAsync(project, linked.LinkedProviders.Single(), CancellationToken.None))!.Id).IsEqualTo(user.Id);
        await Assert.That(await SignInAsync(oidc, providers, project, "google", providerEmail, subject: "12345")).IsNotNull();
    }

    private static Task<HttpResponseMessage> PostAsync(OidcHarness oidc, string project, string session)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/ui/{project}/security/social/connect")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["provider"] = "google" }),
        };
        request.Headers.Add("Cookie", $"tsauth_session_{project}={session}");
        request.Headers.Add("Origin", OidcHarness.PublicOrigin);
        return oidc.Client.SendAsync(request);
    }

    [Test]
    public async Task A_linked_provider_emails_a_single_use_unlink_code()
    {
        var oidc = await fixture.SharedAsync();
        var email = this.faker.Internet.Email(provider: "example.test").ToLowerInvariant();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var linked = await users.FromProviderAsync(OidcIssuer.InstanceId,
            new LinkedProvider(SocialProviders.GitHub, "https://github.com", "platform", "42"), email, true, CancellationToken.None);
        await Assert.That(linked).IsNotNull();
        var (_, tokens) = await oidc.CreateSessionAndRedeemAsync(linked!);
        var token = tokens.GetProperty("access_token").GetString()!;

        using var sent = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/account/providers/github/code", token);
        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var code = await oidc.Email.NextCodeAsync(email);

        using var unlinked = await oidc.CallAsync(HttpMethod.Delete, "/v1/terrascale/account/providers/github", token, new { code });
        await Assert.That(unlinked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var replayed = await oidc.CallAsync(HttpMethod.Delete, "/v1/terrascale/account/providers/github", token, new { code });
        await Assert.That(replayed.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}
