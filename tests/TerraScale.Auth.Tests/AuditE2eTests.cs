namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>Audit log probes: no secret material ever lands in audit data, actors
/// identify the acting surface (user, API key, or directory), significant
/// management actions emit events, and the audit query API is instance-isolated.</summary>
[NotInParallel]
public sealed class AuditE2eTests
{
    private const string RedirectUri = "https://app.test/callback";
    private static readonly Regex RecoveryCodePattern = new("<code>([A-Za-z0-9]{10})</code>");

    // ---- secrets ------------------------------------------------------------------

    [Test]
    public async Task SecretsNeverAppearInAuditEventData()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await service.CreateInstanceAsync("audit-sec-" + Guid.NewGuid().ToString("N")[..8]);
        var http = service.Http;
        var store = service.Store;
        var forbidden = new List<string> { apiKey };
        void Forbid(string? value)
        {
            if (!string.IsNullOrEmpty(value))
                forbidden.Add(value);
        }

        // Sign-up through the hosted UI plants the password; its hash and the
        // verify-email token key are only known to the store.
        var password = "correct horse battery staple";
        var email = $"audit-{Guid.NewGuid().ToString("N")[..8]}@example.com";
        using (var signUp = await http.PostAsync($"/ui/{slug}/sign-up",
            Form(("email", email), ("password", password), ("name", "Audit Probe"))))
        {
            await Assert.That(signUp.StatusCode).IsEqualTo(HttpStatusCode.Found);
            Forbid(SessionCookie(signUp));
        }
        var user = (await store.FindUserByEmailAsync(instanceId, email))!;
        Forbid(password);
        Forbid(user.PasswordHash);
        foreach (var token in await store.ListUserVerificationTokensAsync(user.Id, "verify_email"))
            Forbid(token.TokenKey);

        // Password sign-in mints a session token.
        HttpResponseMessage signIn;
        using (signIn = await http.PostAsync($"/ui/{slug}/sign-in", Form(("email", email), ("password", password))))
        {
            await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.Found);
            Forbid(SessionCookie(signIn));
        }

        // MFA enrollment: the TOTP secret, its SecretBox, and the recovery codes are
        // the crown jewels — none of them may surface in the audit trail.
        var cookie = SessionCookie(signIn)!;
        using (var enroll = await http.SendAsync(CookieRequest(HttpMethod.Post, $"/ui/{slug}/mfa/enroll/start", cookie)))
            await Assert.That(enroll.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var totp = (await store.ListMfaFactorsAsync(user.Id)).Single(f => f.Type == "totp");
        var secret = SecretBox.Decrypt(AuthServiceFixture.MasterKey, totp.SecretBox!);
        Forbid(Encodings.Base32Encode(secret));
        Forbid(totp.SecretBox);
        using (var confirm = await http.SendAsync(CookieRequest(HttpMethod.Post,
            $"/ui/{slug}/mfa/enroll/confirm?factor={totp.Id}", cookie, Form(("code", Totp.Compute(secret, DateTimeOffset.UtcNow))))))
        {
            await Assert.That(confirm.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var html = await confirm.Content.ReadAsStringAsync();
            foreach (var code in RecoveryCodePattern.Matches(html).Select(m => m.Groups[1].Value))
            {
                Forbid(code);
                Forbid(SecureTokens.Sha256Hex(code));
            }
        }

        // Passkey registration: only public key material should ever be referenced.
        using (var authenticator = new PasskeyE2eTests.FakeAuthenticator())
        {
            var challenge = (await (await http.SendAsync(CookieRequest(HttpMethod.Post, $"/ui/{slug}/passkeys/register/options", cookie)))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("publicKey").GetProperty("challenge").GetString()!;
            using var register = await http.SendAsync(CookieRequest(HttpMethod.Post, $"/ui/{slug}/passkeys/register", cookie, Json(new
            {
                id = authenticator.CredentialKey,
                nickname = "Audit probe",
                response = new
                {
                    clientDataJSON = Encodings.Base64UrlEncode(Encoding.UTF8.GetBytes(authenticator.ClientDataJson("webauthn.create", challenge))),
                    attestationObject = Encodings.Base64UrlEncode(authenticator.AttestationObject()),
                    transports = new[] { "internal" },
                },
            })));
            await Assert.That(register.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        // API key, webhook, organization, SCIM directory: each response reveals a
        // credential that must not be echoed into the audit log.
        using (var key = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slug}/api-keys", apiKey, Json(new { name = "probe" }))))
        {
            await Assert.That(key.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var body = await key.Content.ReadFromJsonAsync<JsonElement>();
            Forbid(body.GetProperty("api_key").GetString());
            Forbid((await store.GetApiKeyAsync(body.GetProperty("id").GetString()!))!.SecretKey);
        }
        using (var webhook = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slug}/webhooks", apiKey,
            Json(new { url = "https://sink.test/hook", events = new[] { "user.*" } }))))
        {
            await Assert.That(webhook.StatusCode).IsEqualTo(HttpStatusCode.OK);
            Forbid((await webhook.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("signing_secret").GetString());
        }
        using (var org = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slug}/organizations", apiKey, Json(new { name = "Audit Org" }))))
            await Assert.That(org.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using (var directory = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slug}/directories", apiKey, Json(new { }))))
        {
            await Assert.That(directory.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var body = await directory.Content.ReadFromJsonAsync<JsonElement>();
            Forbid(body.GetProperty("token").GetString());
            using var rotated = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slug}/directories/{body.GetProperty("id").GetString()}/rotate", apiKey, null));
            Forbid((await rotated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        }

        // Impersonation returns a live session token.
        using (var impersonated = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slug}/users/{user.Id}/impersonate", apiKey, null)))
        {
            await Assert.That(impersonated.StatusCode).IsEqualTo(HttpStatusCode.Created);
            Forbid((await impersonated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        }

        // OAuth grant: client secret, authorization code, access token, refresh token.
        string clientSecret;
        using (var client = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slug}/oauth-clients", apiKey,
            Json(new { name = "Probe App", redirect_uris = new[] { RedirectUri } }))))
        {
            await Assert.That(client.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await client.Content.ReadFromJsonAsync<JsonElement>();
            clientSecret = body.GetProperty("client_secret").GetString()!;
            Forbid(clientSecret);
            var clientId = body.GetProperty("client").GetProperty("clientId").GetString()!;

            var verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var challenge = Encodings.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            using var authorize = await http.SendAsync(CookieRequest(HttpMethod.Get,
                $"/{slug}/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
                + $"&scope=openid&state=st1&code_challenge={challenge}&code_challenge_method=S256", cookie));
            await Assert.That(authorize.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            var code = QueryValue(authorize.Headers.Location!.ToString(), "code");
            Forbid(code);

            using var token = await http.SendAsync(TokenRequest(slug, clientId, clientSecret, new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = verifier,
            }));
            var tokens = await token.Content.ReadFromJsonAsync<JsonElement>();
            Forbid(tokens.GetProperty("access_token").GetString());
            Forbid(tokens.GetProperty("refresh_token").GetString());

            using var refreshed = await http.SendAsync(TokenRequest(slug, clientId, clientSecret, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
            }));
            var cycled = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
            Forbid(cycled.GetProperty("access_token").GetString());
            Forbid(cycled.GetProperty("refresh_token").GetString());
        }

        // The probe: fetch the audit trail through the public API and grep it.
        var audit = await AuditJsonAsync(http, slug, apiKey);
        await Assert.That(audit).Contains("user.signed_up"); // non-vacuous: the trail really has events
        await Assert.That(audit).Contains("mfa.enrolled");
        await Assert.That(audit).Contains("passkey.created");
        await Assert.That(audit).Contains("api_key.created");
        await Assert.That(audit).Contains("webhook.created");
        await Assert.That(audit).Contains("organization.created");
        await Assert.That(audit).Contains("oauth.code_issued");
        foreach (var value in forbidden)
            await Assert.That(audit.Contains(value, StringComparison.Ordinal)).IsFalse();
    }

    // ---- actors -------------------------------------------------------------------

    [Test]
    public async Task ActorsIdentifyUserApiKeyOrDirectoryPerSurface()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, rootKey) = await service.CreateInstanceAsync("audit-actor-" + Guid.NewGuid().ToString("N")[..8]);
        var http = service.Http;

        // Management actions act as the API key, not as any user.
        var (keyId, apiKey) = await CreateApiKeyAsync(http, slug, rootKey);

        // Hosted-UI sign-in acts as the user.
        var password = "correct horse battery staple";
        var email = $"actor-{Guid.NewGuid().ToString("N")[..8]}@example.com";
        using (var signUp = await http.PostAsync($"/ui/{slug}/sign-up", Form(("email", email), ("password", password), ("name", "Actor Probe"))))
            await Assert.That(signUp.StatusCode).IsEqualTo(HttpStatusCode.Found);
        var user = (await service.Store.FindUserByEmailAsync(instanceId, email))!;

        // SCIM provisioning is a system surface: the directory is the actor, never a user.
        using var directory = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slug}/directories", apiKey, Json(new { })));
        var directoryBody = await directory.Content.ReadFromJsonAsync<JsonElement>();
        var directoryId = directoryBody.GetProperty("id").GetString()!;
        var scimToken = directoryBody.GetProperty("token").GetString()!;
        using var provision = await http.SendAsync(Api(HttpMethod.Post, $"/scim/{directoryId}/Users", scimToken, Json(new
        {
            userName = $"scim-{Guid.NewGuid().ToString("N")[..8]}@example.com",
            name = new { givenName = "Ada", familyName = "Lovelace" },
            externalId = "ext-" + Guid.NewGuid().ToString("N")[..8],
            active = true,
        })));
        await Assert.That(provision.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Impersonation: the API key acts, and the event records both sides.
        using (var impersonate = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slug}/users/{user.Id}/impersonate", apiKey, null)))
            await Assert.That(impersonate.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using (var deleted = await http.SendAsync(Api(HttpMethod.Delete, $"/v1/{slug}/users/{user.Id}", apiKey, null)))
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var events = (await service.Store.ListAuditEventsAsync(instanceId, 100)).ToList();
        var signedUp = events.Single(e => e.Type == "user.signed_up");
        await Assert.That(signedUp.ActorId).IsEqualTo(user.Id);
        var provisioned = events.Single(e => e.Type == "directory.user_provisioned");
        await Assert.That(provisioned.ActorId).IsEqualTo(directoryId);
        await Assert.That(provisioned.ActorId!.StartsWith("usr_")).IsFalse();
        var impersonated = events.Single(e => e.Type == "user.impersonated");
        await Assert.That(impersonated.ActorId).IsEqualTo(keyId);
        await Assert.That(impersonated.Data["user_id"].ToString()).IsEqualTo(user.Id);
        await Assert.That(impersonated.Data["api_key_id"].ToString()).IsEqualTo(keyId);
        var userDeleted = events.Single(e => e.Type == "user.deleted");
        await Assert.That(userDeleted.ActorId).IsEqualTo(keyId);
        await Assert.That(userDeleted.Data["user_id"].ToString()).IsEqualTo(user.Id);
    }

    // ---- event coverage -----------------------------------------------------------

    [Test]
    public async Task OrganizationAndWebhookLifecycleEmitsAuditEvents()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, rootKey) = await service.CreateInstanceAsync("audit-cover-" + Guid.NewGuid().ToString("N")[..8]);
        var http = service.Http;
        var (keyId, apiKey) = await CreateApiKeyAsync(http, slug, rootKey);

        string orgId;
        using (var org = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slug}/organizations", apiKey, Json(new { name = "Cov Org" }))))
        {
            await Assert.That(org.StatusCode).IsEqualTo(HttpStatusCode.OK);
            orgId = (await org.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }

        string endpointId;
        using (var webhook = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slug}/webhooks", apiKey,
            Json(new { url = "https://sink.test/hook", events = new[] { "*" } }))))
        {
            await Assert.That(webhook.StatusCode).IsEqualTo(HttpStatusCode.OK);
            endpointId = (await webhook.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }
        using (var deleted = await http.SendAsync(Api(HttpMethod.Delete, $"/v1/{slug}/webhooks/{endpointId}", apiKey, null)))
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var events = await service.Store.ListAuditEventsAsync(instanceId, 100);
        var created = events.Single(e => e.Type == "organization.created");
        await Assert.That(created.ActorId).IsEqualTo(keyId);
        await Assert.That(created.Data["organization_id"].ToString()).IsEqualTo(orgId);
        var webhookCreated = events.Single(e => e.Type == "webhook.created");
        await Assert.That(webhookCreated.ActorId).IsEqualTo(keyId);
        await Assert.That(webhookCreated.Data["endpoint_id"].ToString()).IsEqualTo(endpointId);
        var webhookDeleted = events.Single(e => e.Type == "webhook.deleted");
        await Assert.That(webhookDeleted.ActorId).IsEqualTo(keyId);
        await Assert.That(webhookDeleted.Data["endpoint_id"].ToString()).IsEqualTo(endpointId);
    }

    // ---- isolation ----------------------------------------------------------------

    [Test]
    public async Task AuditQueryApiOnlyReturnsTheCallingInstancesEvents()
    {
        var service = await AuthServiceFixture.GetAsync();
        var http = service.Http;
        var (_, slugA, keyA) = await service.CreateInstanceAsync("audit-iso-a-" + Guid.NewGuid().ToString("N")[..8]);
        var (_, slugB, keyB) = await service.CreateInstanceAsync("audit-iso-b-" + Guid.NewGuid().ToString("N")[..8]);

        var markerA = await CreateUserMarkerAsync(http, slugA, keyA);
        var markerB = await CreateUserMarkerAsync(http, slugB, keyB);

        var auditA = await AuditJsonAsync(http, slugA, keyA);
        var auditB = await AuditJsonAsync(http, slugB, keyB);
        await Assert.That(auditA.Contains(markerA, StringComparison.Ordinal)).IsTrue();
        await Assert.That(auditA.Contains(markerB, StringComparison.Ordinal)).IsFalse();
        await Assert.That(auditB.Contains(markerB, StringComparison.Ordinal)).IsTrue();
        await Assert.That(auditB.Contains(markerA, StringComparison.Ordinal)).IsFalse();
    }

    // ---- helpers ------------------------------------------------------------------

    private static async Task<(string KeyId, string ApiKey)> CreateApiKeyAsync(HttpClient http, string slug, string rootKey)
    {
        using var request = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slug}/api-keys", rootKey, Json(new { name = "actor-probe" })));
        await Assert.That(request.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var body = await request.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetString()!, body.GetProperty("api_key").GetString()!);
    }

    /// <summary>Creates a user through the management API and returns its id as the isolation marker.</summary>
    private static async Task<string> CreateUserMarkerAsync(HttpClient http, string slug, string apiKey)
    {
        using var created = await http.SendAsync(Api(HttpMethod.Post, $"/v1/{slug}/users", apiKey,
            Json(new { email = $"iso-{Guid.NewGuid().ToString("N")[..8]}@example.com", password = "correct horse battery staple" })));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> AuditJsonAsync(HttpClient http, string slug, string apiKey)
    {
        using var request = await http.SendAsync(Api(HttpMethod.Get, $"/v1/{slug}/audit-events?limit=100", apiKey, null));
        await Assert.That(request.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return await request.Content.ReadAsStringAsync();
    }

    private static HttpRequestMessage Api(HttpMethod method, string path, string bearer, JsonContent? content) => new(method, path)
    {
        Content = content,
        Headers = { Authorization = new AuthenticationHeaderValue("Bearer", bearer) },
    };

    private static HttpRequestMessage CookieRequest(HttpMethod method, string path, string cookie, HttpContent? content = null) => new(method, path)
    {
        Content = content,
        Headers = { { "Cookie", $"tsauth_session={cookie}" } },
    };

    private static HttpRequestMessage TokenRequest(string slug, string clientId, string clientSecret, Dictionary<string, string> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/{slug}/oauth/token") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
        return request;
    }

    private static JsonContent Json(object body) => JsonContent.Create(body);

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private static string? SessionCookie(HttpResponseMessage response)
    {
        var header = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith("tsauth_session=", StringComparison.Ordinal))
            : null;
        return header?["tsauth_session=".Length..].Split(';')[0];
    }

    private static string QueryValue(string url, string name)
    {
        foreach (var pair in url[(url.IndexOf('?') + 1)..].Split('&'))
            if (pair.Split('=', 2) is [var key, var value] && key == name)
                return Uri.UnescapeDataString(value);
        throw new InvalidOperationException($"{name} missing from {url}");
    }
}
