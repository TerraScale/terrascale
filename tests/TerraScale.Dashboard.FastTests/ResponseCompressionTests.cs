using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TerraScale.Dashboard.FastTests;

public sealed class ResponseCompressionTests
{
    [Test]
    public async Task GzipIsNegotiatedForTheStylesheetOverHttps()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost/"),
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/css/terrascale.css");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        using var response = await client.SendAsync(request);

        await Assert.That(response.IsSuccessStatusCode).IsTrue();
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/css");
        await Assert.That(response.Content.Headers.ContentEncoding).Contains("gzip");
    }
}
