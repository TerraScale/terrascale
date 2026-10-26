using System.Net;
using System.Text.Encodings.Web;
using Bogus;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>A project's end users sign in to the project's own applications at its own issuer (project-tenant-management.md).</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ProjectTenantTests(TerraBaseFixture fixture)
{
    private const string Callback = "https://app.example.test/callback";

    [Test]
    public async Task End_users_sign_in_to_a_project_client_and_get_tokens_only_the_project_issuer_accepts()
    {
        var faker = new Faker();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var owner = faker.Internet.Email().ToLowerInvariant();
        var (_, ownerToken) = await oidc.SignInUserAsync(owner);
        var project = await oidc.CreateProjectAsync(ownerToken, await oidc.CreateOrganizationAsync(ownerToken), faker.Commerce.ProductName());
        var application = await oidc.OkAsync(HttpMethod.Post, $"/v1/{project}/clients", ownerToken,
            new { name = "Example app", type = "public", redirect_uris = new[] { Callback } }, HttpStatusCode.Created);
        var clientId = application.GetProperty("client_id").GetString()!;
        var issuer = $"{OidcHarness.PublicOrigin}/{project}";

        using var discovery = await oidc.Client.GetAsync($"/{project}/.well-known/openid-configuration");
        var metadata = await OidcHarness.JsonAsync(discovery);
        await Assert.That(metadata.GetProperty("issuer").GetString()).IsEqualTo(issuer);
        await Assert.That(metadata.GetProperty("token_endpoint").GetString()).IsEqualTo($"{issuer}/oauth/token");

        // Without the project's own session, authorize sends the browser to the project's hosted sign-in page; the
        // platform session cookie is not the project's.
        var (verifier, challenge) = OidcHarness.Pkce();
        var authorize = OidcHarness.AuthorizePath(challenge, redirectUri: Callback, clientId: clientId).Replace($"/{OidcIssuer.Slug}/", $"/{project}/", StringComparison.Ordinal);
        clock.Advance(HostedAuth.MailCooldown);
        using var anonymous = await oidc.AuthorizeAsync(authorize, await oidc.SignInAsync(owner));
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(anonymous.Headers.Location!.OriginalString).StartsWith($"/ui/{project}/sign-in?next=");
        using var signUpPage = await oidc.Client.GetAsync($"/ui/{project}/sign-up");
        await Assert.That(signUpPage.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The end user's account and session live in the project tenant, under the project's cookie.
        var address = faker.Internet.Email().ToLowerInvariant();
        var user = await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync(project, address) ?? throw new InvalidOperationException("Duplicate address.");
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<Users>().FindByEmailAsync(OidcIssuer.InstanceId, address)).IsNull();
        using (var request = await oidc.PostFormAsync($"/ui/{project}/sign-in", new() { ["email"] = address, ["next"] = authorize }))
        {
            await Assert.That(request.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using var verify = await oidc.PostFormAsync($"/ui/{project}/sign-in/verify", new() { ["email"] = address, ["code"] = await oidc.Email.NextCodeAsync(address), ["next"] = authorize });
        var cookies = verify.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';')[0]).ToArray();
        await Assert.That(cookies.Any(cookie => cookie.StartsWith("tsauth_session=", StringComparison.Ordinal))).IsFalse();
        var session = cookies.Single(cookie => cookie.StartsWith($"tsauth_session_{project}=", StringComparison.Ordinal)).Split('=', 2)[1];

        using var authorized = await oidc.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, authorize) { Headers = { { "Cookie", $"tsauth_session_{project}={session}" } } });
        await Assert.That(authorized.Headers.Location?.AbsoluteUri).StartsWith(Callback + "?");
        var code = OidcHarness.Query(authorized.Headers.Location!)["code"]!;

        var redemption = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = Callback,
            ["code_verifier"] = verifier,
            ["client_id"] = clientId,
        };
        using var redeemed = await oidc.PostTokenAsync(redemption, null, null, tenant: project);
        var tokens = await OidcHarness.JsonAsync(redeemed);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var access = tokens.GetProperty("access_token").GetString()!;
        var claims = new JsonWebToken(access);
        await Assert.That(claims.Issuer).IsEqualTo(issuer);
        await Assert.That(claims.Subject).IsEqualTo(user.Id);
        await Assert.That(claims.Audiences.ToArray()).IsEquivalentTo([clientId]);
        await Assert.That(claims.TryGetClaim("platform_role", out _)).IsFalse();

        using var userInfo = await oidc.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/{project}/oauth/userinfo") { Headers = { { "Authorization", $"Bearer {access}" } } });
        await Assert.That((await OidcHarness.JsonAsync(userInfo)).GetProperty("sub").GetString()).IsEqualTo(user.Id);

        // The project's tokens reach no TerraScale API and no other issuer.
        await Assert.That((await oidc.FailAsync(HttpMethod.Get, PlatformCalls.Organizations, access)).Status).IsEqualTo(401);
        using var platformUserInfo = await oidc.UserInfoAsync(access);
        await Assert.That(platformUserInfo.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        using var refreshed = await oidc.PostTokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!, ["client_id"] = clientId },
            null, null, tenant: project);
        await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(new JsonWebToken((await OidcHarness.JsonAsync(refreshed)).GetProperty("access_token").GetString()!).Issuer).IsEqualTo(issuer);

        using var machine = await oidc.PostTokenAsync(new() { ["grant_type"] = "client_credentials", ["client_id"] = clientId }, null, null, tenant: project);
        await Assert.That(machine.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // Project clients can end their own grant through the endpoint discovery advertises.
        var rotated = await OidcHarness.JsonAsync(refreshed);
        using var revoked = await oidc.PostFormAsync($"/{project}/oauth/revoke", new()
        {
            ["client_id"] = clientId,
            ["token"] = rotated.GetProperty("refresh_token").GetString()!,
            ["token_type_hint"] = "refresh_token",
        });
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var revokedInfo = await oidc.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/{project}/oauth/userinfo")
        {
            Headers = { { "Authorization", $"Bearer {rotated.GetProperty("access_token").GetString()}" } },
        });
        await Assert.That(revokedInfo.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var revokedRefresh = await oidc.PostTokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = rotated.GetProperty("refresh_token").GetString()!,
        }, null, null, tenant: project);
        await Assert.That(revokedRefresh.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Hosted_pages_show_the_project_brand_and_only_its_sign_in_methods()
    {
        var faker = new Faker();
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, ownerToken) = await oidc.SignInUserAsync(faker.Internet.Email().ToLowerInvariant());
        const string projectName = "O'Kon & Kshlerin Project";
        var project = await oidc.CreateProjectAsync(ownerToken, await oidc.CreateOrganizationAsync(ownerToken), projectName);

        // Until the project changes anything, its pages carry its name and every method.
        using var initial = await oidc.Client.GetAsync($"/ui/{project}/sign-in");
        var initialPage = await initial.Content.ReadAsStringAsync();
        await Assert.That(initialPage).Contains($"Sign in to {HtmlEncoder.Default.Encode(projectName)}");
        await Assert.That(initialPage).Contains("Create account");

        // Razor uses HtmlEncoder, whose apostrophe entity differs from WebUtility.HtmlEncode.
        const string brand = "O'Kon & Kshlerin <Labs>";
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/settings", ownerToken, new
        {
            methods = new { password = false, sign_up = false },
            branding = new { name = brand, logo_url = "https://cdn.example.test/logo.png", theme_color = "#112233" },
        });

        using var themed = await oidc.Client.GetAsync($"/ui/{project}/sign-in");
        var page = await themed.Content.ReadAsStringAsync();
        await Assert.That(page).Contains(HtmlEncoder.Default.Encode(brand));
        await Assert.That(page).DoesNotContain(brand);
        await Assert.That(page).Contains("https://cdn.example.test/logo.png");
        await Assert.That(page).Contains("#112233");
        await Assert.That(page).DoesNotContain("Create account");
        await Assert.That(page).DoesNotContain("sign-in/password");
        await Assert.That(page).DoesNotContain("Forgot password?");

        using var signUp = await oidc.Client.GetAsync($"/ui/{project}/sign-up");
        await Assert.That(signUp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var reset = await oidc.Client.GetAsync($"/ui/{project}/reset");
        await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var password = await oidc.PostFormAsync($"/ui/{project}/sign-in/password", new() { ["email"] = faker.Internet.Email(), ["next"] = string.Empty });
        await Assert.That(password.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // The platform's pages keep the TerraScale brand and every method.
        using var platform = await oidc.Client.GetAsync($"/ui/{OidcIssuer.Slug}/sign-in");
        var platformPage = await platform.Content.ReadAsStringAsync();
        await Assert.That(platformPage).Contains("Sign in to TerraScale");
        await Assert.That(platformPage).Contains("Create account");
        await Assert.That(platformPage).DoesNotContain(HtmlEncoder.Default.Encode(brand));
    }

    [Test]
    public async Task Password_only_pages_offer_no_email_code_action_or_recovery_advice()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var faker = new Faker();
        var (_, token) = await oidc.SignInUserAsync(faker.Internet.Email().ToLowerInvariant());
        var project = await oidc.CreateProjectAsync(token, await oidc.CreateOrganizationAsync(token), faker.Commerce.ProductName());
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/settings", token, new { methods = new { passcode = false, password = true, sign_up = false } });
        var email = faker.Internet.Email().ToLowerInvariant();
        using var password = await oidc.PostFormAsync($"/ui/{project}/sign-in/password", new() { ["email"] = email });
        var page = await password.Content.ReadAsStringAsync();
        await Assert.That(password.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page).Contains("Forgot password?");
        await Assert.That(page).DoesNotContain("Email me a sign-in code");
        await Assert.That(page).DoesNotContain("Or get a one-time code by email");
        using var wrong = await oidc.PostFormAsync($"/ui/{project}/sign-in/password", new() { ["email"] = email, ["password"] = "synthetic wrong password" });
        await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await wrong.Content.ReadAsStringAsync()).DoesNotContain("email yourself a sign-in code");
    }

    [Test]
    public async Task Unknown_project_tenants_serve_no_pages_or_endpoints()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var unknown = AuthStore.NewId();
        foreach (var path in new[] { $"/ui/{unknown}/sign-in", $"/ui/{unknown}/sign-up", $"/{unknown}/.well-known/openid-configuration", $"/{unknown}/oauth/authorize?client_id=x" })
        {
            using var response = await oidc.Client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task Deleting_a_project_token_removes_its_reference_claim_and_preserves_the_platform_token()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, ownerToken) = await oidc.SignInUserAsync(new Faker().Internet.Email().ToLowerInvariant());
        var project = await oidc.CreateProjectAsync(ownerToken, await oidc.CreateOrganizationAsync(ownerToken), "Token deletion");
        var services = oidc.Server.App.Services;
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        using var scope = services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenStore<OidcToken>>();
        var store = scope.ServiceProvider.GetRequiredService<AuthStore>();
        var reference = AuthStore.NewId();
        var platformToken = new OidcToken { ReferenceId = reference };
        var projectToken = new OidcToken { ReferenceId = reference };
        var previousContext = accessor.HttpContext;
        try
        {
            accessor.HttpContext = null;
            await tokens.CreateAsync(platformToken, cancellationToken);
            var context = new DefaultHttpContext();
            context.Items[Tenancy.ItemKey] = project;
            accessor.HttpContext = context;
            await tokens.CreateAsync(projectToken, cancellationToken);
            await Assert.That(await store.FindUniqueOwnerAsync(project, "oidc_token_reference", reference, cancellationToken)).IsEqualTo(projectToken.Id);

            await tokens.DeleteAsync(projectToken, cancellationToken);

            await Assert.That(await tokens.FindByIdAsync(projectToken.Id!, cancellationToken)).IsNull();
            await Assert.That(await store.FindUniqueOwnerAsync(project, "oidc_token_reference", reference, cancellationToken)).IsNull();
            accessor.HttpContext = null;
            await Assert.That(await tokens.FindByIdAsync(platformToken.Id!, cancellationToken)).IsNotNull();
            await Assert.That(await store.FindUniqueOwnerAsync(OidcIssuer.InstanceId, "oidc_token_reference", reference, cancellationToken)).IsEqualTo(platformToken.Id);
        }
        finally
        {
            accessor.HttpContext = previousContext;
        }
    }

}
