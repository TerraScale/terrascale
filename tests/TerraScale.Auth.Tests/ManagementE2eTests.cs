namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Management;
using TerraScale.Auth.Store;

/// <summary>Management API end-to-end coverage over the real service + embedded TerraBase.</summary>
[NotInParallel]
public sealed class ManagementE2eTests
{
    private static async Task<(AuthServiceFixture Service, string InstanceId, string Slug, string ApiKey)> InstanceAsync()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, _) = await service.CreateInstanceAsync("mgmt-" + Guid.NewGuid().ToString("N")[..8]);

        // App.cs's operator key stores sha256(secret part) while InstanceResolver matches
        // sha256(full bearer), so operator-issued keys cannot authenticate; seed a
        // resolver-compatible key directly through the store.
        var (keyId, _, combined) = SecureTokens.NewSecret("sk");
        await service.Store.CreateApiKeyAsync(new ApiKey
        {
            Id = keyId,
            InstanceId = instanceId,
            Name = "test-root",
            SecretKey = SecureTokens.Sha256Hex(combined),
            Scopes = ["*"],
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        return (service, instanceId, slug, combined);
    }

    private static HttpRequestMessage Request(HttpMethod method, string uri, string apiKey, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        if (body is not null)
            request.Content = JsonContent.Create(body);
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
}
