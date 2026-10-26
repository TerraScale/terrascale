using System.Net;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardE2EFixture>]
public sealed class BrowserSecurityJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task PagesAssetsErrorsAndApisHaveTheSameBrowserProtections()
    {
        using var client = fixture.CreateClient();
        foreach (var path in new[] { "/login", "/css/terrascale.css", "/api/v1/auth/me", "/.env", "/.git/HEAD", "/debug", "/metrics", "/__test/unexpected-error", "/__test/initializer-error" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("Origin", "https://untrusted.example");
            using var response = await client.SendAsync(request);
            await Assert.That(response.Headers.GetValues("Strict-Transport-Security").Single()).IsEqualTo("max-age=31536000");
            await Assert.That(response.Headers.GetValues("X-Frame-Options").Single()).IsEqualTo("DENY");
            await Assert.That(response.Headers.GetValues("X-Content-Type-Options").Single()).IsEqualTo("nosniff");
            await Assert.That(response.Headers.GetValues("Referrer-Policy").Single()).IsEqualTo("same-origin");
            await Assert.That(response.Headers.GetValues("Permissions-Policy").Single()).Contains("camera=()");
            await Assert.That(response.Headers.GetValues("Content-Security-Policy").Single()).Contains("frame-ancestors 'none'");
            await Assert.That(response.Headers.Contains("Access-Control-Allow-Origin")).IsFalse();
            await Assert.That(response.Headers.Contains("Access-Control-Allow-Credentials")).IsFalse();
            if (path.StartsWith("/.", StringComparison.Ordinal) || path is "/debug" or "/metrics")
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            if (path is "/__test/unexpected-error" or "/__test/initializer-error")
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
                await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
                var html = await response.Content.ReadAsStringAsync();
                await Assert.That(html).DoesNotContain("Synthetic exception detail");
                var nonce = System.Text.RegularExpressions.Regex.Match(html, "nonce=\"([^\"]+)\"").Groups[1].Value;
                nonce = System.Net.WebUtility.HtmlDecode(nonce); // Razor encodes "+" in the attribute
                await Assert.That(nonce.Length).IsGreaterThan(0);
                await Assert.That(response.Headers.GetValues("Content-Security-Policy").Single()).Contains("'nonce-" + nonce + "'");
            }
            if (path == "/login")
            {
                // /login only starts ts-auth's hosted sign-in; its flow cookie must survive the top-level return to /login/callback, so it is Lax.
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
                var flow = response.Headers.GetValues("Set-Cookie")
                    // Starting a flow first expires any earlier flow cookie, so skip that empty deletion.
                    .Single(value => value.StartsWith("ts-dashboard-oidc=", StringComparison.Ordinal) && !value.StartsWith("ts-dashboard-oidc=;", StringComparison.Ordinal));
                await Assert.That(flow.ToLowerInvariant()).Contains("secure");
                await Assert.That(flow.ToLowerInvariant()).Contains("httponly");
                await Assert.That(flow.ToLowerInvariant()).Contains("samesite=lax");
            }
        }
    }
}
