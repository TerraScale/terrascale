using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E;

public static class HttpJourney
{
    public static async Task<string> CsrfAsync(HttpClient client, string path = "/profile")
    {
        using var response = await client.GetAsync(path);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var token = Regex.Match(body, "name=\"_csrf\" value=\"([^\"]+)\"").Groups[1].Value;
        await Assert.That(token.Length).IsGreaterThan(0);
        return WebUtility.HtmlDecode(token);
    }

    // Dashboard origin -> the test-only ts-auth producer serving https://auth.terrascale.tech for that host.
    public static readonly ConcurrentDictionary<string, Uri> AuthProviders = new();
    // Stands in for ts-auth's own session cookie: who this client signed in as on the hosted page.
    private static readonly ConditionalWeakTable<HttpClient, string> HostedAccounts = new();

    // Signs in as a browser does: /login redirects to ts-auth's hosted sign-in, which answers
    // /login/callback. Returns the callback's answer (303 to returnTo when it succeeds).
    public static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email = "user@example.com", string password = "test-password", string returnTo = "/", bool signUp = false)
    {
        var start = await client.GetAsync("/login?returnTo=" + Uri.EscapeDataString(returnTo));
        if (start.StatusCode != HttpStatusCode.Redirect) return start;
        HostedAccounts.AddOrUpdate(client, email);
        return await HostedSignInAsync(client, start, email, password, signUp);
    }

    // Follows a redirect to ts-auth's authorize endpoint through the test stand-in for its hosted
    // pages, then delivers the answer to the dashboard callback.
    public static async Task<HttpResponseMessage> HostedSignInAsync(HttpClient client, HttpResponseMessage authorize, string email = "user@example.com", string password = "test-password", bool signUp = false)
    {
        Uri location;
        using (authorize) location = authorize.Headers.Location ?? throw new InvalidOperationException("Expected a redirect to ts-auth.");
        var account = "email=" + Uri.EscapeDataString(email) + "&password=" + Uri.EscapeDataString(password) + (signUp ? "&signup=1" : "");
        HttpResponseMessage answered;
        if (!location.IsAbsoluteUri) answered = await client.GetAsync(location.OriginalString + "&" + account);
        else
        {
            var provider = AuthProviders[client.BaseAddress!.GetLeftPart(UriPartial.Authority)];
            using var hosted = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(provider, location.PathAndQuery + "&" + account));
            request.Headers.Host = location.Authority;
            answered = await hosted.SendAsync(request);
        }
        if (answered.StatusCode != HttpStatusCode.Redirect) return answered;
        Uri callback;
        using (answered) callback = answered.Headers.Location!;
        return await client.GetAsync(callback.IsAbsoluteUri ? callback.PathAndQuery : callback.OriginalString);
    }

    public static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, Dictionary<string, string> body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(body) };
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return client.SendAsync(request);
    }

    public static async Task<JsonElement> McpAsync(HttpClient client, string secret, string method, object arguments, string path = "/mcp", string? host = null)
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
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (host is not null) request.Headers.Host = host;
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

    // A personal access token gets its own grant: the create form redirects through ts-auth's hosted
    // sign-in as the account this client signed in with, and the callback shows the secret once.
    public static async Task<string> CreatePatAsync(HttpClient client, string name = "TUnit E2E", string password = "test-password")
    {
        var token = await CsrfAsync(client, "/profile?tab=access-tokens");
        var authorize = await PostFormAsync(client, "/api/v1/dashboard/profile/access-tokens", new()
        { ["_csrf"] = token, ["name"] = name, ["expiresInDays"] = "7" });
        await Assert.That(authorize.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using var response = await HostedSignInAsync(client, authorize, HostedAccounts.TryGetValue(client, out var email) ? email : "user@example.com", password);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var secret = Regex.Match(body, "id=\"access-token-secret\" value=\"([^\"]+)\"").Groups[1].Value;
        await Assert.That(secret.StartsWith("tspat_", StringComparison.Ordinal)).IsTrue();
        return secret;
    }
}
