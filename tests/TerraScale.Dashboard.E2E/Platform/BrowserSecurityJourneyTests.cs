using System.Net;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardE2EFixture>]
public sealed class BrowserSecurityJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task PagesAssetsErrorsAndApisHaveTheSameBrowserProtections()
    {
        using var client = fixture.CreateClient();
        foreach (var path in new[] { "/login", "/css/terrascale.css", "/api/v1/auth/me", "/.env", "/.git/HEAD", "/debug", "/metrics" })
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
            if (path == "/login")
            {
                var csrf = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("ts-dashboard-csrf=", StringComparison.Ordinal));
                await Assert.That(csrf.ToLowerInvariant()).Contains("secure");
                await Assert.That(csrf.ToLowerInvariant()).Contains("httponly");
                await Assert.That(csrf.ToLowerInvariant()).Contains("samesite=strict");
            }
        }
    }
}
