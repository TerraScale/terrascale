using System.Net;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class ClientCorsTests
{
    private const string Database = "/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV/databases/todos";

    [Test]
    [Arguments("/healthz", HttpStatusCode.OK)]
    [Arguments("/readyz", HttpStatusCode.OK)]
    [Arguments("/backupz", HttpStatusCode.ServiceUnavailable)]
    public async Task Anonymous_browser_probes_expose_only_public_status(string path, HttpStatusCode status)
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var client = new HttpClient { BaseAddress = server.ClientAddress };
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

    [Test]
    [Arguments("/healthz")]
    [Arguments("/readyz")]
    [Arguments("/backupz")]
    public async Task Probe_preflight_allows_pinned_get_without_bearer_or_cookie_access(string path)
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var client = new HttpClient { BaseAddress = server.ClientAddress };
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

    [Test]
    [Arguments("/collections/todos/documents/by-key", "PUT")]
    [Arguments("/collections/todos/query", "POST")]
    [Arguments("/transactions", "POST")]
    [Arguments("/changes", "GET")]
    [Arguments("/collections/todos/shape", "GET")]
    [Arguments("/COLLECTIONS/todos/query", "POST")]
    public async Task Client_data_preflight_accepts_bearer_headers_without_credentials(string route, string method)
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var client = new HttpClient { BaseAddress = server.ClientAddress };
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

    [Test]
    public async Task Browser_receives_the_denied_data_response_and_exposed_contract_headers()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var client = new HttpClient { BaseAddress = server.ClientAddress };
        using var request = new HttpRequestMessage(HttpMethod.Get, Database + "/collections/todos/documents");
        request.Headers.Add("Origin", "https://example.com");
        using var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Headers.GetValues("Access-Control-Allow-Origin").Single()).IsEqualTo("*");
        await Assert.That(response.Headers.Contains("Access-Control-Allow-Credentials")).IsFalse();
        var exposed = response.Headers.GetValues("Access-Control-Expose-Headers").Single().Split(',').Select(value => value.Trim());
        await Assert.That(exposed).Contains("ETag");
        await Assert.That(exposed).Contains("Location");
        await Assert.That(exposed).Contains("TB-Shape-Next-Offset");
    }

    [Test]
    public async Task Internal_and_management_routes_do_not_answer_browser_preflight()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var client = new HttpClient { BaseAddress = server.ClientAddress };
        using var management = Preflight("/v1/projects", "POST");
        using var managementResponse = await client.SendAsync(management);
        await Assert.That(managementResponse.Headers.Contains("Access-Control-Allow-Origin")).IsFalse();
        using var sql = Preflight(Database + "/sql", "POST");
        using var sqlResponse = await client.SendAsync(sql);
        await Assert.That(sqlResponse.Headers.Contains("Access-Control-Allow-Origin")).IsFalse();
        using var internalRequest = Preflight(Database + "/changes", "GET");
        using var internalResponse = await server.Client.SendAsync(internalRequest);
        await Assert.That(internalResponse.Headers.Contains("Access-Control-Allow-Origin")).IsFalse();
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
