using System.Net;
using NUlid;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Platform;

public sealed class NotFoundPageTests
{
    [Test]
    public async Task Browsers_get_the_hosted_not_found_page_while_apis_and_protocol_paths_keep_their_answers()
    {
        await using var server = await AuthTestServer.StartAsync();

        // Unknown URLs and an unknown tenant's hosted page render the hosted page, keeping the 404 and page protections.
        foreach (var path in new[] { "/nope", "/ui/terrascale/nope", "/ui/unknown-tenant/sign-in" })
        {
            using var response = await SendAsync(server, HttpMethod.Get, path, "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
            var body = await response.Content.ReadAsStringAsync();
            await Assert.That(body).Contains("<h1>Page not found</h1>");
            await Assert.That(body).Contains("href=\"/ui/terrascale/sign-in\"");
            await Assert.That(response.Headers.GetValues("Content-Security-Policy").Single()).Contains("script-src 'self' 'nonce-");
            await Assert.That(response.Headers.GetValues("X-Frame-Options").Single()).IsEqualTo("DENY");
            await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
        }

        using (var head = await SendAsync(server, HttpMethod.Head, "/nope", "text/html"))
        {
            await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(head.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
        }

        // The versioned API keeps its JSON envelope, even for a browser.
        using (var api = await SendAsync(server, HttpMethod.Get, $"/v1/terrascale/organizations/{Ulid.NewUlid()}", "text/html"))
        {
            await Assert.That(api.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
            await Assert.That(await api.Content.ReadAsStringAsync()).DoesNotContain("Page not found");
        }

        // Clients that do not ask for HTML, and protocol or probe paths, still get an empty 404.
        foreach (var (path, accept) in new[] { ("/nope", (string?)null), ("/nope", "application/json"), ("/nope", "*/*"), ("/terrascale/oauth/nope", "text/html"), ("/.well-known/nope", "text/html"), ("/v1/nope", "text/html") })
        {
            using var response = await SendAsync(server, HttpMethod.Get, path, accept);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsNotEqualTo("text/html");
            await Assert.That(await response.Content.ReadAsStringAsync()).DoesNotContain("Page not found");
        }

        // Only reads are pages; an unknown form post stays empty.
        using (var post = await SendAsync(server, HttpMethod.Post, "/nope", "text/html"))
        {
            await Assert.That(post.Content.Headers.ContentType?.MediaType).IsNotEqualTo("text/html");
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(AuthTestServer server, HttpMethod method, string path, string? accept)
    {
        using var request = new HttpRequestMessage(method, path);
        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        return await server.Client.SendAsync(request);
    }
}
