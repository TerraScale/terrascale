namespace TerraScale.Auth.Tests;

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;

/// <summary>Upstream identity connections end to end: the management API, the OIDC
/// authorization code flow against a real in-process fake IdP over real sockets,
/// and SAML SSO with signed responses posted to the ACS.</summary>
[NotInParallel]
public sealed class ConnectionsE2eTests
{
    // ---- Management API ------------------------------------------------------------------

    [Test]
    public async Task ManagementApiAuthenticatesAndManagesConnections()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            // Auth gate: no key, and a valid key pointed at the wrong slug.
            using var noAuth = await service.Http.PostAsync($"/v1/{slug}/connections", JsonContent.Create(new { type = "saml", name = "x" }));
            await Assert.That(noAuth.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            using var wrongSlug = PostJson($"/v1/{slug}-wrong/connections", apiKey, new { type = "saml", name = "x" });
            await Assert.That((await service.Http.SendAsync(wrongSlug)).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

            // Validation: bad type, missing SAML fields, bad PEM.
            using var badType = PostJson($"/v1/{slug}/connections", apiKey, new { type = "magic", name = "x" });
            await Assert.That((await service.Http.SendAsync(badType)).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            using var missingFields = PostJson($"/v1/{slug}/connections", apiKey, new { type = "saml", name = "x" });
            await Assert.That((await service.Http.SendAsync(missingFields)).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            using var badPem = PostJson($"/v1/{slug}/connections", apiKey, new { type = "saml", name = "x", idp_entity_id = "https://idp.example/metadata", sso_redirect_url = "https://idp.example/sso", x509_certificate_pem = "not a pem" });
            await Assert.That((await service.Http.SendAsync(badPem)).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

            // Create.
            using var created = PostJson($"/v1/{slug}/connections", apiKey, new
            {
                type = "saml",
                name = "Acme SSO",
                idp_entity_id = "https://idp.example/metadata",
                sso_redirect_url = "https://idp.example/sso",
                x509_certificate_pem = pem,
            });
            var createdResponse = await service.Http.SendAsync(created);
            await Assert.That(createdResponse.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var createdBody = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
            var connectionId = createdBody.GetProperty("id").GetString()!;
            await Assert.That(createdBody.GetProperty("type").GetString()).IsEqualTo("saml");
            await Assert.That(createdBody.GetProperty("enabled").GetBoolean()).IsTrue();
            await Assert.That(createdBody.TryGetProperty("client_secret", out _)).IsFalse();

            // Unreachable discovery is a 400, not a crash.
            using var badDiscovery = PostJson($"/v1/{slug}/connections", apiKey, new { type = "oidc", name = "dead", issuer_url = "http://127.0.0.1:9/metadata", client_id = "cid", client_secret = "sec" });
            await Assert.That((await service.Http.SendAsync(badDiscovery)).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

            // List (JSON management list and the public enabled-only list).
            using var list = Get($"/v1/{slug}/connections", apiKey);
            var listBody = await (await service.Http.SendAsync(list)).Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(listBody.GetProperty("data").GetArrayLength()).IsEqualTo(1);
            await Assert.That(listBody.GetProperty("data")[0].GetProperty("id").GetString()).IsEqualTo(connectionId);
            var publicList = await (await service.Http.GetAsync($"/ui/{slug}/connections")).Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(publicList.GetArrayLength()).IsEqualTo(1);
            await Assert.That(publicList[0].GetProperty("name").GetString()).IsEqualTo("Acme SSO");
            var listPage = await service.Http.GetAsync($"/ui/{slug}/connections/list");
            await Assert.That(listPage.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var listPageHtml = await listPage.Content.ReadAsStringAsync();
            await Assert.That(listPageHtml.Contains("Continue with Acme SSO")).IsTrue();
            await Assert.That(listPageHtml.Contains($"/ui/{slug}/connections/{connectionId}/saml/login")).IsTrue();

            // Disable: sign-in entry points disappear (404), public lists drop it.
            using var disabled = new HttpRequestMessage(HttpMethod.Patch, $"/v1/{slug}/connections/{connectionId}")
            {
                Content = JsonContent.Create(new { enabled = false }),
            };
            disabled.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            await Assert.That((await service.Http.SendAsync(disabled)).StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/saml/login")).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            var disabledList = await (await service.Http.GetAsync($"/ui/{slug}/connections")).Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(disabledList.GetArrayLength()).IsEqualTo(0);

            // Delete.
            using var deleted = new HttpRequestMessage(HttpMethod.Delete, $"/v1/{slug}/connections/{connectionId}") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", apiKey) } };
            await Assert.That((await service.Http.SendAsync(deleted)).StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            using var deletedAgain = new HttpRequestMessage(HttpMethod.Delete, $"/v1/{slug}/connections/{connectionId}") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", apiKey) } };
            await Assert.That((await service.Http.SendAsync(deletedAgain)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task OidcSecretRotatesThroughThePatchEndpoint()
    {
        var service = await AuthServiceFixture.GetAsync();
        await using var idp = await FakeIdP.StartAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (connectionId, _) = await CreateOidcConnectionAsync(service, slug, apiKey, idp);

        using var rotated = new HttpRequestMessage(HttpMethod.Patch, $"/v1/{slug}/connections/{connectionId}")
        {
            Content = JsonContent.Create(new { client_secret = "rotated-secret" }),
        };
        rotated.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        await Assert.That((await service.Http.SendAsync(rotated)).StatusCode).IsEqualTo(HttpStatusCode.OK);

        var connection = (await service.Store.ListConnectionsAsync(instanceId)).Single(c => c.Id == connectionId);
        await Assert.That(Encoding.UTF8.GetString(SecretBox.Decrypt(AuthServiceFixture.MasterKey, connection.ClientSecretBox!))).IsEqualTo("rotated-secret");
    }

    // ---- OIDC sign-in ----------------------------------------------------------------------

    [Test]
    public async Task OidcAuthorizationCodeFlowSignsUserIn()
    {
        var service = await AuthServiceFixture.GetAsync();
        await using var idp = await FakeIdP.StartAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (connectionId, _) = await CreateOidcConnectionAsync(service, slug, apiKey, idp);
        var callback = $"https://auth.test/{slug}/connections/{connectionId}/oidc/callback";

        using var authorize = await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/oidc/authorize");
        await Assert.That(authorize.StatusCode).IsEqualTo(HttpStatusCode.Found);
        var query = ParseQuery(authorize.Headers.Location!.ToString());
        await Assert.That(authorize.Headers.Location!.ToString().StartsWith(idp.AuthorizationUrl + "?")).IsTrue();
        await Assert.That(query["client_id"]).IsEqualTo(idp.ClientId);
        await Assert.That(query["redirect_uri"]).IsEqualTo(callback);
        await Assert.That(query["response_type"]).IsEqualTo("code");
        await Assert.That(query["scope"]).IsEqualTo("openid email profile");
        await Assert.That(query["state"].Length).IsGreaterThanOrEqualTo(32);

        using var callbackResponse = await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/oidc/callback?code=test-code&state={Uri.EscapeDataString(query["state"])}");
        await Assert.That(callbackResponse.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(callbackResponse.Headers.Location!.ToString()).IsEqualTo($"/ui/{slug}/account");
        var cookie = await SessionCookieAsync(callbackResponse);

        // The token exchange reached the IdP as client_secret_post with the stored secret.
        Dictionary<string, string> tokenRequest;
        lock (idp.TokenRequests)
            tokenRequest = idp.TokenRequests.Single();
        await Assert.That(tokenRequest["grant_type"]).IsEqualTo("authorization_code");
        await Assert.That(tokenRequest["code"]).IsEqualTo("test-code");
        await Assert.That(tokenRequest["client_id"]).IsEqualTo(idp.ClientId);
        await Assert.That(tokenRequest["client_secret"]).IsEqualTo(idp.ClientSecret);
        await Assert.That(tokenRequest["redirect_uri"]).IsEqualTo(callback);

        var user = await service.Store.FindUserByEmailAsync(instanceId, idp.Email)
            ?? throw new InvalidOperationException("user missing from store");
        await Assert.That(user.EmailVerified).IsTrue();
        await Assert.That(user.PasswordHash).IsNull();
        await Assert.That(user.LastSignInAt).IsNotNull();
        await Assert.That(user.Name).IsEqualTo("Jane Doe");

        await Assert.That(await AuditMethodAsync(service, instanceId, user.Id)).IsEqualTo($"oidc:{connectionId}");

        using var account = await service.Http.SendAsync(Get($"/ui/{slug}/account", cookie: cookie));
        await Assert.That(account.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The state is single-use.
        using var replay = await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/oidc/callback?code=test-code&state={Uri.EscapeDataString(query["state"])}");
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await replay.Content.ReadAsStringAsync()).Contains("Sign-in failed")).IsTrue();
    }

    [Test]
    public async Task OidcFallsBackToUserinfoWhenIdTokenHasNoEmail()
    {
        var service = await AuthServiceFixture.GetAsync();
        await using var idp = await FakeIdP.StartAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        idp.IncludeEmailClaim = false;
        var (connectionId, _) = await CreateOidcConnectionAsync(service, slug, apiKey, idp);

        var state = await AuthorizeStateAsync(service, slug, connectionId);
        using var callbackResponse = await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/oidc/callback?code=test-code&state={Uri.EscapeDataString(state)}");
        await Assert.That(callbackResponse.StatusCode).IsEqualTo(HttpStatusCode.Found);
        var user = await service.Store.FindUserByEmailAsync(instanceId, idp.Email)
            ?? throw new InvalidOperationException("userinfo fallback did not create the user");
        await Assert.That(user.EmailVerified).IsTrue();
    }

    [Test]
    public async Task OidcRejectsIdTokenSignedWithTheWrongKey()
    {
        var service = await AuthServiceFixture.GetAsync();
        await using var idp = await FakeIdP.StartAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        idp.SignWithWrongKey = true;
        var (connectionId, _) = await CreateOidcConnectionAsync(service, slug, apiKey, idp);

        var state = await AuthorizeStateAsync(service, slug, connectionId);
        using var callbackResponse = await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/oidc/callback?code=test-code&state={Uri.EscapeDataString(state)}");
        await Assert.That(callbackResponse.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(callbackResponse.Headers.TryGetValues("Set-Cookie", out _)).IsFalse();
        await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, idp.Email)).IsNull();
    }

    [Test]
    public async Task OidcRejectsUnknownState()
    {
        var service = await AuthServiceFixture.GetAsync();
        await using var idp = await FakeIdP.StartAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (connectionId, _) = await CreateOidcConnectionAsync(service, slug, apiKey, idp);

        using var callbackResponse = await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/oidc/callback?code=test-code&state=forged-state");
        await Assert.That(callbackResponse.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        int tokenRequestCount;
        lock (idp.TokenRequests)
            tokenRequestCount = idp.TokenRequests.Count;
        await Assert.That(tokenRequestCount).IsEqualTo(0); // rejected before any upstream call
        await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, idp.Email)).IsNull();
    }

    [Test]
    public async Task OidcUnverifiedEmailMustNotLinkOrCreateAccounts()
    {
        var service = await AuthServiceFixture.GetAsync();
        await using var idp = await FakeIdP.StartAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (connectionId, _) = await CreateOidcConnectionAsync(service, slug, apiKey, idp);

        // A local password account exists for the victim.
        var victim = $"victim{Guid.NewGuid().ToString("N")[..8]}@example.test";
        using (var signUp = await service.Http.PostAsync($"/ui/{slug}/sign-up",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = victim, ["password"] = "password123", ["name"] = "Victim" })))
        {
            await Assert.That(signUp.StatusCode).IsEqualTo(HttpStatusCode.Found);
        }
        var before = await service.Store.FindUserByEmailAsync(instanceId, victim)
            ?? throw new InvalidOperationException("victim missing from store");

        // The upstream asserts the victim's email as unverified: the callback must reject it,
        // not sign the attacker into the victim's account.
        idp.Email = victim;
        idp.EmailVerifiedClaim = false;
        var state = await AuthorizeStateAsync(service, slug, connectionId);
        using var takeover = await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/oidc/callback?code=test-code&state={Uri.EscapeDataString(state)}");
        await Assert.That(takeover.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(takeover.Headers.TryGetValues("Set-Cookie", out _)).IsFalse();

        // An absent email_verified claim is equally unverified.
        idp.EmailVerifiedClaim = null;
        state = await AuthorizeStateAsync(service, slug, connectionId);
        using var absent = await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/oidc/callback?code=test-code&state={Uri.EscapeDataString(state)}");
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(absent.Headers.TryGetValues("Set-Cookie", out _)).IsFalse();

        // The victim's account was untouched: password intact, no SSO sign-in recorded.
        var after = await service.Store.FindUserByEmailAsync(instanceId, victim)
            ?? throw new InvalidOperationException("victim missing from store");
        await Assert.That(after.PasswordHash).IsNotNull();
        await Assert.That(after.LastSignInAt).IsEqualTo(before.LastSignInAt);
        var oidcSignIn = (await service.Store.ListAuditEventsAsync(instanceId, 100))
            .Any(e => e.Type == "user.signed_in" && e.ActorId == after.Id
                && e.Data.TryGetValue("method", out var method) && method.ToString() == $"oidc:{connectionId}");
        await Assert.That(oidcSignIn).IsFalse();
    }

    [Test]
    public async Task OidcPicksUpRotatedUpstreamKeysWithoutWaitingForCacheExpiry()
    {
        var service = await AuthServiceFixture.GetAsync();
        await using var idp = await FakeIdP.StartAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (connectionId, _) = await CreateOidcConnectionAsync(service, slug, apiKey, idp);

        // First sign-in primes the 1h JWKS cache.
        var state = await AuthorizeStateAsync(service, slug, connectionId);
        using var first = await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/oidc/callback?code=test-code&state={Uri.EscapeDataString(state)}");
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Found);

        // The IdP rotates its signing key; the cached JWKS is instantly stale but sign-in must still work.
        idp.RotateSigningKey();
        state = await AuthorizeStateAsync(service, slug, connectionId);
        using var second = await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/oidc/callback?code=test-code&state={Uri.EscapeDataString(state)}");
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(second.Headers.Location!.ToString()).IsEqualTo($"/ui/{slug}/account");
    }

    [Test]
    public async Task OidcMalformedUpstreamResponsesNever500()
    {
        var service = await AuthServiceFixture.GetAsync();
        await using var idp = await FakeIdP.StartAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (connectionId, _) = await CreateOidcConnectionAsync(service, slug, apiKey, idp);

        // A 200 token response with a non-JSON body: a clean 502, not an unhandled exception.
        idp.GarbageTokenResponse = true;
        var state = await AuthorizeStateAsync(service, slug, connectionId);
        using var garbage = await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/oidc/callback?code=test-code&state={Uri.EscapeDataString(state)}");
        await Assert.That(garbage.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
        await Assert.That((await garbage.Content.ReadAsStringAsync()).Contains("Sign-in failed")).IsTrue();

        // An unsigned alg=none id_token: rejected, no session, no user.
        idp.GarbageTokenResponse = false;
        idp.SignAlgNone = true;
        state = await AuthorizeStateAsync(service, slug, connectionId);
        using var none = await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/oidc/callback?code=test-code&state={Uri.EscapeDataString(state)}");
        await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(none.Headers.TryGetValues("Set-Cookie", out _)).IsFalse();
        await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, idp.Email)).IsNull();
    }

    // ---- SAML sign-in ------------------------------------------------------------------------

    [Test]
    public async Task SamlRedirectBindingAndAcsSignUserIn()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";

            using var login = await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/saml/login");
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.Found);
            var location = login.Headers.Location!.ToString();
            await Assert.That(location.StartsWith("https://idp.example/sso?SAMLRequest=")).IsTrue();
            var query = ParseQuery(location);
            var requestXml = Inflate(query["SAMLRequest"]);
            await Assert.That(requestXml.Contains($"AssertionConsumerServiceURL=\"{acs}\"")).IsTrue();
            await Assert.That(requestXml.Contains($"Destination=\"https://idp.example/sso\"")).IsTrue();
            await Assert.That(requestXml.Contains($"<saml:Issuer>{acs}</saml:Issuer>")).IsTrue();
            var requestId = XmlId(requestXml);

            var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, requestId);
            var samlResponse = BuildSignedSamlResponse(certificate, requestId, acs, email: "jane@acme.test");

            using var acsResponse = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = samlResponse, ["RelayState"] = relayState }));
            await Assert.That(acsResponse.StatusCode).IsEqualTo(HttpStatusCode.Found);
            await Assert.That(acsResponse.Headers.Location!.ToString()).IsEqualTo($"/ui/{slug}/account");
            var cookie = await SessionCookieAsync(acsResponse);

            var user = await service.Store.FindUserByEmailAsync(instanceId, "jane@acme.test")
                ?? throw new InvalidOperationException("user missing from store");
            await Assert.That(user.EmailVerified).IsTrue();
            await Assert.That(user.Name).IsEqualTo("Jane Doe");
            await Assert.That(await AuditMethodAsync(service, instanceId, user.Id)).IsEqualTo($"saml:{connectionId}");

            using var account = await service.Http.SendAsync(Get($"/ui/{slug}/account", cookie: cookie));
            await Assert.That(account.StatusCode).IsEqualTo(HttpStatusCode.OK);

            // RelayState is single-use.
            using var replay = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = samlResponse, ["RelayState"] = relayState }));
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
    }

    [Test]
    public async Task SamlRejectsWrongAudience()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";
            var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_anything");
            var samlResponse = BuildSignedSamlResponse(certificate, "_anything", acs, email: "jane@acme.test", audience: "https://other-sp.example/acs");

            using var response = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = samlResponse, ["RelayState"] = relayState }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "jane@acme.test")).IsNull();
        }
    }

    [Test]
    public async Task SamlRejectsExpiredConditions()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";
            var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_anything");
            var samlResponse = BuildSignedSamlResponse(certificate, "_anything", acs, email: "jane@acme.test", notOnOrAfter: DateTimeOffset.UtcNow.AddMinutes(-5));

            using var response = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = samlResponse, ["RelayState"] = relayState }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "jane@acme.test")).IsNull();
        }
    }

    [Test]
    public async Task SamlRejectsTamperedNameId()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";
            var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_anything");
            // Signed for jane@, then rewritten to attacker@ after signing.
            var samlResponse = BuildSignedSamlResponse(certificate, "_anything", acs, email: "jane@acme.test", tamperNameId: true);

            using var response = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = samlResponse, ["RelayState"] = relayState }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "attacker@acme.test")).IsNull();
        }
    }

    [Test]
    public async Task SamlRejectsUnsignedResponse()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";
            var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_anything");
            var samlResponse = BuildSignedSamlResponse(certificate, "_anything", acs, email: "jane@acme.test", sign: false);

            using var response = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = samlResponse, ["RelayState"] = relayState }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
    }

    [Test]
    public async Task SamlRejectsBadRelayState()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";
            var samlResponse = BuildSignedSamlResponse(certificate, "_anything", acs, email: "jane@acme.test");

            using var response = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = samlResponse, ["RelayState"] = "forged-relay-state" }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "jane@acme.test")).IsNull();
        }
    }

    [Test]
    public async Task SamlAcceptsResponseLevelSignature()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";
            var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_respSig");
            var samlResponse = BuildSignedSamlResponse(certificate, "_respSig", acs, email: "resp-signed@acme.test", signResponse: true);

            using var response = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = samlResponse, ["RelayState"] = relayState }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "resp-signed@acme.test")).IsNotNull();
        }
    }

    // ---- XML signature wrapping (XSW) ---------------------------------------------------------

    /// <summary>XSW "assertion wrap": the IdP-signed assertion for the attacker is stripped of its
    /// signature, hidden inside the attacker's own assertion (which carries the victim's NameID),
    /// and the lifted signature is re-attached to the attacker assertion. The signature still
    /// verifies — but only over the embedded decoy, never over the assertion being trusted.</summary>
    [Test]
    public async Task SamlRejectsAssertionSignatureWrapping()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";
            var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_xswA");

            var original = SignEnveloped(certificate, AssertionXml("_attacker" + Guid.NewGuid().ToString("N")[..8], "_xswA", acs, "attacker@acme.test"));
            var decoy = StripSignature(original);
            var lifted = CopiedSignature(original);
            var evil = $"""
                <saml:Assertion xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion" ID="_evil{Guid.NewGuid().ToString("N")[..8]}" IssueInstant="{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}" Version="2.0"><saml:Issuer>https://idp.example/metadata</saml:Issuer><saml:Subject><saml:NameID Format="urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress">victim@acme.test</saml:NameID></saml:Subject><saml:Conditions NotBefore="{DateTimeOffset.UtcNow.AddMinutes(-10):yyyy-MM-dd'T'HH:mm:ss'Z'}" NotOnOrAfter="{DateTimeOffset.UtcNow.AddMinutes(10):yyyy-MM-dd'T'HH:mm:ss'Z'}"><saml:AudienceRestriction><saml:Audience>{acs}</saml:Audience></saml:AudienceRestriction></saml:Conditions><saml:AttributeStatement><saml:Attribute Name="email"><saml:AttributeValue>victim@acme.test</saml:AttributeValue></saml:Attribute></saml:AttributeStatement><saml:Advice>{decoy}</saml:Advice>{lifted}</saml:Assertion>
                """;

            using var response = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = Base64Response(ResponseXml("_xswA", evil)), ["RelayState"] = relayState }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "victim@acme.test")).IsNull();
        }
    }

    /// <summary>XSW "response wrap": a signed Response is stripped of its signature, embedded inside an
    /// attacker Response that carries an unsigned assertion for the victim, and the lifted signature is
    /// re-attached to the wrapper. The signature verifies over the embedded original, not the wrapper.</summary>
    [Test]
    public async Task SamlRejectsResponseSignatureWrapping()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";
            var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_xswB");
            var responseId = "_origresp" + Guid.NewGuid().ToString("N")[..8];

            var original = SignEnveloped(certificate, ResponseXml("_xswB", AssertionXml("_wrapped" + Guid.NewGuid().ToString("N")[..8], "_xswB", acs, "attacker@acme.test"), responseId));
            var decoy = StripSignature(original);
            var lifted = CopiedSignature(original);
            var evil = $"""
                <samlp:Response xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion" ID="_evilresp{Guid.NewGuid().ToString("N")[..8]}" InResponseTo="_xswB" Version="2.0" IssueInstant="{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}" Destination="https://idp.example/sso"><samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/></samlp:Status><saml:Assertion ID="_evil{Guid.NewGuid().ToString("N")[..8]}" IssueInstant="{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}" Version="2.0"><saml:Issuer>https://idp.example/metadata</saml:Issuer><saml:Subject><saml:NameID Format="urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress">victim@acme.test</saml:NameID></saml:Subject><saml:Conditions NotBefore="{DateTimeOffset.UtcNow.AddMinutes(-10):yyyy-MM-dd'T'HH:mm:ss'Z'}" NotOnOrAfter="{DateTimeOffset.UtcNow.AddMinutes(10):yyyy-MM-dd'T'HH:mm:ss'Z'}"><saml:AudienceRestriction><saml:Audience>{acs}</saml:Audience></saml:AudienceRestriction></saml:Conditions><saml:AttributeStatement><saml:Attribute Name="email"><saml:AttributeValue>victim@acme.test</saml:AttributeValue></saml:Attribute></saml:AttributeStatement></saml:Assertion><samlp:Extensions>{decoy}</samlp:Extensions>{lifted}</samlp:Response>
                """;

            using var response = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = Base64Response(evil), ["RelayState"] = relayState }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "victim@acme.test")).IsNull();
        }
    }

    /// <summary>XSW "same-ID wrapper": the attacker Response reuses the signed original's ID, so a naive
    /// URI==ID check would pass — the duplicate ID must itself be rejected.</summary>
    [Test]
    public async Task SamlRejectsDuplicateIdResponseWrapping()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";
            var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_xswC");
            var responseId = "_clonedresp" + Guid.NewGuid().ToString("N")[..8];

            var original = SignEnveloped(certificate, ResponseXml("_xswC", AssertionXml("_wrapped" + Guid.NewGuid().ToString("N")[..8], "_xswC", acs, "attacker@acme.test"), responseId));
            var decoy = StripSignature(original);
            var lifted = CopiedSignature(original);
            var evil = $"""
                <samlp:Response xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion" ID="{responseId}" InResponseTo="_xswC" Version="2.0" IssueInstant="{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}" Destination="https://idp.example/sso"><samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/></samlp:Status><saml:Assertion ID="_evil{Guid.NewGuid().ToString("N")[..8]}" IssueInstant="{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}" Version="2.0"><saml:Issuer>https://idp.example/metadata</saml:Issuer><saml:Subject><saml:NameID Format="urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress">victim@acme.test</saml:NameID></saml:Subject><saml:Conditions NotBefore="{DateTimeOffset.UtcNow.AddMinutes(-10):yyyy-MM-dd'T'HH:mm:ss'Z'}" NotOnOrAfter="{DateTimeOffset.UtcNow.AddMinutes(10):yyyy-MM-dd'T'HH:mm:ss'Z'}"><saml:AudienceRestriction><saml:Audience>{acs}</saml:Audience></saml:AudienceRestriction></saml:Conditions><saml:AttributeStatement><saml:Attribute Name="email"><saml:AttributeValue>victim@acme.test</saml:AttributeValue></saml:Attribute></saml:AttributeStatement></saml:Assertion><samlp:Extensions>{decoy}</samlp:Extensions>{lifted}</samlp:Response>
                """;

            using var response = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = Base64Response(evil), ["RelayState"] = relayState }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "victim@acme.test")).IsNull();
        }
    }

    /// <summary>XSW "evil twin" (SAMLRaider classic): the attacker's assertion copies the signed
    /// assertion's ID and is placed where the SP reads it; the genuine signed assertion rides along.</summary>
    [Test]
    public async Task SamlRejectsEvilTwinAssertionWithCopiedId()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";
            var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_xswD");
            var assertionId = "_cloned" + Guid.NewGuid().ToString("N")[..8];

            var signed = SignEnveloped(certificate, AssertionXml(assertionId, "_xswD", acs, "attacker@acme.test"));
            var twin = AssertionXml(assertionId, "_xswD", acs, "victim@acme.test");

            using var response = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = Base64Response(ResponseXml("_xswD", twin + signed)), ["RelayState"] = relayState }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "victim@acme.test")).IsNull();
        }
    }

    [Test]
    public async Task SamlRejectsForeignSigningKey()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";
            var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_wrongkey");
            using var attackerKey = NewIdpCertificate().Certificate;
            var samlResponse = BuildSignedSamlResponse(attackerKey, "_wrongkey", acs, email: "attacker@acme.test");

            using var response = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = samlResponse, ["RelayState"] = relayState }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "attacker@acme.test")).IsNull();
        }
    }

    [Test]
    public async Task SamlRejectsFutureAndLongExpiredConditions()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";

            var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_future");
            using var future = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = BuildSignedSamlResponse(certificate, "_future", acs, email: "skew@acme.test", notBefore: DateTimeOffset.UtcNow.AddDays(1)), ["RelayState"] = relayState }));
            await Assert.That(future.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

            relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_stale");
            using var stale = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = BuildSignedSamlResponse(certificate, "_stale", acs, email: "skew@acme.test", notOnOrAfter: DateTimeOffset.UtcNow.AddDays(-1)), ["RelayState"] = relayState }));
            await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "skew@acme.test")).IsNull();
        }
    }

    [Test]
    public async Task SamlRejectsInResponseToMismatch()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";
            var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_expected");
            var samlResponse = BuildSignedSamlResponse(certificate, "_someOtherRequest", acs, email: "irt@acme.test");

            using var response = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = samlResponse, ["RelayState"] = relayState }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "irt@acme.test")).IsNull();
        }
    }

    [Test]
    public async Task SamlAudienceStrictness()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acs = $"https://auth.test/{slug}/connections/{connectionId}/saml/acs";

            // Substring/prefix/suffix/empty look-alikes must never pass.
            foreach (var (label, audience) in new[]
            {
                ("prefix", acs[..^4]),
                ("suffix", acs + "/extra"),
                ("substring", "https://auth.test"),
                ("empty", ""),
            })
            {
                var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_aud" + label);
                using var response = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = BuildSignedSamlResponse(certificate, "_aud" + label, acs, email: "aud@acme.test", audience: audience), ["RelayState"] = relayState }));
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "aud@acme.test")).IsNull();

            // Multiple audiences naming this SP among them are spec-valid (any-of within a restriction).
            var relayStateOk = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_audMulti");
            using var multi = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = BuildSignedSamlResponse(certificate, "_audMulti", acs, email: "multi-aud@acme.test", audiences: ["https://other-sp.example/acs", acs]), ["RelayState"] = relayStateOk }));
            await Assert.That(multi.StatusCode).IsEqualTo(HttpStatusCode.Found);
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "multi-aud@acme.test")).IsNotNull();

            // Two AudienceRestrictions where the second omits this SP: every restriction must name the SP.
            var assertionId = "_audRestr" + Guid.NewGuid().ToString("N")[..8];
            var now = DateTimeOffset.UtcNow;
            var before = now.AddMinutes(-10).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            var expiry = now.AddMinutes(10).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            var restricted = $"""
                <saml:Assertion xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion" ID="{assertionId}" IssueInstant="{now:yyyy-MM-dd'T'HH:mm:ss'Z'}" Version="2.0"><saml:Issuer>https://idp.example/metadata</saml:Issuer><saml:Subject><saml:NameID Format="urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress">restrict@acme.test</saml:NameID></saml:Subject><saml:Conditions NotBefore="{before}" NotOnOrAfter="{expiry}"><saml:AudienceRestriction><saml:Audience>{acs}</saml:Audience></saml:AudienceRestriction><saml:AudienceRestriction><saml:Audience>https://other-sp.example/acs</saml:Audience></saml:AudienceRestriction></saml:Conditions><saml:AttributeStatement><saml:Attribute Name="email"><saml:AttributeValue>restrict@acme.test</saml:AttributeValue></saml:Attribute></saml:AttributeStatement></saml:Assertion>
                """;
            var relayStateRestr = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_audRestr");
            using var restrict = await service.Http.PostAsync($"/ui/{slug}/connections/{connectionId}/saml/acs",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = Base64Response(ResponseXml("_audRestr", SignEnveloped(certificate, restricted))), ["RelayState"] = relayStateRestr }));
            await Assert.That(restrict.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, "restrict@acme.test")).IsNull();
        }
    }

    [Test]
    public async Task SamlAcsSurvivesMalformedInput()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await NewInstanceWithKeyAsync();
        var (certificate, pem) = NewIdpCertificate();
        using (certificate)
        {
            var connectionId = await CreateSamlConnectionAsync(service, slug, apiKey, pem);
            var acsUrl = $"/ui/{slug}/connections/{connectionId}/saml/acs";

            async Task<HttpResponseMessage> PostAsync(Func<string, HttpContent> content)
            {
                var relayState = await MintRelayStateAsync(service.Store, instanceId, connectionId, "_malformed");
                using var request = new HttpRequestMessage(HttpMethod.Post, acsUrl) { Content = content(relayState) };
                return await service.Http.SendAsync(request);
            }

            foreach (var (label, content) in new (string, Func<string, HttpContent>)[]
            {
                ("bad base64", relay => new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = "%%%not-base64%%%", ["RelayState"] = relay })),
                ("malformed xml", relay => new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("<samlp:Response xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol")), ["RelayState"] = relay })),
                ("missing response", relay => new FormUrlEncodedContent(new Dictionary<string, string> { ["RelayState"] = relay })),
                ("not a form", _ => new StringContent("plain text body")),
            })
            {
                using var response = await PostAsync(content);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }
        }
    }

    // ---- Arrange helpers ----------------------------------------------------------------------

    /// <summary>Store-arranged sk_ key (the operator endpoint's keys cannot resolve; see WebhooksE2eTests).</summary>
    private static async Task<(string InstanceId, string Slug, string ApiKey)> NewInstanceWithKeyAsync()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, _) = await service.CreateInstanceAsync("sso-" + Guid.NewGuid().ToString("N")[..8]);
        var apiKey = "sk_" + SecureTokens.RandomToken(40);
        await service.Store.CreateApiKeyAsync(new ApiKey
        {
            Id = "key_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Name = "connections-e2e",
            SecretKey = SecureTokens.Sha256Hex(apiKey),
            Scopes = ["*"],
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        return (instanceId, slug, apiKey);
    }

    private static HttpRequestMessage Get(string url, string? apiKey = null, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (apiKey is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private static HttpRequestMessage PostJson(string url, string apiKey, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static async Task<string> SessionCookieAsync(HttpResponseMessage response)
    {
        var header = (response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault() : null)
            ?? throw new InvalidOperationException("expected a session cookie");
        return header.Split(';')[0];
    }

    /// <summary>Creates an OIDC connection pinned at the fake IdP (discovery + explicit endpoint overrides).</summary>
    private static async Task<(string ConnectionId, JsonElement Body)> CreateOidcConnectionAsync(AuthServiceFixture service, string slug, string apiKey, FakeIdP idp)
    {
        using var created = PostJson($"/v1/{slug}/connections", apiKey, new
        {
            type = "oidc",
            name = "Fake OIDC",
            issuer_url = idp.Issuer,
            client_id = idp.ClientId,
            client_secret = idp.ClientSecret,
            authorization_endpoint = idp.AuthorizationUrl,
            token_endpoint = idp.TokenUrl,
            userinfo_endpoint = idp.UserinfoUrl,
            scopes = new[] { "openid", "email" },
        });
        var response = await service.Http.SendAsync(created);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetString()!, body);
    }

    private static async Task<string> CreateSamlConnectionAsync(AuthServiceFixture service, string slug, string apiKey, string pem)
    {
        using var created = PostJson($"/v1/{slug}/connections", apiKey, new
        {
            type = "saml",
            name = "Acme SSO",
            idp_entity_id = "https://idp.example/metadata",
            sso_redirect_url = "https://idp.example/sso",
            x509_certificate_pem = pem,
        });
        var response = await service.Http.SendAsync(created);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    /// <summary>Walks the authorize redirect and returns the minted state.</summary>
    private static async Task<string> AuthorizeStateAsync(AuthServiceFixture service, string slug, string connectionId)
    {
        using var authorize = await service.Http.GetAsync($"/ui/{slug}/connections/{connectionId}/oidc/authorize");
        await Assert.That(authorize.StatusCode).IsEqualTo(HttpStatusCode.Found);
        return ParseQuery(authorize.Headers.Location!.ToString())["state"];
    }

    private static async Task<string?> AuditMethodAsync(AuthServiceFixture service, string instanceId, string userId)
    {
        foreach (var auditEvent in await service.Store.ListAuditEventsAsync(instanceId, 100))
            if (auditEvent.Type == "user.signed_in"
                && auditEvent.ActorId == userId
                && auditEvent.Data.TryGetValue("method", out var method)
                && method is JsonElement { ValueKind: JsonValueKind.String } methodElement)
                return methodElement.GetString();
        return null;
    }

    /// <summary>Mints a RelayState exactly like the login endpoint does: connection id in UserId,
    /// "vrf_&lt;rand&gt;:&lt;requestId&gt;" in the record id.</summary>
    private static async Task<string> MintRelayStateAsync(AuthStore store, string instanceId, string connectionId, string requestId)
    {
        var token = SecureTokens.RandomToken(40);
        var now = DateTimeOffset.UtcNow;
        await store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = "vrf_" + SecureTokens.RandomToken(20) + ":" + requestId,
            InstanceId = instanceId,
            UserId = connectionId,
            Purpose = "saml.state",
            TokenKey = SecureTokens.Sha256Hex(token),
            ExpiresAt = now.AddMinutes(10).ToUnixTimeSeconds(),
            CreatedAt = now.ToUnixTimeSeconds(),
        });
        return token;
    }

    private static (X509Certificate2 Certificate, string Pem) NewIdpCertificate()
    {
        var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test SAML IdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        return (certificate, certificate.ExportCertificatePem());
    }

    private static Dictionary<string, string> ParseQuery(string url)
    {
        var query = url[(url.IndexOf('?') + 1)..];
        return query.Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(
            parts => Uri.UnescapeDataString(parts[0]),
            parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
    }

    private static string Inflate(string base64)
    {
        using var input = new MemoryStream(Convert.FromBase64String(base64));
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static string XmlId(string xml)
    {
        var document = new XmlDocument();
        document.LoadXml(xml);
        return document.DocumentElement!.GetAttribute("ID");
    }

    /// <summary>Builds a SAML Response with an enveloped-signed Assertion (RS256, exc-C14N),
    /// base64-encoded for the POST binding.</summary>
    private static string BuildSignedSamlResponse(
        X509Certificate2 certificate, string requestId, string acs, string email,
        string? audience = null, DateTimeOffset? notOnOrAfter = null, bool tamperNameId = false, bool sign = true,
        bool signResponse = false, string[]? audiences = null, DateTimeOffset? notBefore = null)
    {
        var assertionId = "_assertion" + Guid.NewGuid().ToString("N")[..12];
        var assertion = AssertionXml(assertionId, requestId, acs, email, audiences ?? (audience is null ? null : [audience]), notBefore, notOnOrAfter);

        if (sign && !signResponse)
            assertion = SignEnveloped(certificate, assertion);

        if (tamperNameId)
            assertion = assertion.Replace(email, "attacker@acme.test");

        var response = ResponseXml(requestId, assertion);
        if (signResponse)
            response = SignEnveloped(certificate, response);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(response));
    }

    private static string AssertionXml(
        string assertionId, string requestId, string acs, string email, string[]? audiences = null,
        DateTimeOffset? notBefore = null, DateTimeOffset? notOnOrAfter = null)
    {
        var now = DateTimeOffset.UtcNow;
        var issueInstant = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var before = (notBefore ?? now.AddMinutes(-10)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var expiry = (notOnOrAfter ?? now.AddMinutes(10)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var audience = string.Join("", (audiences ?? [acs]).Select(a => $"<saml:Audience>{a}</saml:Audience>"));
        return $"""
            <saml:Assertion xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion" ID="{assertionId}" IssueInstant="{issueInstant}" Version="2.0"><saml:Issuer>https://idp.example/metadata</saml:Issuer><saml:Subject><saml:NameID Format="urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress">{email}</saml:NameID><saml:SubjectConfirmation Method="urn:oasis:names:tc:SAML:2.0:cm:bearer"><saml:SubjectConfirmationData InResponseTo="{requestId}" Recipient="{acs}" NotOnOrAfter="{expiry}"/></saml:SubjectConfirmation></saml:Subject><saml:Conditions NotBefore="{before}" NotOnOrAfter="{expiry}"><saml:AudienceRestriction>{audience}</saml:AudienceRestriction></saml:Conditions><saml:AuthnStatement AuthnInstant="{issueInstant}" SessionIndex="{assertionId}"><saml:AuthnContext><saml:AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:Password</saml:AuthnContextClassRef></saml:AuthnContext></saml:AuthnStatement><saml:AttributeStatement><saml:Attribute Name="email"><saml:AttributeValue>{email}</saml:AttributeValue></saml:Attribute><saml:Attribute Name="firstName"><saml:AttributeValue>Jane</saml:AttributeValue></saml:Attribute><saml:Attribute Name="lastName"><saml:AttributeValue>Doe</saml:AttributeValue></saml:Attribute></saml:AttributeStatement></saml:Assertion>
            """;
    }

    private static string ResponseXml(string requestId, string assertion, string? responseId = null)
    {
        var issueInstant = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        return $"""
            <samlp:Response xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" ID="{responseId ?? "_response" + Guid.NewGuid().ToString("N")[..12]}" InResponseTo="{requestId}" Version="2.0" IssueInstant="{issueInstant}" Destination="https://idp.example/sso"><saml:Issuer xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion">https://idp.example/metadata</saml:Issuer><samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/></samlp:Status>{assertion}</samlp:Response>
            """;
    }

    /// <summary>Enveloped-signs a standalone XML element (RS256, exc-C14N) by its ID attribute.</summary>
    private static string SignEnveloped(X509Certificate2 certificate, string xml)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml);
        var signedXml = new SignedXml(document)
        {
            SigningKey = certificate.GetRSAPrivateKey()!,
        };
        var reference = new Reference("#" + document.DocumentElement!.GetAttribute("ID"));
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        reference.DigestMethod = SignedXml.XmlDsigSHA256Url;
        signedXml.AddReference(reference);
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signedXml.SignedInfo!.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificate));
        signedXml.KeyInfo = keyInfo;
        signedXml.ComputeSignature();
        document.DocumentElement.AppendChild(document.ImportNode(signedXml.GetXml(), true));
        return document.DocumentElement.OuterXml;
    }

    // ---- XML signature wrapping (XSW) helpers: keep the IdP-signed content, drop or move the proof --

    /// <summary>Removes the root element's ds:Signature child — the IdP-signed content without its proof.</summary>
    private static string StripSignature(string xml)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml);
        var root = document.DocumentElement!;
        foreach (var child in root.ChildNodes.Cast<XmlNode>()
                     .Where(child => child is XmlElement element
                         && element.LocalName == "Signature"
                         && element.NamespaceURI == SignedXml.XmlDsigNamespaceUrl).ToList())
            root.RemoveChild(child);
        return root.OuterXml;
    }

    /// <summary>The root element's ds:Signature child verbatim — the blob a wrapper attack copies around.</summary>
    private static string CopiedSignature(string xml)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml);
        return document.DocumentElement!.ChildNodes.Cast<XmlNode>()
            .First(child => child is XmlElement element
                && element.LocalName == "Signature"
                && element.NamespaceURI == SignedXml.XmlDsigNamespaceUrl).OuterXml;
    }

    private static string Base64Response(string responseXml) => Convert.ToBase64String(Encoding.UTF8.GetBytes(responseXml));

    // ---- Fake OIDC identity provider -----------------------------------------------------------

    /// <summary>A real in-process OIDC provider: discovery, token (signed RS256 id_tokens), JWKS, userinfo.
    /// HttpListener cannot bind :0, so it takes the first free port in 18101-18119.</summary>
    private sealed class FakeIdP : IAsyncDisposable
    {
        private const string Kid = "fake-idp-rsa-1";
        private readonly HttpListener listener;
        private RSA signingKey = RSA.Create(2048);
        private string currentKid = Kid;
        private readonly RSA wrongKey = RSA.Create(2048);
        private readonly Task acceptLoop;
        private readonly string baseUrl;

        /// <summary>Simulates an upstream key rotation: new key material and a new kid in the JWKS.</summary>
        public void RotateSigningKey()
        {
            signingKey.Dispose();
            signingKey = RSA.Create(2048);
            currentKid = "fake-idp-rsa-rotated";
        }

        private FakeIdP(HttpListener listener, string baseUrl)
        {
            this.listener = listener;
            this.baseUrl = baseUrl;
            acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public string Issuer => baseUrl;
        public string AuthorizationUrl => baseUrl + "/authorize";
        public string TokenUrl => baseUrl + "/token";
        public string UserinfoUrl => baseUrl + "/userinfo";

        public string ClientId { get; } = "cid_" + Guid.NewGuid().ToString("N")[..10];
        public string ClientSecret { get; } = "csec_" + Guid.NewGuid().ToString("N")[..16];
        public string Email { get; set; } = $"oidc{Guid.NewGuid().ToString("N")[..8]}@example.test";
        public bool IncludeEmailClaim { get; set; } = true;
        /// <summary>null omits the claim entirely; the email claim then carries no verification.</summary>
        public bool? EmailVerifiedClaim { get; set; } = true;
        public bool SignWithWrongKey { get; set; }
        public bool SignAlgNone { get; set; }
        public bool GarbageTokenResponse { get; set; }
        public List<Dictionary<string, string>> TokenRequests { get; } = [];

        public static async Task<FakeIdP> StartAsync()
        {
            for (var port = 18101; port <= 18119; port++)
            {
                var candidate = new HttpListener();
                candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    candidate.Start();
                    return new FakeIdP(candidate, $"http://127.0.0.1:{port}");
                }
                catch (HttpListenerException)
                {
                    candidate.Close();
                }
            }
            throw new InvalidOperationException("no free fake IdP port in 18101-18119");
        }

        private async Task AcceptLoopAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return; // listener stopped at dispose
                }
                try
                {
                    await HandleAsync(context);
                }
                catch (Exception)
                {
                    context.Response.Abort();
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath;
            switch (path)
            {
                case "/.well-known/openid-configuration":
                    await WriteJsonAsync(context, new Dictionary<string, object>
                    {
                        ["issuer"] = baseUrl,
                        ["authorization_endpoint"] = AuthorizationUrl,
                        ["token_endpoint"] = TokenUrl,
                        ["userinfo_endpoint"] = UserinfoUrl,
                        ["jwks_uri"] = baseUrl + "/jwks.json",
                        ["response_types_supported"] = new[] { "code" },
                    });
                    return;
                case "/jwks.json":
                    var parameters = signingKey.ExportParameters(false);
                    await WriteJsonAsync(context, new Dictionary<string, object>
                    {
                        ["keys"] = new[]
                        {
                            new Dictionary<string, object>
                            {
                                ["kty"] = "RSA",
                                ["use"] = "sig",
                                ["alg"] = "RS256",
                                ["kid"] = currentKid,
                                ["n"] = Encodings.Base64UrlEncode(parameters.Modulus!),
                                ["e"] = Encodings.Base64UrlEncode(parameters.Exponent!),
                            },
                        },
                    });
                    return;
                case "/token":
                    var form = await ReadFormAsync(context.Request);
                    lock (TokenRequests)
                        TokenRequests.Add(form);
                    if (GarbageTokenResponse)
                    {
                        var garbage = Encoding.UTF8.GetBytes("not json at all");
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = garbage.Length;
                        await context.Response.OutputStream.WriteAsync(garbage);
                        context.Response.Close();
                        return;
                    }
                    if (form.GetValueOrDefault("client_secret") != ClientSecret)
                    {
                        context.Response.StatusCode = 401;
                        context.Response.Close();
                        return;
                    }
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var claims = new Dictionary<string, object>
                    {
                        ["iss"] = baseUrl,
                        ["sub"] = "fake-idp-subject",
                        ["aud"] = ClientId,
                        ["iat"] = now,
                        ["exp"] = now + 600,
                        ["name"] = "Jane Doe",
                    };
                    if (IncludeEmailClaim)
                    {
                        claims["email"] = Email;
                        if (EmailVerifiedClaim is { } verified)
                            claims["email_verified"] = verified;
                    }
                    await WriteJsonAsync(context, new Dictionary<string, object>
                    {
                        ["access_token"] = "at_" + Guid.NewGuid().ToString("N")[..12],
                        ["token_type"] = "Bearer",
                        ["expires_in"] = 3600,
                        ["id_token"] = SignAlgNone ? UnsignedToken(claims) : SignRs256(SignWithWrongKey ? wrongKey : signingKey, claims),
                    });
                    return;
                case "/userinfo":
                    var userinfo = new Dictionary<string, object>
                    {
                        ["sub"] = "fake-idp-subject",
                        ["email"] = Email,
                        ["name"] = "Jane Doe",
                    };
                    if (EmailVerifiedClaim is { } userinfoVerified)
                        userinfo["email_verified"] = userinfoVerified;
                    await WriteJsonAsync(context, userinfo);
                    return;
                default:
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
            }
        }

        private string SignRs256(RSA key, Dictionary<string, object> claims)
        {
            var header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = currentKid });
            var payload = JsonSerializer.SerializeToUtf8Bytes(claims);
            var signingInput = Encoding.ASCII.GetBytes(Encodings.Base64UrlEncode(header) + "." + Encodings.Base64UrlEncode(payload));
            var signature = key.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return Encodings.Base64UrlEncode(header) + "." + Encodings.Base64UrlEncode(payload) + "." + Encodings.Base64UrlEncode(signature);
        }

        /// <summary>An unsigned alg=none token with a trailing empty signature segment.</summary>
        private static string UnsignedToken(Dictionary<string, object> claims)
        {
            var header = Encodings.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["alg"] = "none", ["typ"] = "JWT" }));
            var payload = Encodings.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims));
            return header + "." + payload + ".";
        }

        private static async Task WriteJsonAsync(HttpListenerContext context, Dictionary<string, object> body)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        private static async Task<Dictionary<string, string>> ReadFormAsync(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            return body
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            listener.Close();
            await acceptLoop;
            signingKey.Dispose();
            wrongKey.Dispose();
        }
    }
}
