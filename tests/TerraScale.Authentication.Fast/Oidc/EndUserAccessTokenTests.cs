using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>Real HTTP minting with authoritative API-key storage through the SDK and same-commit TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class EndUserAccessTokenTests(TerraBaseFixture fixture)
{
    private const string Audience = "https://api.terrascale.tech";
    private static readonly string[] ReadWrite = ["documents:read", "documents:write"];
    private static readonly string[] ReadOnly = ["documents:read"];
    private static readonly string[] WriteOnly = ["documents:write"];
    private static readonly string[] ExpectedRoles = ["reader", "Reader"];
    private static readonly string[] AssertedRoles = ["reader", "Reader", "reader"];

    [Test]
    public async Task A_backend_asserts_an_unprovisioned_identity_and_roles_and_revocation_stops_future_minting()
    {
        await using var oidc = await this.StartAsync();
        var (owner, organization, project) = await SetupAsync(oidc);
        var key = await KeyAsync(oidc, owner, organization, project);
        var user = AuthStore.NewId();
        var body = new { user_id = user, claims = new { roles = AssertedRoles } };
        using var minted = await oidc.CallAsync(HttpMethod.Post, Path(project), key, body);
        await Assert.That(minted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(minted.Headers.CacheControl!.NoStore).IsTrue();
        await Assert.That(minted.Headers.Contains("Set-Cookie")).IsFalse();
        var response = await OidcHarness.JsonAsync(minted);
        await Assert.That(response.GetProperty("token_type").GetString()).IsEqualTo("Bearer");
        await Assert.That(response.GetProperty("expires_in").GetInt64()).IsEqualTo(900);
        var encoded = response.GetProperty("access_token").GetString()!;
        var jwt = await oidc.ValidateAsync(encoded, Audience);
        await Assert.That(jwt.Subject).IsEqualTo(user);
        await Assert.That(jwt.Typ).IsEqualTo("at+jwt");
        await Assert.That(jwt.Alg).IsEqualTo("ES256");
        await Assert.That(string.IsNullOrEmpty(jwt.Kid)).IsFalse();
        await Assert.That(jwt.GetPayloadValue<string>("project_id")).IsEqualTo(project);
        await Assert.That(jwt.GetPayloadValue<string>("instance_id")).IsEqualTo(project);
        await Assert.That(jwt.GetPayloadValue<string>("token_kind")).IsEqualTo("end_user");
        await Assert.That(jwt.GetPayloadValue<string[]>("roles")).IsEquivalentTo(ExpectedRoles);
        foreach (var claim in new[] { "scope", "scopes", "database", "platform_role", "org_id" })
        {
            await Assert.That(jwt.TryGetPayloadValue<object>(claim, out _)).IsFalse().Because(claim);
        }

        await Assert.That(jwt.ValidTo - jwt.IssuedAt).IsEqualTo(EndUserAccessTokens.Lifetime);
        // Warm all authority/signing caches before revocation.
        await oidc.OkAsync(HttpMethod.Post, Path(project), key, body);
        await oidc.OkAsync(HttpMethod.Delete, $"{Keys(organization, project)}/{key[..26]}", owner, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(project), key, body)).IsEqualTo((401, "unauthorized"));
        // Offline JWT validation does not contact ts-auth to check key revocation.
        await Assert.That((await oidc.ValidateAsync(encoded, Audience)).Subject).IsEqualTo(user);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Rotated_keys_survive_browser_sign_out_but_stop_on_grace_expiry_key_or_global_revocation(bool expirePrevious)
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await this.StartAsync(clock);
        var (creator, session, tokens) = await oidc.SignInAndRedeemAsync("owner@example.test");
        var owner = tokens.GetProperty("access_token").GetString()!;
        var organization = await oidc.CreateOrganizationAsync(owner);
        var project = await oidc.CreateProjectAsync(owner, organization, "Production");
        var original = await KeyAsync(oidc, owner, organization, project);
        var rotation = await oidc.OkAsync(HttpMethod.Post, $"{Keys(organization, project)}/{original[..26]}/rotate", owner,
            new { previous_secret_ttl = 60 });
        var current = rotation.GetProperty("key").GetString()!;
        var epochKey = await KeyAsync(oidc, owner, organization, project);
        var (other, otherToken) = await oidc.SignInUserAsync("other@example.test");
        await Assert.That(await oidc.AddMemberAsync(organization, other.Id, "admin")).IsTrue();
        var otherKey = await KeyAsync(oidc, otherToken, organization, project);
        var body = new { user_id = AuthStore.NewId() };
        await oidc.OkAsync(HttpMethod.Post, Path(project), original, body);
        await oidc.OkAsync(HttpMethod.Post, Path(project), current, body);

        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var key = (await store.GetAsync(AuthStore.ApiKeyCollection, OidcIssuer.InstanceId, original[..26]))!;
        using (var signOut = await oidc.RevokeAsync(tokens.GetProperty("refresh_token").GetString()!))
        {
            await Assert.That(signOut.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        await Assert.That(await sessions.RevokeAsync(OidcIssuer.InstanceId, session)).IsTrue();
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, owner)).IsEqualTo((401, "unauthorized"));
        // Browser revocation leaves fresh credential lineage current.
        await oidc.OkAsync(HttpMethod.Post, Path(project), original, body);
        await oidc.OkAsync(HttpMethod.Post, Path(project), current, body);

        if (expirePrevious)
        {
            clock.Advance(TimeSpan.FromSeconds(61));
            await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(project), original, body)).IsEqualTo((401, "unauthorized"));
            await oidc.OkAsync(HttpMethod.Post, Path(project), current, body);
        }
        else
        {
            await Assert.That(await store.CompareAndDeleteAsync(AuthStore.ApiKeyCollection, OidcIssuer.InstanceId,
                original[..26], key.Version)).IsTrue();
            foreach (var secret in new[] { original, current })
            {
                await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(project), secret, body)).IsEqualTo((401, "unauthorized"));
            }
        }

        // Keep an undeleted key live so the deletion branch cannot mask global account-state revocation.
        await oidc.OkAsync(HttpMethod.Post, Path(project), epochKey, body);
        await oidc.OkAsync(HttpMethod.Post, Path(project), otherKey, body);
        await Assert.That(await sessions.RevokeAllAsync(OidcIssuer.InstanceId, creator.Id)).IsTrue();
        foreach (var secret in new[] { original, current, epochKey })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(project), secret, body)).IsEqualTo((401, "unauthorized"));
        }
        await oidc.OkAsync(HttpMethod.Post, Path(project), otherKey, body);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Revocation_or_expiry_after_initial_key_authentication_is_rechecked_before_signing(bool expires)
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await this.StartAsync(clock);
        var (owner, organization, project) = await SetupAsync(oidc);
        var key = await KeyAsync(oidc, owner, organization, project, expires: clock.GetUtcNow().AddMinutes(1));
        var changed = false;
        using var handler = new SocketsHttpHandler { Expect100ContinueTimeout = Timeout.InfiniteTimeSpan, UseCookies = false };
        using var client = new HttpClient(handler) { BaseAddress = oidc.Client.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(project))
        {
            Content = new AfterContinueContent(Encoding.UTF8.GetBytes("{\"user_id\":\"" + AuthStore.NewId() + "\"}"), async () =>
            {
                // Kestrel sends 100 Continue when the authenticated endpoint begins reading its body.
                if (expires)
                {
                    clock.Advance(TimeSpan.FromMinutes(1));
                }
                else
                {
                    await oidc.OkAsync(HttpMethod.Delete, $"{Keys(organization, project)}/{key[..26]}", owner, status: HttpStatusCode.NoContent);
                }

                changed = true;
            }),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.ExpectContinue = true;
        using var refused = await client.SendAsync(request);
        await Assert.That(changed).IsTrue();
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await OidcHarness.JsonAsync(refused)).TryGetProperty("access_token", out _)).IsFalse();
    }

    [Test]
    public async Task Foreign_org_narrowed_tampered_platform_and_anonymous_credentials_cannot_mint()
    {
        await using var oidc = await this.StartAsync();
        var (owner, organization, project) = await SetupAsync(oidc);
        var other = await oidc.CreateProjectAsync(owner, organization, "Other");
        var key = await KeyAsync(oidc, owner, organization, project);
        var tampered = key[..^1] + (key[^1] == '0' ? '1' : '0');
        var organizationKey = await KeyAsync(oidc, owner, organization, null);
        var read = await KeyAsync(oidc, owner, organization, project, scopes: ReadOnly);
        var write = await KeyAsync(oidc, owner, organization, project, scopes: WriteOnly);
        var narrowed = await KeyAsync(oidc, owner, organization, project, database: await oidc.CreateDatabaseIdAsync("app"));
        foreach (var credential in new[] { tampered, organizationKey, read, write, narrowed, owner, null })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(project), credential, new { user_id = AuthStore.NewId() }))
                .IsEqualTo((401, "unauthorized"));
        }

        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(other), key, new { user_id = AuthStore.NewId() })).IsEqualTo((401, "unauthorized"));
        using var cookieRequest = new HttpRequestMessage(HttpMethod.Post, Path(project)) { Content = new StringContent("{", Encoding.UTF8, "application/json") };
        cookieRequest.Headers.Add("Cookie", "tsauth_session=" + key);
        using var cookie = await oidc.Client.SendAsync(cookieRequest);
        await Assert.That(cookie.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(cookie.Headers.WwwAuthenticate.Single().Scheme).IsEqualTo("Bearer");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Deleted_projects_and_organizations_cannot_mint_even_with_a_previously_accepted_key(bool deleteOrganization)
    {
        await using var oidc = await this.StartAsync();
        var (owner, organization, project) = await SetupAsync(oidc);
        var key = await KeyAsync(oidc, owner, organization, project);
        var body = new { user_id = AuthStore.NewId() };
        var minted = await oidc.OkAsync(HttpMethod.Post, Path(project), key, body);
        await Assert.That((await oidc.ValidateAsync(minted.GetProperty("access_token").GetString()!, Audience)).GetPayloadValue<string[]>("roles").Length).IsEqualTo(0);
        if (deleteOrganization)
        {
            // Model missing organization authority independently of project/key cleanup, through the real SDK store.
            await oidc.Database.DeleteDocumentAsync(AuthStore.OrganizationCollection,
                AuthStore.NaturalKeyId(OidcIssuer.InstanceId, AuthStore.OrganizationCollection, organization));
        }
        else
        {
            await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{organization}/projects/{project}", owner, status: HttpStatusCode.NoContent);
        }
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(project), key, body)).IsEqualTo((401, "unauthorized"));
    }

    [Test]
    public async Task Expiry_is_capped_at_key_expiry_and_expired_keys_are_rechecked_after_success()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await this.StartAsync(clock);
        var (owner, organization, project) = await SetupAsync(oidc);
        var expiry = clock.GetUtcNow().AddMinutes(1);
        var key = await KeyAsync(oidc, owner, organization, project, expires: expiry);
        var body = new { user_id = AuthStore.NewId() };
        var minted = await oidc.OkAsync(HttpMethod.Post, Path(project), key, body);
        await Assert.That(minted.GetProperty("expires_in").GetInt64()).IsEqualTo(60);
        var jwt = await oidc.ValidateAsync(minted.GetProperty("access_token").GetString()!, Audience);
        await Assert.That(jwt.ValidTo <= expiry.UtcDateTime).IsTrue();
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(project), key, body)).IsEqualTo((401, "unauthorized"));
    }

    [Test]
    public async Task Reserved_claims_invalid_identity_and_unbounded_JSON_are_refused()
    {
        await using var oidc = await this.StartAsync();
        var (owner, organization, project) = await SetupAsync(oidc);
        var key = await KeyAsync(oidc, owner, organization, project);
        var user = AuthStore.NewId();
        foreach (var reserved in new[] { "sub", "iss", "aud", "exp", "nbf", "iat", "jti", "token_kind", "instance_id", "project_id", "org_id", "scope", "scopes", "database", "platform_role", "is_anonymous", "admin", "custom" })
        {
            var claims = new Dictionary<string, object> { [reserved] = "injected" };
            await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(project), key, new { user_id = user, claims })).IsEqualTo((400, "invalid_request"));
            var top = new Dictionary<string, object> { ["user_id"] = user, [reserved] = "injected" };
            await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(project), key, top)).IsEqualTo((400, "invalid_request"));
        }

        foreach (var roles in new[] { new string?[] { null }, [""], [" "], ["reader\n"], [new string('x', 129)], Enumerable.Repeat<string?>("reader", 65).ToArray() })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(project), key, new { user_id = user, claims = new { roles } })).IsEqualTo((400, "invalid_request"));
        }

        foreach (var invalid in new[] { "invalid", user.ToLowerInvariant(), "", null })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Post, Path(project), key, new { user_id = invalid })).IsEqualTo((400, "invalid_request"));
        }

        foreach (var json in new[] { "null", "[]", "{", "{\"user_id\":1}", "{\"user_id\":\"" + user + "\",\"claims\":{\"roles\":\"reader\"}}", new string(' ', 16385) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Path(project)) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var invalid = await oidc.Client.SendAsync(request);
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(invalid.Headers.CacheControl!.NoStore).IsTrue();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Anonymous_bad_bodies_charge_the_shared_IP_budget_including_route_casing(bool alternatePath)
    {
        // All four requests exercise one fixed minute, even when a loaded runner crosses a wall-clock boundary.
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, values => values["rate-limit"] = "3", clock: clock);
        var path = Path(AuthStore.NewId());
        if (alternatePath)
        {
            path = path.ToUpperInvariant() + "/";
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var invalid = await oidc.CallAsync(HttpMethod.Post, path, null, new { ignored = attempt });
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        using var limited = await oidc.CallAsync(HttpMethod.Post, path, null);
        await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(limited.Headers.RetryAfter!.Delta > TimeSpan.Zero).IsTrue();
        await Assert.That(limited.Headers.CacheControl!.NoStore).IsTrue();
        await Assert.That(await OidcHarness.ErrorAsync(limited)).IsEqualTo("rate_limited");
    }

    [Test]
    public async Task The_complete_signed_token_is_bounded_at_the_ASCII_header_limit_including_Unicode_expansion()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await this.StartAsync(clock);
        var (owner, organization, project) = await SetupAsync(oidc);
        var key = await KeyAsync(oidc, owner, organization, project);
        var user = AuthStore.NewId();
        var accepted = 0;
        var rejected = 64 * 126;
        var largestToken = string.Empty;
        while (accepted + 1 < rejected)
        {
            var middle = (accepted + rejected) / 2;
            var roles = LargeRoles(middle, 'x');
            using var response = await oidc.CallAsync(HttpMethod.Post, Path(project), key, new { user_id = user, claims = new { roles } });
            if (response.StatusCode == HttpStatusCode.OK)
            {
                accepted = middle;
                largestToken = (await OidcHarness.JsonAsync(response)).GetProperty("access_token").GetString()!;
            }
            else
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
                await Assert.That((await OidcHarness.JsonAsync(response)).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_request");
                rejected = middle;
            }
        }

        await Assert.That(Encoding.ASCII.GetByteCount(largestToken) <= EndUserAccessTokens.MaxTokenBytes).IsTrue();
        await Assert.That(largestToken.Length >= EndUserAccessTokens.MaxTokenBytes - 1).IsTrue();
        await Assert.That((await oidc.ValidateAsync(largestToken, Audience)).Subject).IsEqualTo(user);
        foreach (var roles in new[] { LargeRoles(rejected, 'x'), LargeRoles(4000, 'é') })
        {
            // These generated roles contain only digits and the chosen letter; raw UTF-8 avoids HTTP JSON escaping
            // rejecting the body before the signed-token size check can observe Unicode expansion.
            var json = "{\"user_id\":\"" + user + "\",\"claims\":{\"roles\":[\"" + string.Join("\",\"", roles) + "\"]}}";
            using var request = new HttpRequestMessage(HttpMethod.Post, Path(project)) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await oidc.Client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            var failure = await OidcHarness.JsonAsync(response);
            await Assert.That(failure.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_request");
            await Assert.That(failure.GetProperty("error").GetProperty("message").GetString()).Contains("8192");
            await Assert.That(failure.TryGetProperty("access_token", out _)).IsFalse();
        }
    }

    [Test]
    public async Task The_key_budget_and_OpenAPI_contract_match_the_HTTP_boundary()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await this.StartAsync(clock);
        var (owner, organization, project) = await SetupAsync(oidc);
        var key = await KeyAsync(oidc, owner, organization, project);
        var limiter = oidc.Server.App.Services.GetRequiredService<AttemptLimiter>();
        var before = (await oidc.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        await limiter.AcquireAsync(OidcIssuer.InstanceId, "end-user-token-key", key[..26], PlatformGrants.KeyRequestsPerMinute, TimeSpan.FromMinutes(1), CancellationToken.None);
        var bucket = (await oidc.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Single(item => !before.Contains(item.Id));
        var full = JsonNode.Parse(bucket.Document.GetRawText())!.AsObject();
        full["used"] = PlatformGrants.KeyRequestsPerMinute;
        await oidc.Database.PutDocumentAsync(AttemptLimiter.Collection, bucket.Id, JsonSerializer.SerializeToElement(full), TerraBasePrecondition.Matches(bucket.Version));

        using var limited = await oidc.CallAsync(HttpMethod.Post, Path(project), key, new { user_id = AuthStore.NewId() });
        await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        using var contract = await oidc.Client.GetAsync("/openapi/v1.json");
        var document = await OidcHarness.JsonAsync(contract);
        var operation = document.GetProperty("paths").GetProperty("/v1/terrascale/projects/{project}/end-user-tokens").GetProperty("post");
        await Assert.That(operation.GetProperty("security")[0].TryGetProperty("projectApiKey", out _)).IsTrue();
        await Assert.That(operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()).IsEqualTo("#/components/schemas/EndUserTokenRequest");
        foreach (var status in new[] { "200", "400", "401", "429", "503" })
        {
            await Assert.That(operation.GetProperty("responses").TryGetProperty(status, out _)).IsTrue();
        }
    }

    private Task<OidcHarness> StartAsync(TimeProvider? clock = null) => OidcHarness.StartAsync(fixture, values => values["access-token-audiences"] = Audience, clock: clock);

    private static async Task<(string Owner, string Organization, string Project)> SetupAsync(OidcHarness oidc)
    {
        var (_, owner) = await oidc.SignInUserAsync("owner@example.test");
        var organization = await oidc.CreateOrganizationAsync(owner);
        return (owner, organization, await oidc.CreateProjectAsync(owner, organization, "Production"));
    }

    private static string Path(string project) => $"/v1/terrascale/projects/{project}/end-user-tokens";

    private static string Keys(string organization, string? project) => $"{Organizations}/{organization}" + (project is null ? string.Empty : $"/projects/{project}") + "/api-keys";

    private static string[] LargeRoles(int characters, char value) => Enumerable.Range(0, 64)
        .Select(index => index.ToString("D2", CultureInfo.InvariantCulture) + new string(value, Math.Clamp(characters - index * 126, 0, 126))).ToArray();

    private static async Task<string> KeyAsync(OidcHarness oidc, string owner, string organization, string? project, string? database = null, string[]? scopes = null, DateTimeOffset? expires = null) =>
        (await oidc.OkAsync(HttpMethod.Post, Keys(organization, project), owner,
            new { name = "backend", scopes = scopes ?? ReadWrite, database, expires_at = expires }, HttpStatusCode.Created)).GetProperty("key").GetString()!;

    private sealed class AfterContinueContent(byte[] body, Func<Task> beforeWrite) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await beforeWrite();
            await stream.WriteAsync(body);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return true;
        }
    }
}
