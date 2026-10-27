using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

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
        IDashboardState state = null!;
        var clock = new RegistrationClock(DateTimeOffset.UtcNow);
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root, builder =>
        {
            state = (IDashboardState)builder.Services.Last(service => service.ServiceType == typeof(IDashboardState)).ImplementationInstance!;
            builder.Services.AddSingleton<TimeProvider>(clock);
        });
        using var browser = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
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
        await Assert.That(TerraScaleIds.IsValid(clientId)).IsTrue();

        var initial = (await state.GetAsync(McpOAuth.ClientCollection, clientId, default))!.Document.Deserialize<McpRegistration>()!;
        await Assert.That(initial.ClientId).IsEqualTo(clientId);
        await Assert.That(initial.FirstSignInCompletedAt).IsNull();

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

        using var signedIn = await HttpJourney.LoginAsync(browser, persona.Email, persona.Password);
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
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

        using (var denied = await HttpJourney.PostConfirmedFormAsync(browser, "/oauth/authorize", new(form) { ["decision"] = "deny" }))
            await Assert.That(denied.Headers.Location!.OriginalString).StartsWith(Callback + "?error=access_denied&state=tunit-state");

        await Assert.That((await state.GetAsync(McpOAuth.ClientCollection, clientId, default))!.Document.GetRawText()).IsEqualTo(JsonSerializer.Serialize(initial));
        var allowed = await HttpJourney.PostConfirmedFormAsync(browser, "/oauth/authorize", new(form) { ["decision"] = "allow" });
        await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using var granted = await HttpJourney.HostedSignInAsync(browser, allowed, persona.Email, persona.Password);
        await Assert.That(granted.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var redirect = granted.Headers.Location!;
        await Assert.That(redirect.GetLeftPart(UriPartial.Path)).IsEqualTo(Callback);
        var answer = QueryHelpers.ParseQuery(redirect.Query);
        await Assert.That(answer["state"].ToString()).IsEqualTo("tunit-state");
        await Assert.That(answer["iss"].ToString()).IsEqualTo(origin);
        var code = answer["code"].ToString();
        var callbackActivity = (await state.GetAsync(McpOAuth.ClientCollection, clientId, default))!.Document.Deserialize<McpRegistration>()!;
        await Assert.That(callbackActivity.FirstSignInCompletedAt).IsEqualTo(clock.GetUtcNow());
        clock.Now = clock.Now.AddSeconds(1);

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

        var tokenSet = (await state.GetAsync(AccessTokens.Collection, AccountService.AccountId(persona.Subject), default))!.Document.Deserialize<AccessTokens.TokenSet>()!;
        await Assert.That(tokenSet.Tokens.Single().McpClientId).IsEqualTo(clientId);
        var registration = (await state.GetAsync(McpOAuth.ClientCollection, clientId, default))!;
        var completed = registration.Document.Deserialize<McpRegistration>()!;
        await Assert.That(completed.FirstSignInCompletedAt is not null).IsTrue();
        var idle = completed with { CreatedAt = clock.Now.AddDays(-32), FirstSignInCompletedAt = clock.Now.AddDays(-32), LastActivityAt = clock.Now.AddDays(-30).AddTicks(1) };
        await state.PutAsync(McpOAuth.ClientCollection, clientId, JsonSerializer.SerializeToElement(idle), TerraBasePrecondition.Matches(registration.Version), default);
        using (var invalidRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { }) })
        {
            invalidRequest.Headers.Authorization = new("Bearer", secret[..^1] + (secret[^1] == 'a' ? "b" : "a"));
            using var invalid = await replica.SendAsync(invalidRequest);
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
        await Assert.That((await state.GetAsync(McpOAuth.ClientCollection, clientId, default))!.Document.GetRawText()).IsEqualTo(JsonSerializer.Serialize(idle));
        var tools = await HttpJourney.McpAsync(replica, secret, "tools/list", new { });
        await Assert.That(tools.GetProperty("result").GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "get_context")).IsTrue();
        var context = await HttpJourney.McpAsync(browser, secret, "tools/call", new { name = "get_context", arguments = new { } });
        await Assert.That(context.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean()).IsFalse();

        registration = (await state.GetAsync(McpOAuth.ClientCollection, clientId, default))!;
        completed = registration.Document.Deserialize<McpRegistration>()!;
        await Assert.That(completed.LastActivityAt > idle.LastActivityAt).IsTrue();
        await Assert.That(completed.FirstSignInCompletedAt).IsEqualTo(idle.FirstSignInCompletedAt);
        var expired = completed with { CreatedAt = clock.Now.AddDays(-32), FirstSignInCompletedAt = clock.Now.AddDays(-32), LastActivityAt = clock.Now.AddDays(-30) };
        await state.PutAsync(McpOAuth.ClientCollection, clientId, JsonSerializer.SerializeToElement(expired), TerraBasePrecondition.Matches(registration.Version), default);
        using (var expiredRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { }) })
        {
            expiredRequest.Headers.Authorization = new("Bearer", secret);
            using var rejected = await replica.SendAsync(expiredRequest);
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
        await Assert.That((await state.GetAsync(McpOAuth.ClientCollection, clientId, default))!.Document.Deserialize<McpRegistration>()!.ClosedAt is not null).IsTrue();
        using (var expiredConsent = await browser.GetAsync(consentPath))
            await Assert.That(expiredConsent.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        using var login = await HttpJourney.LoginAsync(browser, persona.Email, persona.Password);
        using var profile = await browser.GetAsync("/profile?tab=access-tokens");
        await Assert.That(Regex.IsMatch(await profile.Content.ReadAsStringAsync(), "MCP: TUnit MCP client")).IsTrue();
    }

    [Test]
    public async Task RegistrationBoundsActualUtf8BytesBeforeWritingAndResolvesCanonicalConsent()
    {
        IDashboardState state = null!;
        var clock = new RegistrationClock(DateTimeOffset.UtcNow);
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root, builder =>
        {
            state = (IDashboardState)builder.Services.Last(service => service.ServiceType == typeof(IDashboardState)).ImplementationInstance!;
            builder.Services.AddSingleton<TimeProvider>(clock);
        });
        using var client = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        const string metadata = "{\"client_name\":\"é ingress\",\"redirect_uris\":[\"http://127.0.0.1:33418/callback\"]}";
        var initialCount = (await state.ListAsync(McpOAuth.ClientCollection, default)).Count;
        var accepted = 0;
        foreach (var chunked in new[] { false, true })
        {
            foreach (var size in new[] { 16384, 16385 })
            {
                var bytes = Encoding.UTF8.GetBytes(metadata + new string(' ', size - Encoding.UTF8.GetByteCount(metadata)));
                using HttpContent content = chunked ? new ChunkedJsonContent(bytes) : new ByteArrayContent(bytes);
                content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
                using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/register") { Content = content };
                if (chunked) request.Headers.TransferEncodingChunked = true;
                using var response = await client.SendAsync(request);
                await Assert.That(response.StatusCode).IsEqualTo(size == 16384 ? HttpStatusCode.Created : HttpStatusCode.RequestEntityTooLarge);
                await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (size == 16384)
                {
                    accepted++;
                    var id = result.GetProperty("client_id").GetString()!;
                    await Assert.That(TerraScaleIds.IsValid(id)).IsTrue();
                    await Assert.That((await state.GetAsync(McpOAuth.ClientCollection, id, default))!.Id).IsEqualTo(id);
                    // Look up the returned public identity through the real authorize endpoint on the other host.
                    using var consent = await replica.GetAsync(ConsentPath(id));
                    await Assert.That(consent.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    await Assert.That(await consent.Content.ReadAsStringAsync()).Contains("ingress");
                    using var lowercase = await replica.GetAsync(ConsentPath(id.ToLowerInvariant()));
                    await Assert.That(lowercase.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
                    var stored = (await state.GetAsync(McpOAuth.ClientCollection, id, default))!;
                    var stale = stored.Document.Deserialize<McpRegistration>()! with { CreatedAt = clock.Now.AddHours(-24).AddTicks(1), LastActivityAt = clock.Now.AddHours(-24).AddTicks(1) };
                    // Consent cannot renew a client just before the exact unfinished cutoff.
                    stale = stale with { LastActivityAt = stale.CreatedAt };
                    await state.PutAsync(McpOAuth.ClientCollection, id, JsonSerializer.SerializeToElement(stale), TerraBasePrecondition.Matches(stored.Version), default);
                    using var stillActive = await replica.GetAsync(ConsentPath(id));
                    await Assert.That(stillActive.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    clock.Now = clock.Now.AddTicks(1);
                    using var expired = await replica.GetAsync(ConsentPath(id));
                    await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
                    await Assert.That((await state.GetAsync(McpOAuth.ClientCollection, id, default))!.Document.Deserialize<McpRegistration>()!.ClosedAt is not null).IsTrue();
                }
                else
                    await Assert.That(result.GetProperty("error").GetString()).IsEqualTo("invalid_client_metadata");
                await Assert.That((await state.ListAsync(McpOAuth.ClientCollection, default)).Count).IsEqualTo(initialCount + accepted);
            }
        }

        foreach (var invalid in new[] { ("{", "application/json"), ("[]", "application/json"), ("{}", "application/json"), (metadata, "text/plain"), (metadata, "application/json; charset=utf-16") })
        {
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(invalid.Item1));
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(invalid.Item2);
            using var response = await client.PostAsync("/oauth/register", content);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
            await Assert.That(error is "invalid_client_metadata" or "invalid_redirect_uri").IsTrue();
            await Assert.That((await state.ListAsync(McpOAuth.ClientCollection, default)).Count).IsEqualTo(initialCount + accepted);
        }

        // Even a persisted old-shaped key cannot bypass canonical request validation.
        var oldId = "mcp_" + new string('a', 32);
        await state.PutAsync(McpOAuth.ClientCollection, oldId, JsonSerializer.SerializeToElement(new
        { Name = "Old-shaped client", RedirectUris = new[] { Callback }, CreatedAt = DateTimeOffset.UtcNow }), TerraBasePrecondition.Absent, default);
        using var old = await replica.GetAsync(ConsentPath(oldId));
        await Assert.That(old.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ExpiredRegistrationBetweenConsentAndCallbackRevokesFreshGrantLocally()
    {
        IDashboardState state = null!;
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root, builder =>
            state = (IDashboardState)builder.Services.Last(service => service.ServiceType == typeof(IDashboardState)).ImplementationInstance!);
        using var browser = scenario.CreateClient();
        using var registered = await browser.PostAsJsonAsync("/oauth/register", new { client_name = "Paused callback", redirect_uris = new[] { Callback } });
        var id = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
        using (var signedIn = await HttpJourney.LoginAsync(browser, scenario.Personas.Seeded.Email, scenario.Personas.Seeded.Password))
            await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var csrf = await HttpJourney.CsrfAsync(browser, ConsentPath(id));
        var form = QueryHelpers.ParseQuery(new Uri(browser.BaseAddress!, ConsentPath(id)).Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
        form["_csrf"] = csrf;
        form["decision"] = "allow";
        using var allowed = await HttpJourney.PostConfirmedFormAsync(browser, "/oauth/authorize", form);
        await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var stored = (await state.GetAsync(McpOAuth.ClientCollection, id, default))!;
        var created = DateTimeOffset.UtcNow.AddHours(-25);
        var stale = stored.Document.Deserialize<McpRegistration>()! with { CreatedAt = created, LastActivityAt = created };
        await state.PutAsync(McpOAuth.ClientCollection, id, JsonSerializer.SerializeToElement(stale), TerraBasePrecondition.Matches(stored.Version), default);
        var codes = (await state.ListAsync(McpOAuth.CodeCollection, default)).Count;
        var credentials = (await state.ListAsync("auth_credentials", default)).Count;
        using var rejected = await HttpJourney.HostedSignInAsync(browser, allowed, scenario.Personas.Seeded.Email, scenario.Personas.Seeded.Password);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(rejected.Headers.Location).IsNull();
        await Assert.That(await rejected.Content.ReadAsStringAsync()).Contains("Add the server again");
        await Assert.That((await state.ListAsync(McpOAuth.CodeCollection, default)).Count).IsEqualTo(codes);
        await Assert.That((await state.ListAsync("auth_credentials", default)).Count).IsEqualTo(credentials);
        var closed = (await state.GetAsync(McpOAuth.ClientCollection, id, default))!;
        await Assert.That(closed.Document.Deserialize<McpRegistration>()!.ClosedAt is not null).IsTrue();
        await state.DeleteAsync(McpOAuth.ClientCollection, id, closed.Version, default);
        using var missing = await browser.GetAsync(ConsentPath(id));
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await state.GetAsync(McpOAuth.ClientCollection, id, default)).IsNull();
    }

    private static string ConsentPath(string id) => QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
    {
        ["response_type"] = "code",
        ["client_id"] = id,
        ["redirect_uri"] = Callback,
        ["code_challenge"] = new string('a', 43),
        ["code_challenge_method"] = "S256",
        ["scope"] = "mcp"
    });

    private sealed class RegistrationClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Unknown length forces the HTTP transport to send chunked bytes, rather than just spoofing a header.
    private sealed class ChunkedJsonContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            for (var offset = 0; offset < bytes.Length; offset += 1024)
                await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(1024, bytes.Length - offset)), cancellationToken);
        }
    }
}
