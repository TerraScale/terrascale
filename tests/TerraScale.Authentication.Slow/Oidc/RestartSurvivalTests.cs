using System.Net;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Oidc;

/// <summary>
/// A ts-auth restart signs nobody out. Hosted sessions are opaque tokens stored in TerraBase; authorization codes,
/// refresh tokens and social state are protected with keys derived from the OCI Vault master key; access tokens are
/// signed by the key ring stored in TerraBase. None of them uses ASP.NET DataProtection, whose keys live in memory, so a
/// new process (with fresh in-memory DataProtection keys) on the same storage accepts every credential the old one issued.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RestartSurvivalTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task A_session_refresh_token_and_access_token_survive_a_restart()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, session, tokens) = await oidc.SignInAndRedeemAsync("restart@example.test");
        var access = tokens.GetProperty("access_token").GetString()!;
        var refresh = tokens.GetProperty("refresh_token").GetString()!;

        await oidc.Server.App.StopAsync();
        await using var restarted = await oidc.StartReplicaAsync();

        // The hosted session still authorizes without a new sign-in.
        var (verifier, challenge) = OidcHarness.Pkce();
        using var authorize = new HttpRequestMessage(HttpMethod.Get, OidcHarness.AuthorizePath(challenge));
        authorize.Headers.Add("Cookie", $"tsauth_session={session}");
        using var authorized = await restarted.Client.SendAsync(authorize);
        var location = authorized.Headers.Location?.AbsoluteUri ?? string.Empty;
        await Assert.That(location).StartsWith(OidcHarness.RedirectUri + "?");
        await Assert.That(location).Contains("code=");

        using var userInfo = await oidc.UserInfoAsync(access, restarted.Client);
        await Assert.That(userInfo.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var refreshed = await oidc.RefreshAsync(refresh, restarted.Client);
        await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var code = location[(location.IndexOf("code=", StringComparison.Ordinal) + 5)..].Split('&')[0];
        using var redeemed = await oidc.RedeemCodeAsync(Uri.UnescapeDataString(code), verifier, client: restarted.Client);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
