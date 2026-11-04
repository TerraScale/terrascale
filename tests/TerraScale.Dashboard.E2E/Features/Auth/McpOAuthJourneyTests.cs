using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
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
            {
                await Assert.That(unsafeRedirect.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }

            using var registered = await browser.PostAsJsonAsync("/oauth/register", new { client_name = "TUnit MCP client", redirect_uris = new[] { Callback }, token_endpoint_auth_method = "none" });
            await Assert.That(registered.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var clientId = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
            await Assert.That(TerraScaleIds.IsValid(clientId)).IsTrue();

            var initial = (await state.GetAsync(McpOAuth.ClientTable, clientId, default))!.Body.Deserialize<McpRegistration>()!;
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
            {
                await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }
            // The consent binds exactly one TerraScale MCP resource and the one scope the token carries.
            foreach (var (key, value, error) in new (string, string?, string)[]
                     {
                         ("resource", null, "invalid_target"), ("resource", "https://attacker.example/mcp", "invalid_target"),
                         ("resource", origin + "/mcp?tenant=other", "invalid_target"), ("resource", "https://auth.terrascale.tech/", "invalid_target"),
                         ("scope", "admin", "invalid_scope"), ("scope", "mcp admin", "invalid_scope")
                     })
            {
                var query = authorize.Where(pair => pair.Key != key).Append(KeyValuePair.Create(key, value)).Where(pair => pair.Value is not null);
                using var rejected = await browser.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", query));
                await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
                await Assert.That(rejected.Headers.Location!.OriginalString).StartsWith($"{Callback}?error={error}&");
            }

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
            {
                await Assert.That(denied.Headers.Location!.OriginalString).StartsWith(Callback + "?error=access_denied&state=tunit-state");
            }

            await Assert.That((await state.GetAsync(McpOAuth.ClientTable, clientId, default))!.Body.GetRawText()).IsEqualTo(JsonSerializer.Serialize(initial));
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
            var callbackActivity = (await state.GetAsync(McpOAuth.ClientTable, clientId, default))!.Body.Deserialize<McpRegistration>()!;
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

            var tokenSet = (await state.GetAsync(AccessTokens.Table, AccountService.AccountId(persona.Subject), default))!.Body.Deserialize<AccessTokens.TokenSet>()!;
            await Assert.That(tokenSet.Tokens.Single().McpClientId).IsEqualTo(clientId);
            var registration = (await state.GetAsync(McpOAuth.ClientTable, clientId, default))!;
            var completed = registration.Body.Deserialize<McpRegistration>()!;
            await Assert.That(completed.FirstSignInCompletedAt is not null).IsTrue();
            var idle = completed with { CreatedAt = clock.Now.AddDays(-32), FirstSignInCompletedAt = clock.Now.AddDays(-32), LastActivityAt = clock.Now.AddDays(-30).AddTicks(1) };
            await state.PutAsync(McpOAuth.ClientTable, clientId, JsonSerializer.SerializeToElement(idle), StatePrecondition.Matches(registration.Version), default);
            using (var invalidRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { }) })
            {
                invalidRequest.Headers.Authorization = new("Bearer", secret[..^1] + (secret[^1] == 'a' ? "b" : "a"));
                using var invalid = await replica.SendAsync(invalidRequest);
                await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }
            await Assert.That((await state.GetAsync(McpOAuth.ClientTable, clientId, default))!.Body.GetRawText()).IsEqualTo(JsonSerializer.Serialize(idle));
            var tools = await HttpJourney.McpAsync(replica, secret, "tools/list", new { });
            await Assert.That(tools.GetProperty("result").GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "get_context")).IsTrue();
            var context = await HttpJourney.McpAsync(browser, secret, "tools/call", new { name = "get_context", arguments = new { } });
            await Assert.That(context.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean()).IsFalse();

            registration = (await state.GetAsync(McpOAuth.ClientTable, clientId, default))!;
            completed = registration.Body.Deserialize<McpRegistration>()!;
            await Assert.That(completed.LastActivityAt > idle.LastActivityAt).IsTrue();
            await Assert.That(completed.FirstSignInCompletedAt).IsEqualTo(idle.FirstSignInCompletedAt);
            var expired = completed with { CreatedAt = clock.Now.AddDays(-32), FirstSignInCompletedAt = clock.Now.AddDays(-32), LastActivityAt = clock.Now.AddDays(-30) };
            await state.PutAsync(McpOAuth.ClientTable, clientId, JsonSerializer.SerializeToElement(expired), StatePrecondition.Matches(registration.Version), default);
            using (var expiredRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { }) })
            {
                expiredRequest.Headers.Authorization = new("Bearer", secret);
                using var rejected = await replica.SendAsync(expiredRequest);
                await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }
            await Assert.That((await state.GetAsync(McpOAuth.ClientTable, clientId, default))!.Body.Deserialize<McpRegistration>()!.ClosedAt is not null).IsTrue();
            using (var expiredConsent = await browser.GetAsync(consentPath))
            {
                await Assert.That(expiredConsent.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }

            using var login = await HttpJourney.LoginAsync(browser, persona.Email, persona.Password);
            using var profile = await browser.GetAsync("/profile?tab=access-tokens");
            await Assert.That(Regex.IsMatch(await profile.Content.ReadAsStringAsync(), "MCP: TUnit MCP client")).IsTrue();
        }

        [Test]
        [Arguments(null)]
        [Arguments("https://mcp.terrascale.tech/")]
        [Arguments("https://attacker.example/mcp")]
        [Arguments("unbound:https://attacker.example/mcp")]
        public async Task RedemptionNamingAnotherResourceBurnsTheCodeWithoutAToken(string? redeemed)
        {
            IDashboardState state = null!;
            await using var scenario = await ReferenceScenario.StartAsync(fixture.Root, builder =>
                state = (IDashboardState)builder.Services.Last(service => service.ServiceType == typeof(IDashboardState)).ImplementationInstance!);
            using var browser = scenario.CreateClient();
            var persona = scenario.Personas.Seeded;
            var origin = browser.BaseAddress!.GetLeftPart(UriPartial.Authority);
            using var registered = await browser.PostAsJsonAsync("/oauth/register", new { client_name = "Resource binding", redirect_uris = new[] { Callback } });
            var id = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
            var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var path = QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
            {
                ["response_type"] = "code",
                ["client_id"] = id,
                ["redirect_uri"] = Callback,
                ["code_challenge_method"] = "S256",
                ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
                ["resource"] = origin + "/mcp"
            });
            using (var signedIn = await HttpJourney.LoginAsync(browser, persona.Email, persona.Password))
            {
                await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var form = QueryHelpers.ParseQuery(new Uri(browser.BaseAddress!, path).Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
            form["_csrf"] = await HttpJourney.CsrfAsync(browser, path);
            form["decision"] = "allow";
            using var allowed = await HttpJourney.PostConfirmedFormAsync(browser, "/oauth/authorize", form);
            using var granted = await HttpJourney.HostedSignInAsync(browser, allowed, persona.Email, persona.Password);
            var code = QueryHelpers.ParseQuery(granted.Headers.Location!.Query)["code"].ToString();
            var credential = (await state.ListAsync(McpOAuth.CodeTable, default)).Single().Body.GetProperty("CredentialId").GetString()!;
            await Assert.That(await state.GetAsync("auth_credentials", credential, default)).IsNotNull();
            if (redeemed?.StartsWith("unbound:", StringComparison.Ordinal) == true)
            {
                // A code stored without a resource must not match a resource that canonicalizes to nothing.
                var stored = (await state.ListAsync(McpOAuth.CodeTable, default)).Single();
                var unbound = System.Text.Json.Nodes.JsonNode.Parse(stored.Body.GetRawText())!.AsObject();
                unbound.Remove("Resource");
                await state.PutAsync(McpOAuth.CodeTable, stored.Id, JsonSerializer.SerializeToElement(unbound), StatePrecondition.Matches(stored.Version), default);
                redeemed = redeemed["unbound:".Length..];
            }
            var exchange = new Dictionary<string, string>
            { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Callback, ["client_id"] = id, ["code_verifier"] = verifier };
            if (redeemed is not null)
            {
                exchange["resource"] = redeemed;
            }

            using (var mismatched = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(exchange)))
            {
                await Assert.That(mismatched.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
                var body = await mismatched.Content.ReadFromJsonAsync<JsonElement>();
                await Assert.That(body.GetProperty("error").GetString()).IsEqualTo("invalid_target");
                await Assert.That(body.TryGetProperty("access_token", out _)).IsFalse();
            }
            exchange["resource"] = origin + "/mcp";
            using (var retried = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(exchange)))
            {
                await Assert.That((await retried.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()).IsEqualTo("invalid_grant");
            }

            var tokens = await state.GetAsync(AccessTokens.Table, AccountService.AccountId(persona.Subject), default);
            await Assert.That(tokens is null || tokens.Body.Deserialize<AccessTokens.TokenSet>()!.Tokens.All(token => token.McpClientId != id)).IsTrue();
            await Assert.That(await state.ListAsync(McpOAuth.CodeTable, default)).IsEmpty();
            await Assert.That(await state.GetAsync("auth_credentials", credential, default)).IsNull();
        }

        private static string ConsentPath(string id)
        {
            return QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
            {
                ["response_type"] = "code",
                ["client_id"] = id,
                ["redirect_uri"] = Callback,
                ["code_challenge"] = new string('a', 43),
                ["code_challenge_method"] = "S256",
                ["resource"] = "https://mcp.terrascale.tech/",
                ["scope"] = "mcp"
            });
        }

        private sealed class RegistrationClock(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;
            public override DateTimeOffset GetUtcNow()
            {
                return Now;
            }
        }

        // Unknown length forces the HTTP transport to send chunked bytes, rather than just spoofing a header.
        private sealed class ChunkedJsonContent(byte[] bytes) : HttpContent
        {
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                return SerializeToStreamAsync(stream, context, CancellationToken.None);
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            {
                for (var offset = 0; offset < bytes.Length; offset += 1024)
                {
                    await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(1024, bytes.Length - offset)), cancellationToken);
                }
            }
        }
    }
}
