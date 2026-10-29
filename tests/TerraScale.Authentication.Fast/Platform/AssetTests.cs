using System.Net;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Platform;

public sealed class AssetTests
{
    [Test]
    public async Task Bundled_assets_are_served_with_locked_down_headers()
    {
        await using var server = await AuthTestServer.StartAsync();

        foreach (var (path, contentType) in new (string Path, string ContentType)[]
        {
            ("/admin/admin.css", "text/css"),
            ("/admin/datastar.js", "text/javascript"),
            ("/admin/logo.svg", "image/svg+xml"),
            ("/admin/logo-icon-blue.svg", "image/svg+xml"),
            ("/fonts/oxanium-latin-wght-normal.woff2", "font/woff2"),
            ("/fonts/jetbrains-mono-latin-wght-normal.woff2", "font/woff2"),
            ("/fonts/Geist-Variable.woff2", "font/woff2"),
        })
        {
            using var response = await server.Client.GetAsync(path);
            var bytes = await response.Content.ReadAsByteArrayAsync();

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(contentType);
            await Assert.That(bytes.Length).IsGreaterThan(100);
            await Assert.That(string.Join(",", response.Headers.GetValues("X-Content-Type-Options"))).IsEqualTo("nosniff");
            await Assert.That(string.Join(",", response.Headers.GetValues("Content-Security-Policy"))).Contains("sandbox");
        }
    }

    [Test]
    public async Task Unknown_assets_are_not_found()
    {
        await using var server = await AuthTestServer.StartAsync();

        foreach (var path in new[]
        {
            "/admin/missing.css",
            "/admin/..%2FTerraScale.Authentication.dll",
            "/fonts/admin.css",
        })
        {
            using var response = await server.Client.GetAsync(path);

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }
}
