using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.E2E.Features.Auth;

// An MCP client with no configured header discovers the dashboard's OAuth metadata from the 401,
// registers itself, sends the user through consent and ts-auth's hosted sign-in, and calls tools
// with the access token it redeemed using PKCE.
[ClassDataSource<DashboardRootFixture>]
public sealed class McpOAuthJourneyTests(DashboardRootFixture fixture)
{
    private const string Callback = "http://127.0.0.1:33418/callback";

    [Test]
    public async Task ClientSignsInThroughDiscoveryRegistrationConsentAndPkce()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        var persona = scenario.Personas.Seeded;
        var origin = browser.BaseAddress!.GetLeftPart(UriPartial.Authority);

        using (var anonymous = await browser.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json")))
        {
            await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(anonymous.Headers.WwwAuthenticate.ToString()).Contains($"resource_metadata=\"{origin}/.well-known/oauth-protected-resource/mcp\"");
        }
        var resource = await browser.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp");
        await Assert.That(resource.GetProperty("resource").GetString()).IsEqualTo(origin + "/mcp");
        await Assert.That(resource.GetProperty("authorization_servers")[0].GetString()).IsEqualTo(origin);
        var server = await browser.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
        await Assert.That(server.GetProperty("issuer").GetString()).IsEqualTo(origin);
        await Assert.That(server.GetProperty("code_challenge_methods_supported")[0].GetString()).IsEqualTo("S256");

        using (var unsafeRedirect = await browser.PostAsJsonAsync("/oauth/register", new { redirect_uris = new[] { "http://attacker.example/callback" } }))
            await Assert.That(unsafeRedirect.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var registered = await browser.PostAsJsonAsync("/oauth/register", new { client_name = "TUnit MCP client", redirect_uris = new[] { Callback }, token_endpoint_auth_method = "none" });
        await Assert.That(registered.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var clientId = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;

        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = Callback,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = "tunit-state",
            ["resource"] = origin + "/mcp",
            ["scope"] = "mcp"
        };
        using (var foreign = await browser.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>(authorize) { ["redirect_uri"] = "http://127.0.0.1:33418/other" })))
            await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var consentPath = QueryHelpers.AddQueryString("/oauth/authorize", authorize);
        using (var consent = await browser.GetAsync(consentPath))
        {
            await Assert.That(consent.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await consent.Content.ReadAsStringAsync()).Contains("TUnit MCP client");
            await Assert.That(consent.Headers.GetValues("Content-Security-Policy").Single()).Contains("form-action 'self' https://auth.terrascale.tech https://checkout.stripe.com https://billing.stripe.com http://127.0.0.1:33418;");
        }
        var csrf = await HttpJourney.CsrfAsync(browser, consentPath);
        var form = authorize.Where(pair => pair.Key != "scope").ToDictionary(pair => pair.Key, pair => pair.Value!);
        form["_csrf"] = csrf;

        using (var denied = await HttpJourney.PostFormAsync(browser, "/oauth/authorize", new(form) { ["decision"] = "deny" }))
            await Assert.That(denied.Headers.Location!.OriginalString).StartsWith(Callback + "?error=access_denied&state=tunit-state");

        var allowed = await HttpJourney.PostFormAsync(browser, "/oauth/authorize", new(form) { ["decision"] = "allow" });
        await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using var granted = await HttpJourney.HostedSignInAsync(browser, allowed, persona.Email, persona.Password);
        await Assert.That(granted.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var redirect = granted.Headers.Location!;
        await Assert.That(redirect.GetLeftPart(UriPartial.Path)).IsEqualTo(Callback);
        var answer = QueryHelpers.ParseQuery(redirect.Query);
        await Assert.That(answer["state"].ToString()).IsEqualTo("tunit-state");
        await Assert.That(answer["iss"].ToString()).IsEqualTo(origin);
        var code = answer["code"].ToString();

        var exchange = new Dictionary<string, string>
        { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Callback, ["client_id"] = clientId, ["code_verifier"] = verifier, ["resource"] = origin + "/mcp" };
        using var token = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(exchange));
        await Assert.That(token.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var issued = await token.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(issued.GetProperty("token_type").GetString()).IsEqualTo("Bearer");
        var secret = issued.GetProperty("access_token").GetString()!;
        using (var replay = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(exchange)))
        {
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()).IsEqualTo("invalid_grant");
        }

        var tools = await HttpJourney.McpAsync(browser, secret, "tools/list", new { });
        await Assert.That(tools.GetProperty("result").GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "get_context")).IsTrue();
        var context = await HttpJourney.McpAsync(browser, secret, "tools/call", new { name = "get_context", arguments = new { } });
        await Assert.That(context.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean()).IsFalse();

        using var login = await HttpJourney.LoginAsync(browser, persona.Email, persona.Password);
        using var profile = await browser.GetAsync("/profile?tab=access-tokens");
        await Assert.That(Regex.IsMatch(await profile.Content.ReadAsStringAsync(), "MCP: TUnit MCP client")).IsTrue();
    }
}
