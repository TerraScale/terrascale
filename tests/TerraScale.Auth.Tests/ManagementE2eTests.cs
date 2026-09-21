namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Management;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>Management API end-to-end coverage over the real service + embedded TerraBase.</summary>
[NotInParallel]
public sealed class ManagementE2eTests
{
    private static async Task<(AuthServiceFixture Service, string InstanceId, string Slug, string ApiKey)> InstanceAsync()
    {
        var service = await AuthServiceFixture.GetAsync();
        // The operator endpoint's key hashes the full bearer, exactly what InstanceResolver matches.
        var (instanceId, slug, apiKey) = await service.CreateInstanceAsync("mgmt-" + Guid.NewGuid().ToString("N")[..8]);
        return (service, instanceId, slug, apiKey);
    }

    private static HttpRequestMessage Request(HttpMethod method, string uri, string apiKey, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    /// <summary>Sends a raw JSON body: JsonContent drops null-valued properties (Web
    /// defaults), so explicit-null PATCH probes need the literal bytes on the wire.</summary>
    private static HttpRequestMessage RawRequest(HttpMethod method, string uri, string apiKey, string jsonBody)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static async Task<JsonElement> SendAsync(HttpClient http, HttpRequestMessage request, HttpStatusCode expected)
    {
        using var response = await http.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(expected);
        if (expected == HttpStatusCode.NoContent)
            return default;
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<JsonElement> CreateUserAsync(HttpClient http, string slug, string apiKey, string email, string? username = null) =>
        SendAsync(http, Request(HttpMethod.Post, $"/v1/{slug}/users", apiKey, new { email, username, password = "passw0rd!", name = "Test User" }), HttpStatusCode.Created);

    [Test]
    public async Task EmailChangeInvalidatesPendingVerificationLinks()
    {
        var (service, instanceId, slug, key) = await InstanceAsync();
        var http = service.Http;

        var created = await CreateUserAsync(http, slug, key, "stale-link@example.com");
        var userId = created.GetProperty("id").GetString()!;
        var token = "e2e" + SecureTokens.RandomToken(32);
        await service.Store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = "vrf_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            UserId = userId,
            Purpose = "verify_email",
            TokenKey = SecureTokens.Sha256Hex(token),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        await SendAsync(http, Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new { email = "moved@example.com" }), HttpStatusCode.OK);

        // The link proved the OLD address; it must not verify the new one.
        using var verify = await http.GetAsync($"/ui/{slug}/verify-email?token={token}");
        await Assert.That(verify.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await service.Store.GetUserAsync(userId))!.EmailVerified).IsFalse();
    }

    [Test]
    public async Task MissingKeyIsUnauthorized()
    {
        var (service, _, slug, _) = await InstanceAsync();
        var response = await service.Http.GetAsync($"/v1/{slug}/users");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task KeyFromAnotherInstanceIsUnauthorized()
    {
        var (service, _, slug, _) = await InstanceAsync();
        var (_, _, otherSlug, otherKey) = await InstanceAsync();

        using var wrongInstance = await service.Http.SendAsync(Request(HttpMethod.Get, $"/v1/{slug}/users", otherKey));
        await Assert.That(wrongInstance.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        // The same key against its own instance proves it is valid and the 401 comes from the mismatch.
        using var rightInstance = await service.Http.SendAsync(Request(HttpMethod.Get, $"/v1/{otherSlug}/users", otherKey));
        await Assert.That(rightInstance.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task UserCrudRoundTrip()
    {
        var (service, instanceId, slug, key) = await InstanceAsync();
        var http = service.Http;

        var created = await CreateUserAsync(http, slug, key, "crud@example.com", "crud");
        var userId = created.GetProperty("id").GetString()!;
        await Assert.That(created.GetProperty("email").GetString()).IsEqualTo("crud@example.com");
        await Assert.That(created.GetProperty("email_verified").GetBoolean()).IsFalse();
        await Assert.That(created.GetProperty("banned").GetBoolean()).IsFalse();
        await Assert.That(created.GetProperty("created_at").GetInt64()).IsGreaterThan(0L);
        await Assert.That(created.GetProperty("private_metadata").ValueKind).IsEqualTo(JsonValueKind.Object);

        var detail = await SendAsync(http, Request(HttpMethod.Get, $"/v1/{slug}/users/{userId}", key), HttpStatusCode.OK);
        await Assert.That(detail.GetProperty("id").GetString()).IsEqualTo(userId);

        var patched = await SendAsync(http, Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new
        {
            name = "Renamed",
            public_metadata = new { role = "admin" },
            banned = true,
            ban_reason = "abuse",
        }), HttpStatusCode.OK);
        await Assert.That(patched.GetProperty("name").GetString()).IsEqualTo("Renamed");
        await Assert.That(patched.GetProperty("public_metadata").GetProperty("role").GetString()).IsEqualTo("admin");
        await Assert.That(patched.GetProperty("banned").GetBoolean()).IsTrue();
        await Assert.That(patched.GetProperty("ban_reason").GetString()).IsEqualTo("abuse");

        var unbanned = await SendAsync(http, Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new { banned = false }), HttpStatusCode.OK);
        await Assert.That(unbanned.GetProperty("banned").GetBoolean()).IsFalse();
        await Assert.That(unbanned.GetProperty("ban_reason").ValueKind).IsEqualTo(JsonValueKind.Null);

        // Email change resets verification and re-keys the email uniqueness index.
        var stored = await service.Store.GetUserAsync(userId);
        await service.Store.UpsertUserAsync(stored! with { EmailVerified = true });
        var reemailed = await SendAsync(http, Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new { email = "renamed@example.com" }), HttpStatusCode.OK);
        await Assert.That(reemailed.GetProperty("email").GetString()).IsEqualTo("renamed@example.com");
        await Assert.That(reemailed.GetProperty("email_verified").GetBoolean()).IsFalse();

        var other = await CreateUserAsync(http, slug, key, "other@example.com");
        var taken = await http.SendAsync(Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new { email = "other@example.com" }));
        await Assert.That(taken.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        var takenBody = await taken.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(takenBody.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("email_taken");

        var duplicate = await http.SendAsync(Request(HttpMethod.Post, $"/v1/{slug}/users", key, new { email = "other@example.com" }));
        await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("email_taken");

        await SendAsync(http, Request(HttpMethod.Delete, $"/v1/{slug}/users/{userId}", key), HttpStatusCode.NoContent);
        using var gone = await http.SendAsync(Request(HttpMethod.Get, $"/v1/{slug}/users/{userId}", key));
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // Soft delete frees the identifiers for reuse.
        await CreateUserAsync(http, slug, key, "crud@example.com", "crud-again");

        var audit = await service.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audit.Any(e => e.Type == "user.deleted"
            && e.Data.TryGetValue("user_id", out var id)
            && ((JsonElement)id).GetString() == userId)).IsTrue();
    }

    [Test]
    public async Task UserListPaginatesInIdOrder()
    {
        var (service, _, slug, key) = await InstanceAsync();
        var http = service.Http;

        for (var i = 0; i < 25; i++)
            await CreateUserAsync(http, slug, key, $"page-{i}@example.com");

        var first = (await SendAsync(http, Request(HttpMethod.Get, $"/v1/{slug}/users?limit=10", key), HttpStatusCode.OK)).GetProperty("data").EnumerateArray().ToList();
        await Assert.That(first.Count).IsEqualTo(10);
        var firstIds = first.Select(u => u.GetProperty("id").GetString()!).ToList();
        await Assert.That(firstIds.SequenceEqual(firstIds.Order(StringComparer.Ordinal))).IsTrue();
        await Assert.That(first[0].TryGetProperty("private_metadata", out _)).IsFalse();
        await Assert.That(first[0].GetProperty("public_metadata").ValueKind).IsEqualTo(JsonValueKind.Object);

        var startingAfter = firstIds[^1];
        var second = (await SendAsync(http, Request(HttpMethod.Get, $"/v1/{slug}/users?limit=10&starting_after={startingAfter}", key), HttpStatusCode.OK)).GetProperty("data").EnumerateArray().ToList();
        await Assert.That(second.Count).IsEqualTo(10);
        var secondIds = second.Select(u => u.GetProperty("id").GetString()!).ToList();
        await Assert.That(secondIds.All(id => string.CompareOrdinal(id, startingAfter) > 0)).IsTrue();
        await Assert.That(secondIds.Any(id => firstIds.Contains(id))).IsFalse();

        var tail = (await SendAsync(http, Request(HttpMethod.Get, $"/v1/{slug}/users?limit=100&starting_after={secondIds[^1]}", key), HttpStatusCode.OK)).GetProperty("data").EnumerateArray().ToList();
        await Assert.That(tail.Count).IsEqualTo(5);
    }

    [Test]
    public async Task ImpersonationTokenIsARealSession()
    {
        var (service, instanceId, slug, key) = await InstanceAsync();
        var user = await CreateUserAsync(service.Http, slug, key, "impersonated@example.com");
        var userId = user.GetProperty("id").GetString()!;

        var body = await SendAsync(service.Http, Request(HttpMethod.Post, $"/v1/{slug}/users/{userId}/impersonate", key), HttpStatusCode.Created);
        var token = body.GetProperty("token").GetString()!;
        var expiresAt = body.GetProperty("expires_at").GetInt64();
        await Assert.That(expiresAt).IsGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var session = await service.Store.FindSessionByTokenAsync(SecureTokens.Sha256Hex(token));
        await Assert.That(session).IsNotNull();
        await Assert.That(session!.UserId).IsEqualTo(userId);
        await Assert.That(session.InstanceId).IsEqualTo(instanceId);
        await Assert.That(session.Status).IsEqualTo("active");
        await Assert.That(session.ExpiresAt).IsEqualTo(expiresAt);

        var resolved = await new SessionService(service.Store).ResolveAsync(instanceId, token);
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.User.Id).IsEqualTo(userId);

        var audit = await service.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audit.Any(e => e.Type == "user.impersonated" && e.Data.TryGetValue("api_key_id", out _))).IsTrue();
    }

    [Test]
    public async Task SessionsCanBeListedAndRevoked()
    {
        var (service, _, slug, key) = await InstanceAsync();
        var http = service.Http;
        var user = await CreateUserAsync(http, slug, key, "sessions@example.com");
        var userId = user.GetProperty("id").GetString()!;

        var first = await SendAsync(http, Request(HttpMethod.Post, $"/v1/{slug}/users/{userId}/impersonate", key), HttpStatusCode.Created);
        var second = await SendAsync(http, Request(HttpMethod.Post, $"/v1/{slug}/users/{userId}/impersonate", key), HttpStatusCode.Created);
        var firstSessionId = (await service.Store.FindSessionByTokenAsync(SecureTokens.Sha256Hex(first.GetProperty("token").GetString()!)))!.Id;
        var secondSessionId = (await service.Store.FindSessionByTokenAsync(SecureTokens.Sha256Hex(second.GetProperty("token").GetString()!)))!.Id;

        var list = (await SendAsync(http, Request(HttpMethod.Get, $"/v1/{slug}/users/{userId}/sessions", key), HttpStatusCode.OK)).GetProperty("data").EnumerateArray().ToList();
        await Assert.That(list.Count).IsEqualTo(2);
        var listedIds = list.Select(s => s.GetProperty("id").GetString()).ToHashSet();
        await Assert.That(listedIds.Contains(firstSessionId)).IsTrue();
        await Assert.That(listedIds.Contains(secondSessionId)).IsTrue();

        await SendAsync(http, Request(HttpMethod.Post, $"/v1/{slug}/sessions/{firstSessionId}/revoke", key), HttpStatusCode.NoContent);
        await Assert.That((await service.Store.GetSessionAsync(firstSessionId))!.Status).IsEqualTo("revoked");
        await Assert.That((await service.Store.GetSessionAsync(secondSessionId))!.Status).IsEqualTo("active");

        await SendAsync(http, Request(HttpMethod.Post, $"/v1/{slug}/users/{userId}/sessions/revoke-all", key), HttpStatusCode.NoContent);
        await Assert.That((await service.Store.GetSessionAsync(secondSessionId))!.Status).IsEqualTo("revoked");
    }

    [Test]
    public async Task ApiKeyLifecycleAuthorizesAndRevokes()
    {
        var (service, _, slug, key) = await InstanceAsync();
        var http = service.Http;

        var created = await SendAsync(http, Request(HttpMethod.Post, $"/v1/{slug}/api-keys", key, new { name = "ci" }), HttpStatusCode.Created);
        var keyId = created.GetProperty("id").GetString()!;
        var secret = created.GetProperty("api_key").GetString()!;
        await Assert.That(secret.Split('_').Length).IsEqualTo(3);
        await Assert.That(secret.Split('_')[0]).IsEqualTo("sk");

        // The returned secret must authorize immediately.
        using var immediateUse = await http.SendAsync(Request(HttpMethod.Get, $"/v1/{slug}/users", secret));
        await Assert.That(immediateUse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var list = (await SendAsync(http, Request(HttpMethod.Get, $"/v1/{slug}/api-keys", key), HttpStatusCode.OK)).GetProperty("data").EnumerateArray().ToList();
        var listed = list.Single(k => k.GetProperty("id").GetString() == keyId);
        await Assert.That(listed.TryGetProperty("api_key", out _)).IsFalse();
        await Assert.That(listed.GetProperty("last_used_at").ValueKind).IsEqualTo(JsonValueKind.Null);

        await SendAsync(http, Request(HttpMethod.Delete, $"/v1/{slug}/api-keys/{keyId}", key), HttpStatusCode.NoContent);
        using var revoked = await http.SendAsync(Request(HttpMethod.Get, $"/v1/{slug}/users", secret));
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task ApiKeyCreateStoresOnlyTheHash()
    {
        var (service, instanceId, slug, key) = await InstanceAsync();

        var created = await SendAsync(service.Http, Request(HttpMethod.Post, $"/v1/{slug}/api-keys", key, new { name = "at-rest" }), HttpStatusCode.Created);
        var secret = created.GetProperty("api_key").GetString()!;
        var keyId = created.GetProperty("id").GetString()!;

        // The stored document must hold the sha256 of the full bearer, never the secret.
        var stored = (await service.Store.ListApiKeysAsync(instanceId)).Single(k => k.Id == keyId);
        await Assert.That(stored.SecretKey).IsEqualTo(SecureTokens.Sha256Hex(secret));
        await Assert.That(stored.SecretKey).IsNotEqualTo(secret);

        // Audit trails for the create carry the key id only.
        var audited = await service.Store.ListAuditEventsAsync(instanceId, 100);
        var created1 = audited.First(e => e.Type == "api_key.created"
            && e.Data.TryGetValue("api_key_id", out var id) && ((JsonElement)id).GetString() == keyId);
        await Assert.That(JsonSerializer.Serialize(created1.Data).Contains(secret)).IsFalse();
    }

    [Test]
    public async Task OperatorIssuedKeyAuthenticatesAndRevokesImmediately()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, key) = await service.CreateInstanceAsync("opkey-" + Guid.NewGuid().ToString("N")[..8]);

        // The operator bootstrap key must be resolver-compatible: it authorizes the management API.
        using var works = await service.Http.SendAsync(Request(HttpMethod.Get, $"/v1/{slug}/users", key));
        await Assert.That(works.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Revoking it via the API must 401 the very next request (no caching in the resolver).
        var keyId = (await service.Store.ListApiKeysAsync(instanceId)).Single(k => !k.Revoked).Id;
        await SendAsync(service.Http, Request(HttpMethod.Delete, $"/v1/{slug}/api-keys/{keyId}", key), HttpStatusCode.NoContent);
        using var revoked = await service.Http.SendAsync(Request(HttpMethod.Get, $"/v1/{slug}/users", key));
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task CrossInstanceKeyIsUnauthorizedOnEveryRouteGroup()
    {
        var (service, _, slug, key) = await InstanceAsync();
        var (_, _, otherSlug, otherKey) = await InstanceAsync();
        var http = service.Http;

        // A valid key for instance A probing EVERY route group under instance B's slug: uniform 401,
        // never 403/404 — those would confirm the key is valid and only the instance is wrong.
        var probes = new (HttpMethod Method, string Uri)[]
        {
            new(HttpMethod.Get, $"/v1/{otherSlug}/users"),
            new(HttpMethod.Get, $"/v1/{otherSlug}/api-keys"),
            new(HttpMethod.Post, $"/v1/{otherSlug}/api-keys"),
            new(HttpMethod.Patch, $"/v1/{otherSlug}/instance"),
            new(HttpMethod.Get, $"/v1/{otherSlug}/billing"),
            new(HttpMethod.Post, $"/v1/{otherSlug}/connections"),
            new(HttpMethod.Post, $"/v1/{otherSlug}/directories"),
            new(HttpMethod.Post, $"/v1/{otherSlug}/organizations"),
            new(HttpMethod.Get, $"/v1/{otherSlug}/jwt-templates"),
            new(HttpMethod.Post, $"/v1/{otherSlug}/organizations/org_x/domains"),
            new(HttpMethod.Post, $"/v1/{otherSlug}/domains/example.com/enroll"),
            new(HttpMethod.Post, $"/v1/{otherSlug}/webhooks"),
            new(HttpMethod.Get, $"/v1/{otherSlug}/audit-events"),
            new(HttpMethod.Post, $"/v1/{otherSlug}/oauth-clients"),
            new(HttpMethod.Get, $"/v1/{otherSlug}/users/usr_x/mfa-factors"),
            new(HttpMethod.Get, $"/v1/{otherSlug}/users/usr_x/passkeys"),
        };
        foreach (var (method, uri) in probes)
        {
            using var response = await http.SendAsync(Request(method, uri, key));
            await Assert.That(response.StatusCode)
                .IsEqualTo(HttpStatusCode.Unauthorized)
                .Because($"{method} {uri} must reject a foreign-instance key with 401");
        }

        // The ?key= billing page follows the same rule.
        using var ui = await http.GetAsync($"/ui/{otherSlug}/billing?key={key}");
        await Assert.That(ui.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        // Control: the same key against its own slug still works, and the other key vice versa.
        using var own = await http.SendAsync(Request(HttpMethod.Get, $"/v1/{slug}/users", key));
        await Assert.That(own.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var otherOwn = await http.SendAsync(Request(HttpMethod.Get, $"/v1/{otherSlug}/users", otherKey));
        await Assert.That(otherOwn.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task SlugAliasesDoNotBypassTheInstanceGate()
    {
        var (service, _, slug, key) = await InstanceAsync();

        // Case tricks, URL-encoding of the slug, and trailing whitespace must all miss the
        // key's own slug and 401 — they must never resolve a different (or the same) instance.
        var probes = new[]
        {
            $"/v1/{slug.ToUpperInvariant()}/users",
            $"/v1/{slug.ToUpperInvariant()}/api-keys",
            $"/v1/%73{slug[1..]}/users",
            $"/v1/{slug}%20/users",
            $"/v1/{slug}-/users",
        };
        foreach (var uri in probes)
        {
            using var response = await service.Http.SendAsync(Request(HttpMethod.Get, uri, key));
            await Assert.That(response.StatusCode)
                .IsEqualTo(HttpStatusCode.Unauthorized)
                .Because($"alias form {uri} must not satisfy the gate");
        }
    }

    [Test]
    public async Task MalformedBearersAreUnauthorizedNeverServerError()
    {
        var (service, _, slug, key) = await InstanceAsync();

        // Send the non-ASCII probes over the real socket too. The default
        // HttpClient ASCII encoder rejects them before the service can validate them.
        using var headerClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            RequestHeaderEncodingSelector = (name, _) =>
                name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    ? System.Text.Encoding.UTF8
                    : null,
        }) { BaseAddress = service.Origin };

        // Every malformed Authorization shape must resolve to nothing — 401, never a 500.
        var probes = new[]
        {
            "Bearer",                            // scheme only, no space or secret
            "Bearer ",                           // empty secret
            "Bearer sk_",                        // prefix with no secret material
            "Bearer " + key.ToUpperInvariant(),  // sk_ keys are case-sensitive
            "Bearer " + key + " extra",          // trailing junk is part of the token, not ignored
            "Bearer " + key.Insert(10, " "),     // whitespace inside the secret
            "Bearer " + key.Insert(10, "\u00A0"),  // non-breaking space inside the secret
            "Bearer \u00AD" + key,             // soft-hyphen smearing the sk_ prefix (culture-sensitive StartsWith trap)
            "Basic " + key,                      // wrong scheme
            "Session " + key,                    // unknown scheme
            "Bearer " + new string('x', 10_000), // extreme length must hash and 401, nothing else
            "Bearer sk_" + new string('x', 10_000),
        };
        foreach (var header in probes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/{slug}/users");
            request.Headers.TryAddWithoutValidation("Authorization", header);
            using var response = await headerClient.SendAsync(request);
            var preview = header.Length <= 24 ? header : header[..24] + "…";
            await Assert.That(response.StatusCode)
                .IsEqualTo(HttpStatusCode.Unauthorized)
                .Because($"Authorization '{preview}' must be rejected with 401");
        }

        // Lenient-by-design: whitespace padding around the secret trims away and still
        // resolves (curl/client quirks); the trimmed hash must match a real key either way.
        foreach (var header in new[] { "Bearer  " + key, "Bearer " + key + "  " })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/{slug}/users");
            request.Headers.TryAddWithoutValidation("Authorization", header);
            using var response = await service.Http.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        // The scheme itself is case-insensitive per RFC 7235.
        using var lowercase = new HttpRequestMessage(HttpMethod.Get, $"/v1/{slug}/users");
        lowercase.Headers.TryAddWithoutValidation("Authorization", "bearer " + key);
        using var lowercaseResponse = await service.Http.SendAsync(lowercase);
        await Assert.That(lowercaseResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task OperatorKeyAndInstanceKeysInhabitSeparateRealms()
    {
        var (service, _, slug, key) = await InstanceAsync();

        // The operator bearer must never satisfy an instance route.
        using var operatorOnInstance = new HttpRequestMessage(HttpMethod.Get, $"/v1/{slug}/users");
        operatorOnInstance.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "operator-test-key");
        using var operatorResponse = await service.Http.SendAsync(operatorOnInstance);
        await Assert.That(operatorResponse.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        // And an instance sk_ key must never satisfy the operator route.
        using var keyOnOperator = new HttpRequestMessage(HttpMethod.Post, "/v1/instances")
        {
            Content = JsonContent.Create(new { slug = "nope-" + Guid.NewGuid().ToString("N")[..8], name = "nope" }),
        };
        keyOnOperator.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var keyResponse = await service.Http.SendAsync(keyOnOperator);
        await Assert.That(keyResponse.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task EngineOutageMidResolutionAnswersCleanEnvelopeNotAnUnhandledException()
    {
        // Private fixture: its wire to the engine gets severed while the auth app
        // keeps serving, so every store read — key lookup, instance fetch — fails
        // mid-resolution exactly like a network outage to the engine.
        var service = await AuthServiceFixture.StartAsync();
        try
        {
            var (_, slug, key) = await service.CreateInstanceAsync("engfail-" + Guid.NewGuid().ToString("N")[..8]);
            service.BreakEngineConnection();

            using var response = await service.Http.SendAsync(Request(HttpMethod.Get, $"/v1/{slug}/users", key));
            await Assert.That((int)response.StatusCode).IsEqualTo(503);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("temporarily_unavailable");

            // No-header requests stay a clean 401 — no store read is attempted at all.
            using var anonymous = await service.Http.GetAsync($"/v1/{slug}/users");
            await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    [Test]
    public async Task UserListSurvivesAFullEnginePageOfForeignUsers()
    {
        var (service, instanceId, slug, key) = await InstanceAsync();
        var store = service.Store;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        User Seed(string id, string instance) => new()
        {
            Id = id,
            InstanceId = instance,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // One target user, then a full raw engine page (1000 documents) with zero of
        // this instance's users, then another target user whose id sorts after the block.
        await store.CreateUserAsync(Seed("usr_probe0", instanceId));
        foreach (var batch in Enumerable.Range(1, 1000).Chunk(50))
            await Task.WhenAll(batch.Select(i => store.CreateUserAsync(Seed($"usr_probef{i:0000}", "inst_probe_foreign"))));
        await store.CreateUserAsync(Seed("usr_probezz", instanceId));

        var ids = (await SendAsync(service.Http, Request(HttpMethod.Get, $"/v1/{slug}/users?limit=100", key), HttpStatusCode.OK))
            .GetProperty("data").EnumerateArray().Select(u => u.GetProperty("id").GetString()!).ToList();
        await Assert.That(ids).Contains("usr_probe0");
        await Assert.That(ids).Contains("usr_probezz");
    }

    [Test]
    public async Task UserListWalkIsCompleteAndUnknownCursorStillPages()
    {
        var (service, _, slug, key) = await InstanceAsync();
        var http = service.Http;
        for (var i = 0; i < 9; i++)
            await CreateUserAsync(http, slug, key, $"walk-{i}@example.com");

        // Walk every page: no skips, no duplicates, ids strictly ascending.
        var seen = new List<string>();
        string? after = null;
        while (true)
        {
            var uri = $"/v1/{slug}/users?limit=3" + (after is null ? "" : $"&starting_after={after}");
            var page = (await SendAsync(http, Request(HttpMethod.Get, uri, key), HttpStatusCode.OK))
                .GetProperty("data").EnumerateArray().Select(u => u.GetProperty("id").GetString()!).ToList();
            if (page.Count == 0)
                break;
            seen.AddRange(page);
            after = page[^1];
        }
        await Assert.That(seen.Count).IsEqualTo(9);
        await Assert.That(seen.Distinct().Count()).IsEqualTo(9);
        await Assert.That(seen.SequenceEqual(seen.Order(StringComparer.Ordinal))).IsTrue();

        // starting_after an id that never existed still pages from the top.
        var unknown = (await SendAsync(http, Request(HttpMethod.Get, $"/v1/{slug}/users?limit=3&starting_after=usr_", key), HttpStatusCode.OK))
            .GetProperty("data").EnumerateArray().Select(u => u.GetProperty("id").GetString()!).ToList();
        await Assert.That(unknown.Count).IsEqualTo(3);
        await Assert.That(unknown[0]).IsEqualTo(seen[0]);
    }

    [Test]
    public async Task PatchDistinguishesAbsentFromExplicitNull()
    {
        var (service, _, slug, key) = await InstanceAsync();
        var http = service.Http;

        var created = await CreateUserAsync(http, slug, key, "tri@example.com", "tri");
        var userId = created.GetProperty("id").GetString()!;

        // Absent properties leave every field alone.
        var untouched = await SendAsync(http, Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new { }), HttpStatusCode.OK);
        await Assert.That(untouched.GetProperty("name").GetString()).IsEqualTo("Test User");
        await Assert.That(untouched.GetProperty("username").GetString()).IsEqualTo("tri");

        // Explicit null unsets — and the released username is reusable by another account.
        var unnamed = await SendAsync(http, RawRequest(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, """{"name":null,"username":null}"""), HttpStatusCode.OK);
        await Assert.That(unnamed.GetProperty("name").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(unnamed.GetProperty("username").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That((await service.Store.GetUserAsync(userId))!.UsernameKey).IsNull();
        await CreateUserAsync(http, slug, key, "tri-reuse@example.com", "tri");

        // A value sets (and re-keys) the username.
        var renamed = await SendAsync(http, Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new { username = "renamed" }), HttpStatusCode.OK);
        await Assert.That(renamed.GetProperty("username").GetString()).IsEqualTo("renamed");

        // A taken username conflicts without clobbering the current one.
        await CreateUserAsync(http, slug, key, "tri-taken@example.com", "claimer");
        using var taken = await http.SendAsync(Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new { username = "claimer" }));
        await Assert.That(taken.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await service.Store.GetUserAsync(userId))!.Username).IsEqualTo("renamed");

        // Metadata: absent keeps, a value replaces wholesale, explicit null clears.
        await SendAsync(http, Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new { public_metadata = new { a = 1, b = 2 } }), HttpStatusCode.OK);
        var kept = await SendAsync(http, Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new { }), HttpStatusCode.OK);
        await Assert.That(kept.GetProperty("public_metadata").EnumerateObject().Count()).IsEqualTo(2);
        var cleared = await SendAsync(http, RawRequest(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, """{"public_metadata":null}"""), HttpStatusCode.OK);
        await Assert.That(cleared.GetProperty("public_metadata").EnumerateObject().Count()).IsEqualTo(0);

        // Email cannot be removed, only changed.
        using var nullEmail = await http.SendAsync(RawRequest(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, """{"email":null}"""));
        await Assert.That(nullEmail.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task PatchPasswordHashesAndExplicitNullGoesPasswordless()
    {
        var (service, _, slug, key) = await InstanceAsync();
        var http = service.Http;
        var created = await CreateUserAsync(http, slug, key, "pwpatch@example.com");
        var userId = created.GetProperty("id").GetString()!;

        using var tooShort = await http.SendAsync(Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new { password = "short" }));
        await Assert.That(tooShort.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        await SendAsync(http, Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new { password = "newpassword1" }), HttpStatusCode.OK);
        var user = await service.Store.GetUserAsync(userId);
        await Assert.That(SessionService.VerifyPassword(user!, "newpassword1")).IsTrue();
        await Assert.That(SessionService.VerifyPassword(user!, "passw0rd!")).IsFalse();

        using var signIn = await http.PostAsync($"/ui/{slug}/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = "pwpatch@example.com",
            ["password"] = "newpassword1",
        }));
        await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.Found);

        // Explicit null removes the credential entirely.
        await SendAsync(http, RawRequest(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, """{"password":null}"""), HttpStatusCode.OK);
        using var noPassword = await http.PostAsync($"/ui/{slug}/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = "pwpatch@example.com",
            ["password"] = "newpassword1",
        }));
        await Assert.That(noPassword.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await noPassword.Content.ReadAsStringAsync()).Contains("Invalid email or password")).IsTrue();
    }

    [Test]
    public async Task PatchEmailConflictLeavesUserUnchanged()
    {
        var (service, _, slug, key) = await InstanceAsync();
        var http = service.Http;

        var user = await CreateUserAsync(http, slug, key, "keepme@example.com");
        var userId = user.GetProperty("id").GetString()!;
        await CreateUserAsync(http, slug, key, "held@example.com");
        var stored = await service.Store.GetUserAsync(userId);
        await service.Store.UpsertUserAsync(stored! with { EmailVerified = true });

        using var conflict = await http.SendAsync(Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new { email = "held@example.com" }));
        await Assert.That(conflict.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        var after = (await service.Store.GetUserAsync(userId))!;
        await Assert.That(after.Email).IsEqualTo("keepme@example.com");
        await Assert.That(after.EmailKey).IsEqualTo(stored!.EmailKey);
        await Assert.That(after.EmailVerified).IsTrue();
    }

    [Test]
    public async Task PatchBanTakesEffectImmediatelyOnSessionsAndImpersonation()
    {
        var (service, _, slug, key) = await InstanceAsync();
        var http = service.Http;
        var user = await CreateUserAsync(http, slug, key, "banpatch@example.com");
        var userId = user.GetProperty("id").GetString()!;

        var impersonated = await SendAsync(http, Request(HttpMethod.Post, $"/v1/{slug}/users/{userId}/impersonate", key), HttpStatusCode.Created);
        var cookie = "tsauth_session=" + impersonated.GetProperty("token").GetString()!;

        using var before = await http.SendAsync(WithCookie(HttpMethod.Get, $"/ui/{slug}/account", cookie));
        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await SendAsync(http, Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new { banned = true, ban_reason = "abuse" }), HttpStatusCode.OK);

        using var banned = await http.SendAsync(WithCookie(HttpMethod.Get, $"/ui/{slug}/account", cookie));
        await Assert.That(banned.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(banned.Headers.Location?.ToString()).Contains("sign-in");
        using var bannedImpersonation = await http.SendAsync(Request(HttpMethod.Post, $"/v1/{slug}/users/{userId}/impersonate", key));
        await Assert.That(bannedImpersonation.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        // Unban restores the still-active session.
        await SendAsync(http, Request(HttpMethod.Patch, $"/v1/{slug}/users/{userId}", key, new { banned = false }), HttpStatusCode.OK);
        using var restored = await http.SendAsync(WithCookie(HttpMethod.Get, $"/ui/{slug}/account", cookie));
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static HttpRequestMessage WithCookie(HttpMethod method, string uri, string cookie)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    [Test]
    public async Task DeleteUserKillsSessionsSignInAndReleasesEveryClaim()
    {
        var (service, instanceId, slug, key) = await InstanceAsync();
        var http = service.Http;

        var created = await CreateUserAsync(http, slug, key, "gone@example.com", "gone");
        var userId = created.GetProperty("id").GetString()!;

        using var signIn = await http.PostAsync($"/ui/{slug}/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = "gone@example.com",
            ["password"] = "passw0rd!",
        }));
        await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.Found);
        var cookie = (signIn.Headers.GetValues("Set-Cookie").First().Split(';'))[0];

        // The user holds a passkey credential claim too (a separate document).
        await service.Store.CreatePasskeyAsync(new Passkey
        {
            Id = "pky_probe",
            InstanceId = instanceId,
            UserId = userId,
            CredentialKey = "probe-credential-key",
            PublicKeyCborBase64 = "x",
            Algorithm = "ES256",
            SignCount = 0,
            Nickname = "probe",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await service.Store.CreateRefreshTokenAsync(new RefreshToken
        {
            Id = "rtk_purge_probe",
            InstanceId = instanceId,
            ClientId = "client-purge",
            UserId = userId,
            TokenKey = "refresh-purge-key",
            Status = "active",
            Scopes = ["openid"],
            CreatedAt = now,
            ExpiresAt = now + 3600,
        });
        await service.Store.CreateAuthorizationCodeAsync(new AuthorizationCode
        {
            Id = "code_purge_probe",
            InstanceId = instanceId,
            ClientId = "client-purge",
            UserId = userId,
            CodeKey = "authorization-purge-key",
            RedirectUri = "https://example.test/callback",
            Scopes = ["openid"],
            ExpiresAt = now + 600,
        });
        await service.Store.CreateMfaFactorAsync(new MfaFactor
        {
            Id = "mfa_purge_probe",
            InstanceId = instanceId,
            UserId = userId,
            Type = "recovery",
            RecoveryCodes = ["hash"],
            CreatedAt = now,
        });
        await service.Store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = "verify_purge_probe",
            InstanceId = instanceId,
            UserId = userId,
            Purpose = "reset_password",
            TokenKey = "verification-purge-key",
            ExpiresAt = now + 600,
            CreatedAt = now,
        });

        await SendAsync(http, Request(HttpMethod.Delete, $"/v1/{slug}/users/{userId}", key), HttpStatusCode.NoContent);

        await Assert.That(await service.Store.ListSessionsByUserAsync(userId)).IsEmpty();
        await Assert.That(await service.Store.GetRefreshTokenAsync("rtk_purge_probe")).IsNull();
        await Assert.That(await service.Store.GetAuthorizationCodeAsync("code_purge_probe")).IsNull();
        await Assert.That(await service.Store.GetMfaFactorAsync("mfa_purge_probe")).IsNull();
        await Assert.That(await service.Store.GetVerificationTokenAsync("verify_purge_probe")).IsNull();

        using var secondDelete = await http.SendAsync(Request(HttpMethod.Delete, $"/v1/{slug}/users/{userId}", key));
        await Assert.That(secondDelete.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var getGone = await http.SendAsync(Request(HttpMethod.Get, $"/v1/{slug}/users/{userId}", key));
        await Assert.That(getGone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var impersonateGone = await http.SendAsync(Request(HttpMethod.Post, $"/v1/{slug}/users/{userId}/impersonate", key));
        await Assert.That(impersonateGone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var listed = (await SendAsync(http, Request(HttpMethod.Get, $"/v1/{slug}/users?limit=100", key), HttpStatusCode.OK))
            .GetProperty("data").EnumerateArray().Select(u => u.GetProperty("id").GetString()!).ToList();
        await Assert.That(listed.Contains(userId)).IsFalse();

        using var deadSession = await http.SendAsync(WithCookie(HttpMethod.Get, $"/ui/{slug}/account", cookie));
        await Assert.That(deadSession.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(deadSession.Headers.Location?.ToString()).Contains("sign-in");

        using var deadSignIn = await http.PostAsync($"/ui/{slug}/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = "gone@example.com",
            ["password"] = "passw0rd!",
        }));
        await Assert.That((await deadSignIn.Content.ReadAsStringAsync()).Contains("Invalid email or password")).IsTrue();

        // Every uniqueness claim the user held is released: email, username, passkey credential.
        await CreateUserAsync(http, slug, key, "gone@example.com", "someone-else");
        await service.Store.CreatePasskeyAsync(new Passkey
        {
            Id = "pky_probe2",
            InstanceId = instanceId,
            UserId = "usr_whatever",
            CredentialKey = "probe-credential-key",
            PublicKeyCborBase64 = "x",
            Algorithm = "ES256",
            SignCount = 0,
            Nickname = "probe",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
    }

    [Test]
    public async Task ApiKeyScopesLimitTheKeyToItsRouteFamily()
    {
        var (service, _, slug, key) = await InstanceAsync();
        var http = service.Http;

        var created = await SendAsync(http, Request(HttpMethod.Post, $"/v1/{slug}/api-keys", key,
            new { name = "scoped", scopes = new[] { "users" } }), HttpStatusCode.Created);
        var secret = created.GetProperty("api_key").GetString()!;
        await Assert.That(created.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).Single()).IsEqualTo("users");

        // In-family routes answer normally, including nested ones.
        await SendAsync(http, Request(HttpMethod.Get, $"/v1/{slug}/users", secret), HttpStatusCode.OK);
        var user = await CreateUserAsync(http, slug, secret, $"scoped-{Guid.NewGuid().ToString("N")[..6]}@example.com");
        await SendAsync(http, Request(HttpMethod.Get, $"/v1/{slug}/users/{user.GetProperty("id").GetString()}", secret), HttpStatusCode.OK);

        // Every other family answers 403 insufficient_scope — enforced by the uniform
        // gate, so it holds for modules outside ManagementSupport too.
        foreach (var uri in new[]
                 {
                     $"/v1/{slug}/api-keys", $"/v1/{slug}/organizations", $"/v1/{slug}/webhooks",
                     $"/v1/{slug}/connections", $"/v1/{slug}/jwt-templates", $"/v1/{slug}/directories",
                     $"/v1/{slug}/audit-events", $"/v1/{slug}/billing", $"/v1/{slug}/instance",
                 })
        {
            using var denied = await http.SendAsync(Request(HttpMethod.Get, uri, secret));
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            var body = await denied.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("insufficient_scope");
        }

        // The unrestricted key the scoped one was created with still reaches everything.
        await SendAsync(http, Request(HttpMethod.Get, $"/v1/{slug}/organizations", key), HttpStatusCode.OK);
    }

    [Test]
    public async Task RateLimitAppliesUniformlyAcrossManagementModules()
    {
        var (service, _, slug, key) = await InstanceAsync();
        var http = service.Http;

        // Exhaust the per-key window through instance settings — a module outside
        // ManagementSupport. Loop instead of assuming the default 1000 limit.
        var exhausted = false;
        for (var i = 0; i < 2000 && !exhausted; i++)
        {
            using var response = await http.SendAsync(Request(HttpMethod.Get, $"/v1/{slug}/instance", key));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                exhausted = true;
            else
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        await Assert.That(exhausted).IsTrue();

        // The bucket is per key, not per module: every management surface must 429.
        foreach (var uri in new[]
                 {
                     $"/v1/{slug}/instance", $"/v1/{slug}/users", $"/v1/{slug}/webhooks",
                     $"/v1/{slug}/organizations", $"/v1/{slug}/connections", $"/v1/{slug}/jwt-templates",
                     $"/v1/{slug}/directories", $"/v1/{slug}/api-keys", $"/v1/{slug}/audit-events", $"/v1/{slug}/billing",
                 })
        {
            using var denied = await http.SendAsync(Request(HttpMethod.Get, uri, key));
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        }

        // The 429 envelope: uniform error body, Retry-After inside the window, no remaining quota.
        using var probe = await http.SendAsync(Request(HttpMethod.Get, $"/v1/{slug}/instance", key));
        await Assert.That(probe.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(probe.Headers.TryGetValues("Retry-After", out var retryAfterValues)).IsTrue();
        var retryAfter = int.Parse(retryAfterValues!.Single(), System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(retryAfter is >= 1 and <= 300).IsTrue();
        await Assert.That(probe.Headers.GetValues("X-RateLimit-Remaining").Single()).IsEqualTo("0");
        var body = await probe.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("rate_limited");
    }

    [Test]
    public async Task RateLimiterAllowsUpToLimitAndResetsOnWindowRollover()
    {
        var limiter = new RateLimiter(300);
        const long windowStart = 33 * 300; // 9900: a fixed 5-minute window boundary

        await Assert.That(limiter.Allow("key", 3, windowStart, out var first)).IsTrue();
        await Assert.That(first).IsEqualTo(2);
        await Assert.That(limiter.Allow("key", 3, windowStart + 1, out var second)).IsTrue();
        await Assert.That(second).IsEqualTo(1);
        await Assert.That(limiter.Allow("key", 3, windowStart + 2, out var third)).IsTrue();
        await Assert.That(third).IsEqualTo(0);

        await Assert.That(limiter.Allow("key", 3, windowStart + 3, out var exceeded)).IsFalse();
        await Assert.That(exceeded).IsEqualTo(0);
        await Assert.That(limiter.RetryAfterSeconds(windowStart + 3)).IsEqualTo(297);

        // Other keys have their own counters.
        await Assert.That(limiter.Allow("other", 3, windowStart + 3, out _)).IsTrue();

        // The next window resets the counter.
        await Assert.That(limiter.Allow("key", 3, windowStart + 300, out var reset)).IsTrue();
        await Assert.That(reset).IsEqualTo(2);
    }

    [Test]
    public async Task RateLimiterCountsConcurrentBurstsAtomically()
    {
        var limiter = new RateLimiter(300);
        const int limit = 50;
        var now = 7 * 300L;

        var results = await Task.WhenAll(Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => limiter.Allow("burst", limit, now, out _))));

        // A parallel burst admits exactly `limit` requests — a check-then-increment race would let more through.
        await Assert.That(results.Count(admitted => admitted)).IsEqualTo(limit);
    }

    [Test]
    public async Task RateLimiterRetryAfterNeverZeroOrNegativeAtBoundary()
    {
        var limiter = new RateLimiter(300);

        await Assert.That(limiter.RetryAfterSeconds(0)).IsEqualTo(300); // epoch tick: fresh window
        await Assert.That(limiter.RetryAfterSeconds(299)).IsEqualTo(1); // last second of the first window
        await Assert.That(limiter.RetryAfterSeconds(300)).IsEqualTo(300); // exact rollover: full window again
        await Assert.That(limiter.RetryAfterSeconds(600 + 150)).IsEqualTo(150);
    }

    [Test]
    public async Task RateLimiterEvictsStaleWindows()
    {
        var limiter = new RateLimiter(300);
        limiter.Allow("old", 10, 0, out _);
        limiter.Allow("stale", 10, 1 * 300, out _);
        limiter.Allow("current", 10, 3 * 300, out _);

        // Buckets older than current window - 2 are pruned; the two most recent windows stay.
        await Assert.That(limiter.BucketCount).IsEqualTo(2);
    }
}
