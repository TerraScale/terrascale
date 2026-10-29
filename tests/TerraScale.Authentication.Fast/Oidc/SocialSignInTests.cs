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
    [Arguments("github")]
    [Arguments("apple")]
    [Arguments("oidc")]
    public async Task End_users_sign_up_and_back_in_with_the_projects_provider(string provider)
    {
        using var providers = new FakeSocialProviders();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: providers.Route);
        var (project, token) = await this.ProjectAsync(oidc);
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/providers/{provider}", token, Configuration(provider, providers));

        using var page = await oidc.Client.GetAsync($"/ui/{project}/sign-in");
        await Assert.That(await page.Content.ReadAsStringAsync()).Contains($"/ui/{project}/social/{provider}");

        // First sign-in creates the account in the project tenant; the second signs the same account in.
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
        await Assert.That(await SignInAsync(oidc, providers, project, provider, email)).IsNotNull();
        await Assert.That((await users.FindByEmailAsync(project, email))!.Id).IsEqualTo(created!.Id);

        // An address the provider did not verify signs nobody in.
        await Assert.That(await SignInAsync(oidc, providers, project, provider, "unverified-" + email, verified: false, subject: "67890")).IsNull();
        await Assert.That(await users.FindByEmailAsync(project, "unverified-" + email)).IsNull();

        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var changedEmail = this.faker.Internet.Email(provider: "example.test").ToLowerInvariant();
        var other = (await users.CreateAsync(project, changedEmail))!;
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/settings", token, new { methods = new { sign_up = false } });
        var cookie = (await SignInAsync(oidc, providers, project, provider, changedEmail))!;
        var resolved = (await sessions.ResolveAsync(project, cookie[(cookie.IndexOf('=') + 1)..]))!.Value;
        await Assert.That(resolved.User.Id).IsEqualTo(created.Id);
        await Assert.That(resolved.User.Email).IsEqualTo(email);
        await Assert.That(TerraScale.DotNet.TerraScaleIds.IsValid(resolved.User.Id)).IsTrue();
        await UnchangedAsync(users, created);
        await UnchangedAsync(users, other);
        var unusedEmail = this.faker.Internet.Email();
        await Assert.That(await SignInAsync(oidc, providers, project, provider, unusedEmail, subject: "67890")).IsNull();
        await Assert.That(await users.FindByEmailAsync(project, unusedEmail)).IsNull();

        var organization = await oidc.CreateOrganizationAsync(token, this.faker.Company.CompanyName());
        var second = await oidc.CreateProjectAsync(token, organization, this.faker.Commerce.ProductName());
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{second}/providers/{provider}", token, Configuration(provider, providers));
        await Assert.That(await SignInAsync(oidc, providers, second, provider, email)).IsNotNull();
        await Assert.That((await users.FindByEmailAsync(second, email))!.Id).IsNotEqualTo(created.Id);
        await UnchangedAsync(users, created);
        await users.SetDisabledAsync(project, created.Id, true);
        await Assert.That(await SignInAsync(oidc, providers, project, provider, email)).IsNull();
    }

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
