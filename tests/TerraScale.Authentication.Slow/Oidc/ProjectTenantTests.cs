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

namespace TerraScale.Authentication.Slow.Oidc;

/// <summary>A project's end users sign in to the project's own applications at its own issuer (project-tenant-management.md).</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ProjectTenantTests(TerraBaseFixture fixture)
{

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
