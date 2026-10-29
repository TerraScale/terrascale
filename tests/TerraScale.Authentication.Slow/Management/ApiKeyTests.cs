using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Slow.Management;

/// <summary>
/// API keys and the token grants of the platform contract (TS-598): an API key's client credentials and a user's token
/// exchange, each for an organization or project token, over real HTTP on the real TerraBase.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class ApiKeyTests(TerraBaseFixture fixture)
{
    private const string Audience = "https://api.terrascale.tech";
    private static readonly string[] ReadOnly = ["documents:read"];

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_key_revoked_or_expired_after_client_authentication_cannot_become_a_service_client(bool expires)
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        Func<Task>? invalidate = null;
        string? clientId = null;
        var invalidated = false;
        await using var oidc = await OidcHarness.StartAsync(fixture, values => values["access-token-audiences"] = Audience,
            clock: clock, services: services => services.AddOpenIddict().AddServer(server =>
                server.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(handler => handler
                    // Client authentication and grant checks finish before the passthrough endpoint runs.
                    .SetOrder(int.MaxValue)
                    .UseInlineHandler(async context =>
                    {
                        if (context.Request.IsClientCredentialsGrantType() && context.Request.ClientId == clientId && invalidate is not null)
                        {
                            await invalidate();
                            invalidated = true;
                        }
                    }))));
        var (_, token) = await oidc.SignInUserAsync("client-race@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var keys = $"{Organizations}/{organization}/api-keys";
        var created = await oidc.OkAsync(HttpMethod.Post, keys, token,
            new { name = "race", scopes = ReadOnly, expires_at = clock.GetUtcNow().AddMinutes(5) }, HttpStatusCode.Created);
        var key = created.GetProperty("key").GetString()!;
        await Assert.That((await TokenErrorAsync(oidc.ApiKeyTokenAsync(key))).Status).IsEqualTo(200);

        clientId = key[..26];
        invalidate = async () =>
        {
            if (expires)
            {
                clock.Advance(TimeSpan.FromMinutes(5));
            }
            else
            {
                await oidc.OkAsync(HttpMethod.Delete, $"{keys}/{clientId}", token, status: HttpStatusCode.NoContent);
            }
        };
        using var refused = await oidc.ApiKeyTokenAsync(key);
        var body = await OidcHarness.JsonAsync(refused);
        await Assert.That(invalidated).IsTrue();
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(body.GetProperty("error").GetString()).IsEqualTo("invalid_client");
        await Assert.That(body.TryGetProperty("access_token", out _)).IsFalse();
        await Assert.That(body.TryGetProperty("refresh_token", out _)).IsFalse();
        await Assert.That(body.TryGetProperty("id_token", out _)).IsFalse();
    }

    [Test]
    public async Task A_key_stored_narrowed_by_database_name_never_mints_a_token()
    {
        await using var oidc = await StartAsync();
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Production");
        var created = await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{organization}/projects/{project}/api-keys", token,
            new { name = "legacy", scopes = ReadOnly, database = await oidc.CreateDatabaseIdAsync("app") }, HttpStatusCode.Created);
        var key = created.GetProperty("key").GetString()!;
        var id = created.GetProperty("api_key").GetProperty("id").GetString()!;
        // Keys created before narrowing by ULID hold the database's name, which can move to another database.
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var stored = (await store.GetAsync(AuthStore.ApiKeyCollection, PlatformDirectory.Instance, id))!;
        var legacy = JsonNode.Parse(stored.Document.GetRawText())!.AsObject();
        legacy["database"] = "app";
        await Assert.That(await store.ReplaceAsync(AuthStore.ApiKeyCollection, PlatformDirectory.Instance, id,
            JsonSerializer.SerializeToElement(legacy), stored.Version)).IsNotNull();
        await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(key))).IsEqualTo((401, "invalid_client"));
    }

    [Test]
    public async Task Keys_stored_without_secret_bindings_keep_exchanging_and_rotate_into_a_bound_secret()
    {
        // Keys written before fresh-proof lineage existed carry no binding fields; keys written for sign-ins without lineage
        // carry empty lists. Both keep exchanging; rotation (with its step-up proof, attached by the harness) binds the new secret.
        await using var oidc = await StartAsync();
        var (_, token) = await oidc.SignInUserAsync("legacy@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var keys = $"{Organizations}/{organization}/api-keys";
        var key = (await oidc.OkAsync(HttpMethod.Post, keys, token, new { name = "legacy", scopes = ReadOnly }, HttpStatusCode.Created)).GetProperty("key").GetString()!;
        var id = key[..26];
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        foreach (var emptyLists in new[] { false, true })
        {
            var stored = (await store.GetAsync(AuthStore.ApiKeyCollection, OidcIssuer.InstanceId, id))!;
            var record = JsonNode.Parse(stored.Document.GetRawText())!.AsObject();
            record.Remove("secretBindings");
            record.Remove("previousSecretBindings");
            if (emptyLists)
            {
                record["secretBindings"] = new JsonArray();
                record["previousSecretBindings"] = new JsonArray();
            }

            await Assert.That(await store.ReplaceAsync(AuthStore.ApiKeyCollection, OidcIssuer.InstanceId, id, TerraBaseFixture.Json(record.ToJsonString()), stored.Version)).IsNotNull();
            await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(key))).IsEqualTo((200, null));
        }

        var rotated = (await oidc.OkAsync(HttpMethod.Post, $"{keys}/{id}/rotate", token, new { previous_secret_ttl = 0 })).GetProperty("key").GetString()!;
        await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(rotated))).IsEqualTo((200, null));
        await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(key))).IsEqualTo((401, "invalid_client"));
    }

    [Test]
    public async Task Rotation_keeps_the_previous_key_string_only_for_its_grace_and_expired_keys_are_refused()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, values => values["access-token-audiences"] = Audience, clock: clock);
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Production");
        var keys = $"{Organizations}/{organization}/projects/{project}/api-keys";
        var created = await oidc.OkAsync(HttpMethod.Post, keys, token, new { name = "backend", scopes = ReadOnly, expires_at = clock.GetUtcNow().AddDays(2) }, HttpStatusCode.Created);
        var first = created.GetProperty("key").GetString()!;
        var id = first[..26];

        // A body that is there must be the JSON object, chunked or not: it never silently means "no grace".
        using (var chunked = new HttpRequestMessage(HttpMethod.Post, $"{keys}/{id}/rotate") { Content = new StreamContent(new MemoryStream("previous_secret_ttl=3600"u8.ToArray())) })
        {
            chunked.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            chunked.Headers.TransferEncodingChunked = true;
            chunked.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            using var refused = await oidc.Client.SendAsync(chunked);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
        }

        await Assert.That((await TokenErrorAsync(oidc.ApiKeyTokenAsync(first))).Status).IsEqualTo(200);

        // Without a grace the old string stops at once, on every replica, whatever it authenticated before.
        await using var replica = await oidc.StartReplicaAsync(values => values["access-token-audiences"] = Audience);
        using (var warm = await replica.Client.SendAsync(KeyTokenRequest(first)))
        {
            await Assert.That(warm.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var second = (await oidc.OkAsync(HttpMethod.Post, $"{keys}/{id}/rotate", token)).GetProperty("key").GetString()!;
        await Assert.That(second[..26]).IsEqualTo(id);
        await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(first))).IsEqualTo((401, "invalid_client"));
        using (var stale = await replica.Client.SendAsync(KeyTokenRequest(first)))
        {
            await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        // The new string works at once, also on the replica that authenticated the old one.
        using (var fresh = await replica.Client.SendAsync(KeyTokenRequest(second)))
        {
            await Assert.That(fresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That((await TokenErrorAsync(oidc.ApiKeyTokenAsync(second))).Status).IsEqualTo(200);

        // With one, both work until it ends.
        var rotated = await oidc.OkAsync(HttpMethod.Post, $"{keys}/{id}/rotate", token, new { previous_secret_ttl = 3600 });
        var third = rotated.GetProperty("key").GetString()!;
        await Assert.That(rotated.GetProperty("api_key").GetProperty("previous_secret_expires_at").ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That((await TokenErrorAsync(oidc.ApiKeyTokenAsync(second))).Status).IsEqualTo(200);
        await Assert.That((await TokenErrorAsync(oidc.ApiKeyTokenAsync(third))).Status).IsEqualTo(200);
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(second))).IsEqualTo((401, "invalid_client"));
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{keys}/{id}", token)).GetProperty("previous_secret_expires_at").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{keys}/{id}/rotate", token, new { previous_secret_ttl = 86401 })).IsEqualTo((400, "invalid_request"));

        // An expired key is refused like a revoked one.
        clock.Advance(TimeSpan.FromDays(2));
        await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(third))).IsEqualTo((401, "invalid_client"));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_active_rotation_grace_cannot_be_shortened_and_rotation_resumes_at_exact_expiry(bool projectKey)
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, values => values["access-token-audiences"] = Audience, clock: clock);
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = projectKey ? await oidc.CreateProjectAsync(token, organization, "Production") : null;
        var keys = $"{Organizations}/{organization}{(project is null ? string.Empty : $"/projects/{project}")}/api-keys";
        var first = (await oidc.OkAsync(HttpMethod.Post, keys, token, new { name = "backend", scopes = ReadOnly }, HttpStatusCode.Created)).GetProperty("key").GetString()!;
        var id = first[..26];
        var route = $"{keys}/{id}";
        await using var replica = await StartReplicaAsync(oidc, clock);
        await Assert.That((await TokenErrorAsync(replica.Client.SendAsync(KeyTokenRequest(first)))).Status).IsEqualTo(200);

        var rotated = await oidc.OkAsync(HttpMethod.Post, $"{route}/rotate", token, new { previous_secret_ttl = 60 });
        var second = rotated.GetProperty("key").GetString()!;
        var expiry = rotated.GetProperty("api_key").GetProperty("previous_secret_expires_at").GetDateTimeOffset();
        await Assert.That(expiry).IsEqualTo(clock.GetUtcNow().AddSeconds(60));
        var metadata = (await oidc.OkAsync(HttpMethod.Get, route, token)).GetRawText();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var before = (await store.GetAsync(AuthStore.ApiKeyCollection, "terrascale", id))!;
        var auditRoute = $"{Organizations}/{organization}/audit-events";
        var events = (await oidc.OkAsync(HttpMethod.Get, auditRoute, token)).GetRawText();

        clock.Advance(TimeSpan.FromSeconds(30));
        // Neither a shorter/longer grace nor an explicit/default zero may replace the two working credentials.
        foreach (var body in new object?[] { new { previous_secret_ttl = 1 }, new { previous_secret_ttl = 60 }, new { previous_secret_ttl = 120 }, new { previous_secret_ttl = 0 }, null })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{route}/rotate", token, body)).IsEqualTo((409, "rotation_pending"));
            await Assert.That((await oidc.OkAsync(HttpMethod.Get, route, token)).GetRawText()).IsEqualTo(metadata);
            var after = (await store.GetAsync(AuthStore.ApiKeyCollection, "terrascale", id))!;
            await Assert.That(after.Version).IsEqualTo(before.Version);
            await Assert.That(after.Document.GetRawText()).IsEqualTo(before.Document.GetRawText());
            await Assert.That((await oidc.OkAsync(HttpMethod.Get, auditRoute, token)).GetRawText()).IsEqualTo(events);
            foreach (var client in new[] { oidc.Client, replica.Client })
            {
                await Assert.That((await TokenErrorAsync(client.SendAsync(KeyTokenRequest(first)))).Status).IsEqualTo(200);
                await Assert.That((await TokenErrorAsync(client.SendAsync(KeyTokenRequest(second)))).Status).IsEqualTo(200);
            }
        }

        clock.Advance(expiry - clock.GetUtcNow() - TimeSpan.FromTicks(1));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{route}/rotate", token, new { previous_secret_ttl = 0 })).IsEqualTo((409, "rotation_pending"));
        var last = (await store.GetAsync(AuthStore.ApiKeyCollection, "terrascale", id))!;
        await Assert.That(last.Version).IsEqualTo(before.Version);
        await Assert.That(last.Document.GetRawText()).IsEqualTo(before.Document.GetRawText());
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, route, token)).GetRawText()).IsEqualTo(metadata);
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, auditRoute, token)).GetRawText()).IsEqualTo(events);
        clock.Advance(TimeSpan.FromTicks(1));
        foreach (var client in new[] { oidc.Client, replica.Client })
        {
            await Assert.That(await TokenErrorAsync(client.SendAsync(KeyTokenRequest(first)))).IsEqualTo((401, "invalid_client"));
            await Assert.That((await TokenErrorAsync(client.SendAsync(KeyTokenRequest(second)))).Status).IsEqualTo(200);
        }

        // The expired grace no longer blocks rotation; successive no-grace rotations are still permitted.
        var current = second;
        foreach (var body in new object?[] { null, new { previous_secret_ttl = 0 } })
        {
            var next = await oidc.OkAsync(HttpMethod.Post, $"{route}/rotate", token, body);
            var key = next.GetProperty("key").GetString()!;
            await Assert.That(key[..26]).IsEqualTo(id);
            await Assert.That(next.GetProperty("api_key").GetProperty("previous_secret_expires_at").ValueKind).IsEqualTo(JsonValueKind.Null);
            foreach (var client in new[] { oidc.Client, replica.Client })
            {
                await Assert.That(await TokenErrorAsync(client.SendAsync(KeyTokenRequest(current)))).IsEqualTo((401, "invalid_client"));
                await Assert.That((await TokenErrorAsync(client.SendAsync(KeyTokenRequest(key)))).Status).IsEqualTo(200);
            }

            current = key;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Concurrent_positive_grace_rotations_across_replicas_have_one_winner_and_preserve_the_original_secret(bool projectKey)
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var pause = new PausingTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, values => values["access-token-audiences"] = Audience, clock: clock,
            services: services => services.AddSingleton(new HttpClient(pause)));
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = projectKey ? await oidc.CreateProjectAsync(token, organization, "Production") : null;
        var keys = $"{Organizations}/{organization}{(project is null ? string.Empty : $"/projects/{project}")}/api-keys";
        var original = (await oidc.OkAsync(HttpMethod.Post, keys, token, new { name = "backend", scopes = ReadOnly }, HttpStatusCode.Created)).GetProperty("key").GetString()!;
        var id = original[..26];
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var before = (await store.GetAsync(AuthStore.ApiKeyCollection, "terrascale", id))!;
        await using var replica = await StartReplicaAsync(oidc, clock);

        // Hold one real transaction after its reads so it must retry against a rotation committed by another replica.
        var held = await pause.HoldNextAsync(() => oidc.FailAsync(HttpMethod.Post, $"{keys}/{id}/rotate", token, new { previous_secret_ttl = 60 }));
        (HttpStatusCode Status, JsonElement Body)[] responses;
        (int Status, string? Code) refused;
        try
        {
            responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
            {
                using var response = await oidc.CallAsync(HttpMethod.Post, $"{keys}/{id}/rotate", token, new { previous_secret_ttl = 60 }, client: replica.Client);
                return (response.StatusCode, await OidcHarness.JsonAsync(response));
            }));
        }
        finally
        {
            refused = await pause.ReleaseAsync(held);
        }

        await Assert.That(refused).IsEqualTo((409, "rotation_pending"));
        await Assert.That(responses.Count(response => response.Status == HttpStatusCode.OK)).IsEqualTo(1);
        await Assert.That(responses.Count(response => response.Status == HttpStatusCode.Conflict)).IsEqualTo(2);
        foreach (var response in responses.Where(response => response.Status == HttpStatusCode.Conflict))
        {
            await Assert.That(response.Body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("rotation_pending");
        }

        var winner = responses.Single(response => response.Status == HttpStatusCode.OK).Body;
        var key = winner.GetProperty("key").GetString()!;
        await Assert.That(key[..26]).IsEqualTo(id);
        var after = (await store.GetAsync(AuthStore.ApiKeyCollection, "terrascale", id))!;
        await Assert.That(after.Document.GetProperty("previousSecretHash").GetString()).IsEqualTo(before.Document.GetProperty("secretHash").GetString());
        await Assert.That(after.Document.GetProperty("previousSecretExpiresAt").GetDateTimeOffset()).IsEqualTo(clock.GetUtcNow().AddSeconds(60));
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{keys}/{id}", token)).GetRawText()).IsEqualTo(winner.GetProperty("api_key").GetRawText());
        var events = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{organization}/audit-events", token)).GetProperty("items");
        await Assert.That(events.EnumerateArray().Count(item => item.GetProperty("action").GetString() == "api_key.rotated")).IsEqualTo(1);
        foreach (var client in new[] { oidc.Client, replica.Client })
        {
            await Assert.That((await TokenErrorAsync(client.SendAsync(KeyTokenRequest(original)))).Status).IsEqualTo(200);
            await Assert.That((await TokenErrorAsync(client.SendAsync(KeyTokenRequest(key)))).Status).IsEqualTo(200);
        }
    }

    [Test]
    public async Task Key_tokens_report_live_revocation_rotation_and_the_keys_expiration()
    {
        await using var oidc = await StartAsync();
        var (_, token) = await oidc.SignInUserAsync("key-status@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Production");
        var keys = $"{Organizations}/{organization}/projects/{project}/api-keys";
        const string status = "/v1/terrascale/api-keys/self";
        var created = await oidc.OkAsync(HttpMethod.Post, keys, token, new { name = "status", scopes = ReadOnly }, HttpStatusCode.Created);
        var key = created.GetProperty("key").GetString()!;
        var id = key[..26];
        async Task<string> ExchangeAsync(string secret)
        {
            using var response = await oidc.ApiKeyTokenAsync(secret);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            return (await OidcHarness.JsonAsync(response)).GetProperty("access_token").GetString()!;
        }

        var access = await ExchangeAsync(key);
        var jwt = await oidc.ValidateAsync(access, Audience);
        await Assert.That(jwt.GetClaim("key_id").Value).IsEqualTo(id);
        await Assert.That(jwt.GetClaim("key_gen").Value).IsEqualTo(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16]);
        using (var live = await oidc.CallAsync(HttpMethod.Get, status, access))
        {
            await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await OidcHarness.JsonAsync(live)).GetProperty("key_id").GetString()).IsEqualTo(id);
            await Assert.That(live.Headers.CacheControl!.NoStore).IsTrue();
        }

        var rotated = await oidc.OkAsync(HttpMethod.Post, $"{keys}/{id}/rotate", token, new { previous_secret_ttl = 0 });
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, status, access)).IsEqualTo((401, "key_revoked"));
        var fresh = await ExchangeAsync(rotated.GetProperty("key").GetString()!);
        await oidc.OkAsync(HttpMethod.Get, status, fresh);
        await oidc.OkAsync(HttpMethod.Delete, $"{keys}/{id}", token, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, status, fresh)).IsEqualTo((401, "key_revoked"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, status, token)).IsEqualTo((401, "invalid_token"));
        var expires = DateTimeOffset.UtcNow.AddSeconds(5);
        var shortKey = (await oidc.OkAsync(HttpMethod.Post, keys, token, new { name = "short", scopes = ReadOnly, expires_at = expires }, HttpStatusCode.Created)).GetProperty("key").GetString()!;
        var shortToken = await oidc.ValidateAsync(await ExchangeAsync(shortKey), Audience);
        await Assert.That(new DateTimeOffset(shortToken.ValidTo, TimeSpan.Zero)).IsLessThanOrEqualTo(expires);
    }

    private Task<OidcHarness> StartAsync(TimeProvider? clock = null) => OidcHarness.StartAsync(fixture, values => values["access-token-audiences"] = Audience, clock: clock);

    private Task<AuthTestServer> StartReplicaAsync(OidcHarness oidc, ManualClock clock) =>
        OidcHarness.StartServerAsync(fixture, oidc.Tenant, oidc.Email, values =>
        {
            values["master-key"] = Convert.ToBase64String(oidc.Server.App.Services.GetRequiredService<AuthOptions>().MasterKey);
            values["access-token-audiences"] = Audience;
        }, clock: clock);

    private static HttpRequestMessage KeyTokenRequest(string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/terrascale/oauth/token") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{key[..26]}:{key}")));
        return request;
    }

    private static async Task<(int Status, string? Error)> TokenErrorAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        var body = await OidcHarness.JsonAsync(response);
        return ((int)response.StatusCode, body.TryGetProperty("error", out var error) ? error.GetString() : null);
    }

}
