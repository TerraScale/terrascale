using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Platform;

public sealed partial class BrowserSecurityTests
{
    [Test]
    public async Task Pages_assets_apis_and_cleared_error_responses_keep_browser_protections()
    {
        await using var server = await AuthTestServer.StartAsync(routes: app =>
            app.MapGet("/__test/security-error", (Func<IResult>)(() => throw new InvalidOperationException("Synthetic error"))).ExcludeFromDescription());

        foreach (var path in new[] { "/ui/terrascale/sign-in", "/admin/admin.css", "/openapi/v1.json", "/.env", "/.git/HEAD", "/debug", "/metrics", "/__test/security-error" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Origin", "https://untrusted.example");
            using var response = await server.Client.SendAsync(request);
            await Assert.That(response.Headers.GetValues("Strict-Transport-Security").Single()).IsEqualTo("max-age=31536000");
            await Assert.That(response.Headers.GetValues("X-Frame-Options").Single()).IsEqualTo("DENY");
            await Assert.That(response.Headers.GetValues("X-Content-Type-Options").Single()).IsEqualTo("nosniff");
            await Assert.That(response.Headers.GetValues("Referrer-Policy").Single()).IsEqualTo("strict-origin-when-cross-origin");
            await Assert.That(response.Headers.GetValues("Permissions-Policy").Single()).Contains("camera=()");
            await Assert.That(response.Headers.Contains("Content-Security-Policy")).IsTrue();
            await Assert.That(response.Headers.Contains("Access-Control-Allow-Origin")).IsFalse();
            await Assert.That(response.Headers.Contains("Access-Control-Allow-Credentials")).IsFalse();
            if (path.StartsWith("/.", StringComparison.Ordinal) || path is "/debug" or "/metrics")
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            if (path == "/__test/security-error")
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
                await Assert.That(response.Headers.GetValues("Content-Security-Policy").Single()).Contains("default-src 'none'");
            }
        }
    }

    [Test]
    public async Task Scalar_uses_a_fresh_nonce_matching_its_content_security_policy()
    {
        await using var server = await AuthTestServer.StartAsync();
        string? previous = null;
        foreach (var _ in new[] { 0, 1 })
        {
            using var response = await server.Client.GetAsync("/scalar/");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var nonce = ScriptNonce().Match(await response.Content.ReadAsStringAsync()).Groups[1].Value;
            await Assert.That(nonce.Length).IsGreaterThan(0);
            await Assert.That(nonce).IsNotEqualTo(previous);
            await Assert.That(response.Headers.GetValues("Content-Security-Policy").Single()).Contains($"script-src 'self' 'nonce-{nonce}'");
            await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
            previous = nonce;
        }
    }

    [GeneratedRegex("<script[^>]*\\snonce=\"([^\"]+)\"")]
    private static partial Regex ScriptNonce();
}
