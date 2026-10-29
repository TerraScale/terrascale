using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.Platform
{
    [ClassDataSource<DashboardE2EFixture>]
    public sealed class CsrfBindingJourneyTests(DashboardE2EFixture fixture)
    {
        [Test]
        public async Task FormProofsAreHostOnlyAndBoundToTheIssuingBrowserSession()
        {
            var first = await SignInAsync();
            var second = await SignInAsync();
            await Assert.That(first == second).IsFalse();

            using var browser = Raw(first);
            var (nonce, token, setCookie) = await ProofAsync(browser, "/profile");
            // __Host- makes browsers refuse the cookie from sibling subdomains or with a Domain attribute.
            await Assert.That(setCookie).Contains("path=/").And.Contains("secure").And.Contains("samesite=strict").And.Contains("httponly");
            await Assert.That(setCookie.Contains("domain=", StringComparison.Ordinal)).IsFalse();
            // The form proof is not the cookie value, so a planted cookie cannot pair with a guessable form field.
            await Assert.That(nonce.EndsWith("=" + token, StringComparison.Ordinal)).IsFalse();

            // Missing, wrong, cross-origin and cross-session proofs are rejected without ending the session.
            await Assert.That(await LogoutAsync(first + "; " + nonce, null)).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await LogoutAsync(first + "; " + nonce, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)))).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await LogoutAsync(first + "; " + nonce, token, "https://evil.example")).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await LogoutAsync(first + "; " + nonce, token, sameOriginCookie: false)).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await LogoutAsync(second + "; " + nonce, token)).IsEqualTo(HttpStatusCode.Forbidden);
            using (var me = Raw(first))
            using (var still = await me.GetAsync("/api/v1/auth/me"))
            {
                await Assert.That(still.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            await Assert.That(await LogoutAsync(first + "; " + nonce, token)).IsEqualTo(HttpStatusCode.SeeOther);
        }

        private async Task<string> SignInAsync()
        {
            using var client = fixture.CreateClient();
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            return login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal)).Split(';')[0];
        }

        // A cookie-less browser receives a fresh nonce cookie together with the page's form proof.
        private static async Task<(string Nonce, string Token, string SetCookie)> ProofAsync(HttpClient client, string path)
        {
            using var page = await client.GetAsync(path);
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var set = page.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-ts-dashboard-csrf=", StringComparison.Ordinal));
            var token = WebUtility.HtmlDecode(Regex.Match(await page.Content.ReadAsStringAsync(), "name=\"_csrf\" value=\"([^\"]+)\"").Groups[1].Value);
            await Assert.That(token.Length).IsEqualTo(64);
            return (set.Split(';')[0], token, set.ToLowerInvariant());
        }

        private async Task<HttpStatusCode> LogoutAsync(string cookie, string? token, string? origin = null, bool sameOriginCookie = true)
        {
            using var client = Raw(cookie);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout")
            { Content = new FormUrlEncodedContent(token is null ? [] : new Dictionary<string, string> { ["_csrf"] = token }) };
            request.Headers.Add("Origin", origin ?? client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            if (!sameOriginCookie)
            {
                request.Headers.Add("Sec-Fetch-Site", "cross-site");
            }

            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        private HttpClient Raw(string cookie)
        {
            var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, UseCookies = false })
            { BaseAddress = fixture.Origin };
            if (cookie.Length > 0)
            {
                client.DefaultRequestHeaders.Add("Cookie", cookie);
            }

            return client;
        }
    }
}
