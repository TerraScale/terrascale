using System.Net;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class ClientCorsTests
{
    private const string Database = "/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV/databases/todos";

    [Test]
    public async Task Anonymous_browser_probes_expose_only_public_status()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var client = new HttpClient { BaseAddress = server.ClientAddress };

        (string Path, HttpStatusCode Status)[] cases =
        [
            ("/healthz", HttpStatusCode.OK),
            ("/readyz", HttpStatusCode.OK),
            ("/backupz", HttpStatusCode.ServiceUnavailable),
        ];

        foreach (var (path, status) in cases)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Origin", "https://example.com");
            using var response = await client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(status);
            await Assert.That(response.Headers.GetValues("Access-Control-Allow-Origin").Single()).IsEqualTo("*");
            await Assert.That(response.Headers.Contains("Access-Control-Allow-Credentials")).IsFalse();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            await Assert.That(body.RootElement.EnumerateObject().Select(property => property.Name).ToList())
                .IsEquivalentTo(["status", "version", "region"]);
            using var internalRequest = new HttpRequestMessage(HttpMethod.Get, path);
            internalRequest.Headers.Add("Origin", "https://example.com");
            using var internalResponse = await server.Client.SendAsync(internalRequest);
            await Assert.That(internalResponse.Headers.Contains("Access-Control-Allow-Origin")).IsFalse();
        }
    }

    [Test]
    public async Task Probe_preflight_allows_pinned_get_without_bearer_or_cookie_access()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var client = new HttpClient { BaseAddress = server.ClientAddress };

        string[] cases =
        [
            "/healthz",
            "/readyz",
            "/backupz",
        ];

        foreach (var path in cases)
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, path);
            request.Headers.Add("Origin", "https://example.com");
            request.Headers.Add("Access-Control-Request-Method", "GET");
            request.Headers.Add("Access-Control-Request-Headers", "x-terrascale-region-pin");
            using var response = await client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await Assert.That(response.Headers.GetValues("Access-Control-Allow-Origin").Single()).IsEqualTo("*");
            await Assert.That(response.Headers.GetValues("Access-Control-Allow-Methods").Single()).IsEqualTo("GET");
            await Assert.That(response.Headers.GetValues("Access-Control-Allow-Headers").Single().ToLowerInvariant()).IsEqualTo("x-terrascale-region-pin");
            await Assert.That(response.Headers.Contains("Access-Control-Allow-Credentials")).IsFalse();
        }
    }

    [Test]
    public async Task Client_data_preflight_accepts_bearer_headers_without_credentials()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var client = new HttpClient { BaseAddress = server.ClientAddress };

        (string Route, string Method)[] cases =
        [
            ("/collections/todos/documents/by-key", "PUT"),
            ("/collections/todos/query", "POST"),
            ("/transactions", "POST"),
            ("/changes", "GET"),
            ("/collections/todos/shape", "GET"),
            ("/COLLECTIONS/todos/query", "POST"),
        ];

        foreach (var (route, method) in cases)
        {
            using var request = Preflight(Database + route, method);
            using var response = await client.SendAsync(request);

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await Assert.That(response.Headers.GetValues("Access-Control-Allow-Origin").Single()).IsEqualTo("*");
            await Assert.That(response.Headers.Contains("Access-Control-Allow-Credentials")).IsFalse();
            await Assert.That(response.Headers.GetValues("Access-Control-Allow-Methods").Single().Split(',')).Contains(method);
            foreach (var header in new[] { "authorization", "content-type", "if-match", "last-event-id" })
            {
                await Assert.That(response.Headers.GetValues("Access-Control-Allow-Headers").Single().Split(',').Select(value => value.Trim().ToLowerInvariant())).Contains(header);
            }
        }
    }

    private static HttpRequestMessage Preflight(string route, string method)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, route);
        request.Headers.Add("Origin", "https://example.com");
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,if-match,last-event-id");
        return request;
    }
}
