using System.Net;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Fast.Platform;

public sealed class AssetTests
{
    [Test]
    [Arguments("/admin/admin.css", "text/css")]
    [Arguments("/admin/datastar.js", "text/javascript")]
    [Arguments("/admin/logo.svg", "image/svg+xml")]
    [Arguments("/fonts/oxanium-latin-wght-normal.woff2", "font/woff2")]
    [Arguments("/fonts/jetbrains-mono-latin-wght-normal.woff2", "font/woff2")]
    public async Task Bundled_assets_are_served_with_locked_down_headers(string path, string contentType)
    {
        await using var server = await AuthTestServer.StartAsync();

        using var response = await server.Client.GetAsync(path);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(contentType);
        await Assert.That(bytes.Length).IsGreaterThan(100);
        await Assert.That(string.Join(",", response.Headers.GetValues("X-Content-Type-Options"))).IsEqualTo("nosniff");
        await Assert.That(string.Join(",", response.Headers.GetValues("Content-Security-Policy"))).Contains("sandbox");
    }

    [Test]
    [Arguments("/admin/missing.css")]
    [Arguments("/admin/..%2FTerraScale.Auth.dll")]
    [Arguments("/fonts/admin.css")]
    public async Task Unknown_assets_are_not_found(string path)
    {
        await using var server = await AuthTestServer.StartAsync();

        using var response = await server.Client.GetAsync(path);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
