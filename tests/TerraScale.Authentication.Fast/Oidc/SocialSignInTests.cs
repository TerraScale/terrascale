using System.Net;
using Bogus;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
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
}
