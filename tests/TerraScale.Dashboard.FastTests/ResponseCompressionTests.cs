using System.IO.Compression;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TerraScale.Dashboard.FastTests;

public sealed class ResponseCompressionTests
{
    [Test]
    // The SDK emits gzip during build and adds Brotli at publish. Verify
    // negotiation and identity fallback here; published assets have E2E coverage.
    [Arguments("/css/terrascale.css", "gzip", "gzip")]
    [Arguments("/css/terrascale.css", "br, gzip", "gzip")]
    [Arguments("/css/terrascale.css", "br", null)]
    [Arguments("/login", "gzip", "gzip")]
    [Arguments("/login", "br", "br")]
    public async Task HttpsCompressionPreservesTheResponse(string path, string accepted, string? encoding)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost/"),
        });
        using var original = await client.GetAsync(path);
        await Assert.That(original.IsSuccessStatusCode).IsTrue();
        await Assert.That(original.Content.Headers.ContentEncoding.Count).IsEqualTo(0);
        var expected = await original.Content.ReadAsStringAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.AcceptEncoding.ParseAdd(accepted);

        using var response = await client.SendAsync(request);

        await Assert.That(response.IsSuccessStatusCode).IsTrue();
        await Assert.That(response.Content.Headers.ContentType?.MediaType)
            .IsEqualTo(original.Content.Headers.ContentType?.MediaType);
        if (encoding is null)
        {
            await Assert.That(response.Content.Headers.ContentEncoding.Count).IsEqualTo(0);
            await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(expected);
            return;
        }
        await Assert.That(response.Content.Headers.ContentEncoding).Contains(encoding);
        await Assert.That(response.Headers.Vary).Contains("Accept-Encoding");
        using var body = await response.Content.ReadAsStreamAsync();
        using Stream decoded = encoding == "gzip"
            ? new GZipStream(body, CompressionMode.Decompress)
            : new BrotliStream(body, CompressionMode.Decompress);
        using var reader = new StreamReader(decoded);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo(expected);
    }
}
