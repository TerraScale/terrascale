using System.Net;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class ClientCorsEdgeTests
{
    private const string Database = "/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV/databases/todos";

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
