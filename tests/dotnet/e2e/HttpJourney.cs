using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E;

public static class HttpJourney
{
    public static async Task<string> CsrfAsync(HttpClient client, string path = "/login")
    {
        using var response = await client.GetAsync(path);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var token = Regex.Match(body, "name=\"_csrf\" value=\"([^\"]+)\"").Groups[1].Value;
        await Assert.That(token.Length).IsGreaterThan(0);
        return WebUtility.HtmlDecode(token);
    }

    public static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email = "user@example.com", string password = "test-password", string returnTo = "/")
    {
        var token = await CsrfAsync(client);
        return await PostFormAsync(client, "/login", new() { ["_csrf"] = token, ["email"] = email, ["password"] = password, ["returnTo"] = returnTo, ["redirectTo"] = returnTo });
    }

    public static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, Dictionary<string, string> body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(body) };
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return client.SendAsync(request);
    }

    public static async Task<JsonElement> McpAsync(HttpClient client, string secret, string method, object arguments)
    {
        // The source dashboard uses the current stateless protocol. Keep the
        // explicit legacy initialize journey as a compatibility assertion.
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments))!;
        var protocol = method == "initialize" ? parameters["protocolVersion"].GetString()! : "2026-07-28";
        if (protocol == "2026-07-28")
            parameters["_meta"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["io.modelcontextprotocol/protocolVersion"] = protocol,
                ["io.modelcontextprotocol/clientCapabilities"] = new { },
                ["io.modelcontextprotocol/clientInfo"] = new { name = "TUnit dashboard E2E", version = "1" }
            });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Add("MCP-Protocol-Version", protocol);
        request.Headers.Add("Mcp-Method", method);
        if (method == "tools/call") request.Headers.Add("Mcp-Name", parameters["name"].GetString()!);
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = string.Join('\n', body.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].TrimStart()));
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    public static async Task<string> CreatePatAsync(HttpClient client, string name = "TUnit E2E", string password = "test-password")
    {
        var token = await CsrfAsync(client, "/profile?tab=access-tokens");
        using var response = await PostFormAsync(client, "/api/v1/dashboard/profile/access-tokens", new()
        { ["_csrf"] = token, ["name"] = name, ["password"] = password, ["expiresInDays"] = "7" });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var secret = Regex.Match(body, "id=\"access-token-secret\" value=\"([^\"]+)\"").Groups[1].Value;
        await Assert.That(secret.StartsWith("tspat_", StringComparison.Ordinal)).IsTrue();
        return secret;
    }
}
