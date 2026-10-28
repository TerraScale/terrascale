using System.Net;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Identity;

/// <summary><c>POST /v1/terrascale/account/sign-out</c> ends every sign-in of the account on every device.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class SignOutEverywhereTests(TerraBaseFixture fixture)
{
    private const string SignOut = "/v1/terrascale/account/sign-out";

    [Test]
    public async Task Signing_out_everywhere_ends_every_device_session_and_token_family()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, laptop, laptopTokens) = await oidc.SignInAndRedeemAsync("dana@example.test");
        clock.UtcNow += HostedAuth.MailCooldown;
        var phone = await oidc.SignInAsync(user.Email);
        var (verifier, challenge) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(phone, challenge), verifier);
        var phoneTokens = await OidcHarness.JsonAsync(redeemed);
        var laptopAccess = laptopTokens.GetProperty("access_token").GetString()!;
        var phoneAccess = phoneTokens.GetProperty("access_token").GetString()!;
        await oidc.OkAsync(HttpMethod.Get, "/v1/terrascale/account/mfa", phoneAccess);

        await Assert.That(await oidc.FailAsync(HttpMethod.Post, SignOut, null)).IsEqualTo((401, "unauthorized"));
        await oidc.OkAsync(HttpMethod.Post, SignOut, laptopAccess, status: HttpStatusCode.NoContent);

        // Both devices' access tokens, refresh tokens and hosted sessions are refused.
        foreach (var access in new[] { laptopAccess, phoneAccess })
        {
            await Assert.That((await oidc.FailAsync(HttpMethod.Get, "/v1/terrascale/account/mfa", access)).Status).IsEqualTo(401);
            await Assert.That((await oidc.FailAsync(HttpMethod.Post, SignOut, access)).Status).IsEqualTo(401);
        }

        foreach (var tokens in new[] { laptopTokens, phoneTokens })
        {
            using var refresh = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
            await Assert.That(refresh.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        foreach (var session in new[] { laptop, phone })
        {
            using var authorize = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge), session);
            await Assert.That(authorize.Headers.Location?.OriginalString ?? string.Empty).DoesNotStartWith(OidcHarness.RedirectUri);
        }

        // A new sign-in works again.
        clock.UtcNow += HostedAuth.MailCooldown;
        var fresh = await oidc.SignInAsync(user.Email);
        (verifier, challenge) = OidcHarness.Pkce();
        using var again = await oidc.RedeemCodeAsync(await oidc.CodeAsync(fresh, challenge), verifier);
        await oidc.OkAsync(HttpMethod.Get, "/v1/terrascale/account/mfa", (await OidcHarness.JsonAsync(again)).GetProperty("access_token").GetString()!);
    }
}
