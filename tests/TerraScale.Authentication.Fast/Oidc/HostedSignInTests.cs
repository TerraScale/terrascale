using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bogus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Microsoft.Playwright;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using IResult = Microsoft.AspNetCore.Http.IResult;
using SameSiteMode = Microsoft.Net.Http.Headers.SameSiteMode;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class HostedSignInTests(TerraBaseFixture fixture)
{
    private const string SignIn = $"/ui/{OidcIssuer.Slug}/sign-in";

    [ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
    public required BrowserFixture Chromium { get; init; }

    [Test]
    public async Task A_wrong_code_is_refused_and_the_right_one_sets_a_locked_down_session_cookie()
    {
        await using var oidc = await OidcHarness.StartSharedManagementAsync(fixture);
        var user = await oidc.CreateUserAsync(oidc.UniqueEmail("alice"));
        using var requested = await oidc.PostFormAsync(SignIn, new() { ["email"] = user.Email, ["next"] = "https://evil.example/" });
        await Assert.That(await requested.Content.ReadAsStringAsync()).DoesNotContain("evil.example");
        var code = await oidc.Email.NextCodeAsync(user.Email);

        using var wrong = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = user.Email, ["code"] = code == "000000" ? "111111" : "000000", ["next"] = string.Empty });
        await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(OidcHarness.SessionCookie(wrong)).IsNull();

        using var verified = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = user.Email, ["code"] = code, ["next"] = "//evil.example/terrascale/oauth/authorize?x=1" });
        var page = await verified.Content.ReadAsStringAsync();
        await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page).Contains("You are signed in.");
        await Assert.That(page).DoesNotContain("evil.example");

        var cookie = SetCookieHeaderValue.ParseList([.. verified.Headers.GetValues("Set-Cookie")]).Single(value => value.Name == "tsauth_session");
        await Assert.That(cookie.Secure).IsTrue();
        await Assert.That(cookie.HttpOnly).IsTrue();
        await Assert.That(cookie.SameSite).IsEqualTo(SameSiteMode.Lax);
        await Assert.That(cookie.Path.ToString()).IsEqualTo("/");
        await Assert.That(cookie.Value.ToString()).IsEqualTo(OidcHarness.SessionCookie(verified)!);
    }


    // Starts a hosted sign-up and returns the pending token its verify page carries.

    // Every hosted email-code start shares the same mail cooldown.

    private static async Task<string?> PasswordSignInAsync(OidcHarness oidc, string address, string password)
    {
        using var response = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in/password", new() { ["email"] = address, ["password"] = password, ["next"] = string.Empty });
        return OidcHarness.SessionCookie(response);
    }


    // Erases the two values a fresh response always regenerates, so the remaining bytes must match.
    private static string Normalize(string page) =>
        RequestIdHint().Replace(NonceAttribute().Replace(page, "nonce=\"N\""), "Request ID: R");

    [GeneratedRegex("name=\"pending_token\" value=\"([^\"]+)\"")]
    private static partial Regex PendingToken();

    [GeneratedRegex("nonce=\"[^\"]+\"")]
    private static partial Regex NonceAttribute();

    [GeneratedRegex("Request ID: [^<]+")]
    private static partial Regex RequestIdHint();
}
