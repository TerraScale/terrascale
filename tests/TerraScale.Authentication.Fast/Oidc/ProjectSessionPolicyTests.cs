using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Tests;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ProjectSessionPolicyTests(TerraBaseFixture fixture)
{
    private const string Callback = "https://app.example.test/callback";





    [Test]
    public async Task Project_lifetimes_apply_on_issuance_resolution_and_rotation_without_affecting_other_tenants()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (ownerToken, project) = await ProjectAsync(oidc);
        var other = await oidc.CreateProjectAsync(ownerToken, await oidc.CreateOrganizationAsync(ownerToken), "Other project");
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var user = (await users.CreateAsync(project, new Faker().Internet.Email()))!;
        var platform = await sessions.CreateAsync((await users.CreateAsync(OidcIssuer.InstanceId, new Faker().Internet.Email()))!);
        var bystander = await sessions.CreateAsync((await users.CreateAsync(other, user.Email))!);
        await ConfigureAsync(oidc, ownerToken, project, 1200);
        var issued = await sessions.CreateAsync(user);
        await Assert.That(issued.Session.ExpiresAt).IsEqualTo(issued.Session.CreatedAt.AddSeconds(1200));

        clock.UtcNow += TimeSpan.FromSeconds(120);
        var rotated = (await sessions.RotateAsync(project, issued.Token))!;
        await Assert.That(rotated.Session.CreatedAt).IsEqualTo(issued.Session.CreatedAt);
        await Assert.That(rotated.Session.ExpiresAt).IsEqualTo(issued.Session.ExpiresAt);
        await ConfigureAsync(oidc, ownerToken, project, 300);
        var effective = (await sessions.ResolveAsync(project, rotated.Token))!.Value.Session;
        await Assert.That(effective.ExpiresAt).IsEqualTo(issued.Session.CreatedAt.AddSeconds(300));
        rotated = (await sessions.RotateAsync(project, rotated.Token))!;
        await Assert.That(rotated.Session.CreatedAt).IsEqualTo(issued.Session.CreatedAt);
        await Assert.That(rotated.Session.ExpiresAt).IsEqualTo(effective.ExpiresAt);
        var fresh = await sessions.CreateAsync(user);
        await Assert.That(fresh.Session.ExpiresAt).IsEqualTo(clock.UtcNow.AddSeconds(300));

        clock.UtcNow = effective.ExpiresAt;
        await Assert.That(await sessions.ResolveAsync(project, rotated.Token)).IsNull();
        await Assert.That(await sessions.RotateAsync(project, rotated.Token)).IsNull();
        await Assert.That(await sessions.RevokeAsync(project, rotated.Token)).IsTrue();
        await Assert.That(await sessions.ResolveAsync(project, fresh.Token)).IsNotNull();
        await Assert.That(await sessions.ResolveAsync(other, fresh.Token)).IsNull();
        await Assert.That((await sessions.ResolveAsync(other, bystander.Token))!.Value.Session.ExpiresAt).IsEqualTo(bystander.Session.CreatedAt + Sessions.Lifetime);
        await Assert.That((await sessions.ResolveAsync(OidcIssuer.InstanceId, platform.Token))!.Value.Session.ExpiresAt).IsEqualTo(platform.Session.CreatedAt + Sessions.Lifetime);
    }



    private static long AuthenticationTime(JsonElement tokens) =>
        new JsonWebToken(tokens.GetProperty("access_token").GetString()!).GetPayloadValue<long>("auth_time");

    private static async Task<DateTimeOffset> RefreshExpiryAsync(OidcHarness oidc, string project) =>
        (await oidc.Database.ListDocumentsAsync("oidc_tokens", limit: 1000)).Items.Select(item => item.Document)
            .Single(token => token.GetProperty("instanceId").GetString() == project && token.GetProperty("type").GetString() == TokenTypeIdentifiers.RefreshToken
                && token.GetProperty("status").GetString() == "valid").GetProperty("expirationDate").GetDateTimeOffset();

    private static async Task<Dictionary<string, string>> GrantAsync(OidcHarness oidc, string project, string client, string session, bool refresh)
    {
        var (verifier, challenge) = OidcHarness.Pkce();
        using var authorize = await AuthorizeAsync(oidc, project, client, session, challenge, Callback);
        Dictionary<string, string> grant = new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = OidcHarness.Query(authorize.Headers.Location!)["code"]!,
            ["client_id"] = client,
            ["redirect_uri"] = Callback,
            ["code_verifier"] = verifier,
        };
        if (!refresh)
        {
            return grant;
        }

        using var redeemed = await oidc.PostTokenAsync(grant, null, null, tenant: project);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return new() { ["grant_type"] = "refresh_token", ["refresh_token"] = (await OidcHarness.JsonAsync(redeemed)).GetProperty("refresh_token").GetString()!, ["client_id"] = client };
    }

    private static async Task AssertInvalidGrantAsync(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await OidcHarness.JsonAsync(response)).GetProperty("error").GetString()).IsEqualTo("invalid_grant");
    }

    private static async Task<string> RenewOwnerAsync(OidcHarness oidc, string token)
    {
        var session = await oidc.SignInAsync(new JsonWebToken(token).GetPayloadValue<string>("email"));
        var (verifier, challenge) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, challenge), verifier);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return (await OidcHarness.JsonAsync(redeemed)).GetProperty("access_token").GetString()!;
    }

    private static async Task<(string OwnerToken, string Project)> ProjectAsync(OidcHarness oidc)
    {
        var (_, token) = await oidc.SignInUserAsync(new Faker().Internet.Email().ToLowerInvariant());
        return (token, await oidc.CreateProjectAsync(token, await oidc.CreateOrganizationAsync(token), "Policy project"));
    }

    private static async Task ConfigureAsync(OidcHarness oidc, string token, string project, int seconds, params string[] redirects) =>
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/settings", token,
            new { configuration = new { redirect_urls = redirects, session_lifetime_seconds = seconds } });

    private static async Task<string> ClientAsync(OidcHarness oidc, string token, string project, params string[] redirects) =>
        (await oidc.OkAsync(HttpMethod.Post, $"/v1/{project}/clients", token,
            new { name = "Policy app", type = "public", redirect_uris = redirects }, HttpStatusCode.Created)).GetProperty("client_id").GetString()!;

    private static async Task<HttpResponseMessage> AuthorizeAsync(OidcHarness oidc, string project, string client, string session, string challenge,
        string redirect, string? extra = null, string scope = "openid email offline_access")
    {
        var path = OidcHarness.AuthorizePath(challenge, redirectUri: redirect, clientId: client, extra: extra, scope: scope)
            .Replace($"/{OidcIssuer.Slug}/", $"/{project}/", StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Get, path) { Headers = { { "Cookie", $"tsauth_session_{project}={session}" } } };
        return await oidc.Client.SendAsync(request);
    }
}
