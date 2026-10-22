using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class StatusClientTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Startup_registers_one_secretless_status_client_without_dashboard_configuration()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, values =>
        {
            values.Remove("dashboard-client-secret");
            values.Remove("dashboard-redirect-uris");
            values.Remove("dashboard-post-logout-redirect-uris");
        });
        await oidc.Server.App.Services.GetRequiredService<ClientRegistration>().RegisterAllAsync(CancellationToken.None);

        var stored = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcApplicationCollection)).Items.Single().Document;
        await Assert.That(stored.GetProperty("clientId").GetString()).IsEqualTo(OidcIssuer.StatusClientId);
        await Assert.That(stored.GetProperty("clientType").GetString()).IsEqualTo("public");
        await Assert.That(stored.GetProperty("clientSecret").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        await Assert.That(stored.GetProperty("redirectUris").EnumerateArray().Select(uri => uri.GetString()!))
            .IsEquivalentTo([OidcIssuer.StatusRedirectUri]);
    }

    [Test]
    public async Task Status_code_requires_the_exact_redirect_and_s256_and_redeems_without_a_client_secret()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var session = await oidc.SignInAsync(AuthTestServer.BootstrapOwnerEmail);
        var (verifier, challenge) = OidcHarness.Pkce();
        var path = OidcHarness.AuthorizePath(challenge, redirectUri: OidcIssuer.StatusRedirectUri,
            scope: "openid email profile", clientId: OidcIssuer.StatusClientId);

        foreach (var invalid in new[]
        {
            path.Replace(Uri.EscapeDataString(OidcIssuer.StatusRedirectUri), Uri.EscapeDataString(OidcIssuer.StatusRedirectUri + "/"), StringComparison.Ordinal),
            path.Replace($"&code_challenge={challenge}&code_challenge_method=S256", string.Empty, StringComparison.Ordinal),
            path.Replace("code_challenge_method=S256", "code_challenge_method=plain", StringComparison.Ordinal),
        })
        {
            using var refused = await oidc.AuthorizeAsync(invalid, session);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(refused.Headers.Location).IsNull();
        }

        using var authorized = await oidc.AuthorizeAsync(path, session);
        await Assert.That(authorized.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(authorized.Headers.Location!.GetLeftPart(UriPartial.Path)).IsEqualTo(OidcIssuer.StatusRedirectUri);
        var code = OidcHarness.Query(authorized.Headers.Location!)["code"]!;
        using var redeemed = await oidc.PostTokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = OidcIssuer.StatusClientId,
            ["code"] = code,
            ["redirect_uri"] = OidcIssuer.StatusRedirectUri,
            ["code_verifier"] = verifier,
        }, clientId: null, secret: null);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(redeemed);
        var organization = await oidc.OkAsync(HttpMethod.Get, $"{PlatformCalls.Organizations}/{PlatformBootstrap.OrganizationId}",
            tokens.GetProperty("access_token").GetString()!);
        await Assert.That(organization.GetProperty("role").GetString()).IsEqualTo(Roles.Owner);
        await Assert.That(tokens.TryGetProperty("refresh_token", out _)).IsFalse();

        using var serviceGrant = await oidc.PostTokenAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = OidcIssuer.StatusClientId,
        }, clientId: null, secret: null);
        await Assert.That(serviceGrant.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(serviceGrant)).IsEqualTo("unauthorized_client");
    }
}
