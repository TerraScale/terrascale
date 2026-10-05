using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.SignIn;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class HostedSignInTests(TerraBaseFixture fixture)
{
    private const string SignIn = $"/ui/{OidcIssuer.Slug}/sign-in";

    [Test]
    public async Task Cross_origin_posts_and_unknown_addresses_send_no_mail()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var form = new Dictionary<string, string> { ["email"] = "alice@example.test", ["next"] = string.Empty };

        foreach (var origin in new[] { null, "https://evil.example" })
        {
            using var refused = await oidc.PostFormAsync(SignIn, form, origin);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            using var refusedVerify = await oidc.PostFormAsync($"{SignIn}/verify", new(form) { ["code"] = "123456" }, origin);
            await Assert.That(refusedVerify.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(OidcHarness.SessionCookie(refusedVerify)).IsNull();
        }

        using var known = await oidc.PostFormAsync(SignIn, form);
        using var unknown = await oidc.PostFormAsync(SignIn, new() { ["email"] = "nobody@example.test", ["next"] = string.Empty });
        await Assert.That(unknown.StatusCode).IsEqualTo(known.StatusCode);
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await unknown.Content.ReadAsStringAsync()).Contains("If an account exists for <strong>nobody@example.test</strong>");

        // Exactly one mail went out: the one for the known address from the same-origin post.
        var (recipient, _) = await oidc.Email.NextAsync();
        await Assert.That(recipient).IsEqualTo("alice@example.test");
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task A_wrong_code_is_refused_and_the_right_one_sets_a_locked_down_session_cookie()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        using var requested = await oidc.PostFormAsync(SignIn, new() { ["email"] = "alice@example.test", ["next"] = "https://evil.example/" });
        await Assert.That(await requested.Content.ReadAsStringAsync()).DoesNotContain("evil.example");
        var code = await oidc.Email.NextCodeAsync("alice@example.test");

        using var wrong = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = "alice@example.test", ["code"] = code == "000000" ? "111111" : "000000", ["next"] = string.Empty });
        await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(OidcHarness.SessionCookie(wrong)).IsNull();

        using var verified = await oidc.PostFormAsync($"{SignIn}/verify", new() { ["email"] = "alice@example.test", ["code"] = code, ["next"] = "//evil.example/terrascale/oauth/authorize?x=1" });
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

    [Test]
    public async Task The_origin_check_compares_scheme_ascii_host_and_effective_port()
    {
        // The configured origin has an explicit default port, upper case and a Unicode host; a browser sends the
        // serialized ASCII form without the default port.
        await using var oidc = await OidcHarness.StartAsync(fixture, values => values["public-origin"] = "https://Bücher.Test.Invalid:443");
        await oidc.CreateUserAsync();
        var form = new Dictionary<string, string> { ["email"] = "alice@example.test", ["next"] = string.Empty };

        foreach (var origin in new[]
        {
            "https://xn--bcher-kva.test.invalid:8443", "http://xn--bcher-kva.test.invalid", "https://xn--bcher-kva.test.invalid/",
            "https://user@xn--bcher-kva.test.invalid", "https://auth.test.invalid", "null",
        })
        {
            using var refused = await oidc.PostFormAsync(SignIn, form, origin);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }

        await Assert.That(oidc.Email.HasPending).IsFalse();
        foreach (var origin in new[] { "https://xn--bcher-kva.test.invalid", "https://XN--BCHER-KVA.test.invalid:443" })
        {
            using var accepted = await oidc.PostFormAsync(SignIn, form, origin);
            await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await oidc.Email.NextAsync()).Recipient).IsEqualTo("alice@example.test");
        }
    }

    [Test]
    public async Task Codes_go_out_after_the_response_one_at_a_time_and_queued_ones_are_sent_when_the_host_stops()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var email = new CapturingEmailSender { Gate = gate };
        await using var oidc = await OidcHarness.StartAsync(fixture, email: email);
        await oidc.CreateUserAsync("alice@example.test");
        await oidc.CreateUserAsync("bob@example.test");

        // Both responses arrive while delivery is blocked: no request waits for its mail.
        foreach (var address in new[] { "alice@example.test", "bob@example.test" })
        {
            using var response = await oidc.PostFormAsync(SignIn, new() { ["email"] = address, ["next"] = string.Empty });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        // One worker: the second code waits for the first send instead of opening another connection.
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (email.InFlight == 0)
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        await Task.Delay(200);
        await Assert.That(email.MaxInFlight).IsEqualTo(1);

        // Stopping waits for the queued code instead of dropping it.
        var stopping = oidc.Server.App.Services.GetRequiredService<SignInMail>().StopAsync(CancellationToken.None);
        await Assert.That(stopping.IsCompleted).IsFalse();
        gate.SetResult();
        await stopping;
        string[] delivered = [(await email.NextAsync()).Recipient, (await email.NextAsync()).Recipient];
        await Assert.That(delivered).IsEquivalentTo(["alice@example.test", "bob@example.test"]);
        await Assert.That(email.MaxInFlight).IsEqualTo(1);
    }

    [Test]
    public async Task The_sign_in_page_drops_a_next_that_is_not_this_issuers_authorize_url()
    {
        await using var server = await AuthTestServer.StartAsync();

        foreach (var next in new[] { "https://evil.example/", "//evil.example/terrascale/oauth/authorize?x=1", "/terrascale/oauth/authorize?x=1#https://evil.example/", "/terrascale/oauth/authorize?x=1\\\\evil.example/" })
        {
            using var page = await server.Client.GetAsync($"{SignIn}?next={Uri.EscapeDataString(next)}");
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await page.Content.ReadAsStringAsync()).DoesNotContain("evil.example");
        }
    }
}
