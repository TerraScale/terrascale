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

namespace TerraScale.Authentication.Slow.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ProjectSessionPolicyTests(TerraBaseFixture fixture)
{
    private const string Callback = "https://app.example.test/callback";
    private const string Blocked = "https://app.example.test/other";
    private const string Unregistered = "https://app.example.test/unregistered";

    [Test]
    [Arguments(120)]
    [Arguments(301)]
    public async Task Observed_browser_session_reductions_survive_expiry_and_policy_increases(int observedAt)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (ownerToken, project) = await ProjectAsync(oidc);
        await ConfigureAsync(oidc, ownerToken, project, 1200);
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var user = (await users.CreateAsync(project, new Faker().Internet.Email()))!;
        var session = await sessions.CreateAsync(user);
        var client = await ClientAsync(oidc, ownerToken, project, Callback);
        await ConfigureAsync(oidc, ownerToken, project, 300);
        clock.UtcNow = session.Session.CreatedAt.AddSeconds(observedAt);
        var observed = await sessions.ResolveAsync(project, session.Token);
        await Assert.That(observed is not null).IsEqualTo(observedAt < 300);
        var stored = (await oidc.Database.ListDocumentsAsync("sessions", limit: 1000)).Items
            .Single(item => item.Document.GetProperty("tokenHash").GetString() == session.Session.TokenHash).Document;
        await Assert.That(stored.GetProperty("expiresAt").GetDateTimeOffset()).IsEqualTo(session.Session.CreatedAt.AddSeconds(300));

        ownerToken = await RenewOwnerAsync(oidc, ownerToken);
        await ConfigureAsync(oidc, ownerToken, project, 1200);
        clock.UtcNow = session.Session.CreatedAt.AddSeconds(301);
        await Assert.That(await sessions.ResolveAsync(project, session.Token)).IsNull();
        await Assert.That(await sessions.RotateAsync(project, session.Token)).IsNull();
        var (_, challenge) = OidcHarness.Pkce();
        using var rejected = await AuthorizeAsync(oidc, project, client, session.Token, challenge, Callback, "&prompt=none");
        await Assert.That(OidcHarness.Query(rejected.Headers.Location!)["error"]).IsEqualTo("login_required");
        await Assert.That(await sessions.ResolveAsync(project, (await sessions.CreateAsync(user)).Token)).IsNotNull();
    }

    [Test]
    public async Task Administrative_session_pages_show_current_policy_expiry_and_exclude_policy_expired_sessions()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (ownerToken, project) = await ProjectAsync(oidc);
        await ConfigureAsync(oidc, ownerToken, project, 1200);
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var user = (await users.CreateAsync(project, new Faker().Internet.Email()))!;
        var issued = await sessions.CreateAsync(user);
        await ConfigureAsync(oidc, ownerToken, project, 300);
        var listed = await oidc.OkAsync(HttpMethod.Get, $"/v1/{project}/sessions?user={user.Id}", ownerToken);
        await Assert.That(listed.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await Assert.That(listed.GetProperty("items")[0].GetProperty("expires_at").GetDateTimeOffset())
            .IsEqualTo(issued.Session.CreatedAt.AddSeconds(300));

        clock.UtcNow = issued.Session.CreatedAt.AddSeconds(301);
        ownerToken = await RenewOwnerAsync(oidc, ownerToken);
        listed = await oidc.OkAsync(HttpMethod.Get, $"/v1/{project}/sessions?user={user.Id}", ownerToken);
        await Assert.That(listed.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await sessions.CreateAsync(user);
        listed = await oidc.OkAsync(HttpMethod.Get, $"/v1/{project}/sessions?user={user.Id}", ownerToken);
        await Assert.That(listed.GetProperty("items").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Policy_expired_code_and_refresh_grants_cannot_revive_after_a_policy_increase(bool refresh)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (ownerToken, project) = await ProjectAsync(oidc);
        var client = await ClientAsync(oidc, ownerToken, project, Callback);
        await ConfigureAsync(oidc, ownerToken, project, 1200);
        var user = (await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync(project, new Faker().Internet.Email()))!;
        var session = await oidc.Server.App.Services.GetRequiredService<Sessions>().CreateAsync(user);
        // Obtain the code near the shorter deadline so its own one-minute lifetime has not elapsed.
        clock.UtcNow = session.Session.CreatedAt.AddSeconds(290);
        var grant = await GrantAsync(oidc, project, client, session.Token, refresh);
        ownerToken = await RenewOwnerAsync(oidc, ownerToken);
        await ConfigureAsync(oidc, ownerToken, project, 300);
        clock.UtcNow = session.Session.CreatedAt.AddSeconds(301);
        using var expired = await oidc.PostTokenAsync(grant, null, null, tenant: project);
        await AssertInvalidGrantAsync(expired);
        var authorization = (await oidc.Database.ListDocumentsAsync("oidc_authorizations", limit: 1000)).Items
            .Single(item => item.Document.GetProperty("instanceId").GetString() == project).Document;
        await Assert.That(authorization.GetProperty("status").GetString()).IsEqualTo("revoked");

        clock.UtcNow += HostedAuth.MailCooldown;
        ownerToken = await RenewOwnerAsync(oidc, ownerToken);
        await ConfigureAsync(oidc, ownerToken, project, 1200);
        using var replay = await oidc.PostTokenAsync(grant, null, null, tenant: project);
        await AssertInvalidGrantAsync(replay);
        var fresh = await oidc.Server.App.Services.GetRequiredService<Sessions>().CreateAsync(user);
        var freshGrant = await GrantAsync(oidc, project, client, fresh.Token, refresh);
        using var freshRedemption = await oidc.PostTokenAsync(freshGrant, null, null, tenant: project);
        await Assert.That(freshRedemption.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(false, long.MaxValue)]
    [Arguments(true, long.MaxValue)]
    [Arguments(false, long.MinValue)]
    [Arguments(true, long.MinValue)]
    [Arguments(false, 253402300800L)]
    [Arguments(true, 253402300800L)]
    public async Task Extreme_signed_authentication_times_fail_with_invalid_grant(bool refresh, long authenticationTime)
    {
        // Fault injection is confined to the test host: produce a genuine signed/encrypted grant with a bad auth_time.
        await using var oidc = await OidcHarness.StartAsync(fixture, services: services => services.AddOpenIddict().AddServer(server =>
            server.AddEventHandler<GenerateTokenContext>(handler => handler.UseInlineHandler(context =>
            {
                if (context.TokenType == (refresh ? TokenTypeIdentifiers.RefreshToken : TokenTypeIdentifiers.Private.AuthorizationCode)
                    && Tenancy.IsProject(Tenancy.Current(context.Transaction.GetHttpRequest()?.HttpContext)))
                {
                    context.Principal!.SetClaim(Claims.AuthenticationTime, authenticationTime);
                }

                return default;
            }).SetOrder(OpenIddictServerHandlers.Protection.AttachSecurityCredentials.Descriptor.Order - 1))));
        var (ownerToken, project) = await ProjectAsync(oidc);
        var client = await ClientAsync(oidc, ownerToken, project, Callback);
        var user = (await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync(project, new Faker().Internet.Email()))!;
        var session = await oidc.Server.App.Services.GetRequiredService<Sessions>().CreateAsync(user);
        var grant = await GrantAsync(oidc, project, client, session.Token, refresh);
        using var rejected = await oidc.PostTokenAsync(grant, null, null, tenant: project);
        await AssertInvalidGrantAsync(rejected);
    }

    [Test]
    public async Task Redirect_policy_narrows_registration_and_protocol_redirects_but_preserves_client_validation_and_empty_defaults()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (ownerToken, project) = await ProjectAsync(oidc);
        var client = await ClientAsync(oidc, ownerToken, project, Callback, Blocked, Callback + "/", Callback + "?query=1");
        await ConfigureAsync(oidc, ownerToken, project, 1200, Callback, Unregistered);
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"/v1/{project}/clients", ownerToken,
            new { name = "Blocked app", type = "public", redirect_uris = new[] { Blocked } })).IsEqualTo((400, "invalid_request"));
        await ClientAsync(oidc, ownerToken, project, Callback);
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var user = (await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync(project, new Faker().Internet.Email()))!;
        var session = await sessions.CreateAsync(user);
        var (_, challenge) = OidcHarness.Pkce();

        using var allowed = await AuthorizeAsync(oidc, project, client, session.Token, challenge, Callback);
        await Assert.That(allowed.Headers.Location!.AbsoluteUri).StartsWith(Callback + "?");
        foreach (var extra in new[] { string.Empty, "&ts_login_after=soon", "&response_mode=form_post" })
        {
            using var rejected = await AuthorizeAsync(oidc, project, client, session.Token, challenge, Blocked, extra);
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(rejected.Headers.Location).IsNull();
            await Assert.That(await rejected.Content.ReadAsStringAsync()).DoesNotContain("<form");
        }

        // This failure happens inside OpenIddict before endpoint passthrough and must also stay local.
        using var earlyError = await AuthorizeAsync(oidc, project, client, session.Token, challenge, Blocked, scope: "openid unknown_scope");
        await Assert.That(earlyError.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(earlyError.Headers.Location).IsNull();
        using var unregistered = await AuthorizeAsync(oidc, project, client, session.Token, challenge, Unregistered);
        await Assert.That(unregistered.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(unregistered.Headers.Location).IsNull();
        foreach (var variant in new[] { Callback + "/", Callback + "?query=1" })
        {
            using var exactMismatch = await AuthorizeAsync(oidc, project, client, session.Token, challenge, variant);
            await Assert.That(exactMismatch.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(exactMismatch.Headers.Location).IsNull();
        }

        using var logout = await oidc.Client.GetAsync($"/{project}/oauth/logout?client_id={client}&post_logout_redirect_uri={Uri.EscapeDataString(Blocked)}");
        await Assert.That(logout.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await logout.Content.ReadAsStringAsync()).DoesNotContain("http-equiv=\"refresh\"");

        var other = await oidc.CreateProjectAsync(ownerToken, await oidc.CreateOrganizationAsync(ownerToken), "Other project");
        await ClientAsync(oidc, ownerToken, other, Blocked);
        await ConfigureAsync(oidc, ownerToken, project, 1200);
        using var unrestricted = await AuthorizeAsync(oidc, project, client, session.Token, challenge, Blocked);
        await Assert.That(unrestricted.Headers.Location!.AbsoluteUri).StartsWith(Blocked + "?");
    }

    [Test]
    [Arguments(1200)]
    [Arguments(2592000)]
    public async Task Project_refresh_tokens_keep_real_authentication_time_and_honor_a_reduced_absolute_lifetime(int initialLifetime)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (ownerToken, project) = await ProjectAsync(oidc);
        var client = await ClientAsync(oidc, ownerToken, project, Callback);
        await ConfigureAsync(oidc, ownerToken, project, initialLifetime, Callback);
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var user = (await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync(project, new Faker().Internet.Email()))!;
        var session = await sessions.CreateAsync(user);
        clock.UtcNow += TimeSpan.FromSeconds(30);
        session = (await sessions.RotateAsync(project, session.Token))!;
        var (verifier, challenge) = OidcHarness.Pkce();
        using var tooOld = await AuthorizeAsync(oidc, project, client, session.Token, challenge, Callback, "&max_age=10&prompt=none");
        await Assert.That(OidcHarness.Query(tooOld.Headers.Location!)["error"]).IsEqualTo("login_required");
        using var pending = await AuthorizeAsync(oidc, project, client, session.Token, challenge, Callback);
        await ConfigureAsync(oidc, ownerToken, project, initialLifetime, Blocked);
        using var policyChanged = await oidc.PostTokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = OidcHarness.Query(pending.Headers.Location!)["code"]!,
            ["client_id"] = client,
            ["redirect_uri"] = Callback,
            ["code_verifier"] = verifier,
        }, null, null, tenant: project);
        await Assert.That(policyChanged.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await OidcHarness.JsonAsync(policyChanged)).GetProperty("error").GetString()).IsEqualTo("invalid_grant");
        await ConfigureAsync(oidc, ownerToken, project, initialLifetime, Callback);
        using var authorize = await AuthorizeAsync(oidc, project, client, session.Token, challenge, Callback);
        var code = OidcHarness.Query(authorize.Headers.Location!)["code"]!;
        using var redeemed = await oidc.PostTokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = client,
            ["redirect_uri"] = Callback,
            ["code_verifier"] = verifier,
        }, null, null, tenant: project);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(redeemed);
        await Assert.That(AuthenticationTime(tokens)).IsEqualTo(session.Session.CreatedAt.ToUnixTimeSeconds());
        await Assert.That(tokens.GetProperty("refresh_token").GetString()).IsNotNull();
        await Assert.That(await RefreshExpiryAsync(oidc, project)).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(AuthenticationTime(tokens)).AddSeconds(initialLifetime));

        await ConfigureAsync(oidc, ownerToken, project, 300, Callback);
        using var refreshed = await oidc.PostTokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
            ["client_id"] = client,
        }, null, null, tenant: project);
        await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var rotated = await OidcHarness.JsonAsync(refreshed);
        await Assert.That(AuthenticationTime(rotated)).IsEqualTo(AuthenticationTime(tokens));
        await Assert.That(await RefreshExpiryAsync(oidc, project)).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(AuthenticationTime(tokens)).AddSeconds(300));

        clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(AuthenticationTime(tokens)).AddSeconds(300);
        using var expired = await oidc.PostTokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = rotated.GetProperty("refresh_token").GetString()!,
            ["client_id"] = client,
        }, null, null, tenant: project);
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await OidcHarness.JsonAsync(expired)).GetProperty("error").GetString()).IsEqualTo("invalid_grant");
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
