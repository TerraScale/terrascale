using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Slow.Security;

/// <summary>
/// Per-instance ES256 signing keys in TerraBase: sealed storage, JWT signing and validation, rotation seen by every
/// replica, and tenant isolation. Test routes sign and validate through the key ring.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class KeyRingTests(TerraBaseFixture fixture)
{
    private const string Audience = "test-audience";
    private const string AccessType = "at+jwt";

    // The project tenants these tests sign for.
    private static readonly string[] Projects = ["ins_a", "ins_b"];

    // Binds to the real TerraBase target; without one the server's storage is unreachable.
    private static Task<AuthTestServer> StartAsync(SharedStateTarget? target, string? masterKey = null, TimeProvider? clock = null, TimeSpan? refresh = null) => AuthTestServer.StartAsync(
        settings: values =>
        {
            if (masterKey is not null)
            {
                values["master-key"] = masterKey;
            }
        },
        services: services =>
        {
            target?.Register(services);
            if (refresh is { } interval)
            {
                services.AddSingleton(new KeyRingRefresh(interval));
            }

            if (clock is not null)
            {
                services.AddSingleton(clock);
            }
        },
        routes: app =>
        {
            app.MapPost("/__test/instances/{instance}/tokens", async (string instance, SignRequest request, KeyRing ring, AuthOptions options, TimeProvider time, CancellationToken cancellationToken) =>
            {
                var keys = await ring.GetActiveAsync(instance, cancellationToken);
                try
                {
                    var claims = request.Claims.ToDictionary(pair => pair.Key, pair => (object)pair.Value);
                    // The replica's clock stamps tokens as OpenIddict's does; validation here uses real time.
                    var issuedAt = (request.ReplicaClock ? time.GetUtcNow() : DateTimeOffset.UtcNow).AddSeconds(-request.AgeSeconds);
                    return Results.Ok(new { token = Jwt.Sign(keys[0], request.Type, Issuer(options, instance), request.Audience ?? Audience, claims, issuedAt, TimeSpan.FromSeconds(request.LifetimeSeconds)) });
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest();
                }
                finally
                {
                    keys.ToList().ForEach(key => key.Dispose());
                }
            });
            app.MapPost("/__test/instances/{instance}/tokens/validate", async (string instance, ValidateRequest request, KeyRing ring, AuthOptions options, CancellationToken cancellationToken) =>
            {
                // As PlatformAuthentication and OpenIddict validation do: name the token's key.
                var keys = await ring.GetPublicAsync(instance, Jwt.KeyId(request.Token), cancellationToken);
                try
                {
                    var claims = await Jwt.ValidateAsync(request.Token, keys, request.Type, Issuer(options, instance), request.Audience);
                    return claims is null ? Results.Unauthorized() : Results.Ok(new { sub = claims["sub"].ToString() });
                }
                finally
                {
                    keys.ToList().ForEach(key => key.Dispose());
                }
            });
            app.MapPost("/__test/instances/{instance}/keys/rotate", async (string instance, KeyRing ring, CancellationToken cancellationToken) =>
            {
                using var key = await ring.RotateAsync(instance, cancellationToken);
                return new { kid = key.KeyId };
            });
            app.MapPost("/__test/instances/{instance}/keys/rotate-if-due", async (string instance, KeyRing ring, CancellationToken cancellationToken) =>
                new { rotated = await ring.RotateIfDueAsync(instance, cancellationToken) });
            app.MapGet("/__test/instances/{instance}/jwks", async (string instance, KeyRing ring, CancellationToken cancellationToken) =>
            {
                var keys = await ring.GetPublicAsync(instance, cancellationToken);
                try
                {
                    return Results.Text(
                        "{\"keys\":[" + string.Join(',', keys.Select(key => Serialize(key.PublicJwk()))) + "]}",
                        "application/json");
                }
                finally
                {
                    keys.ToList().ForEach(key => key.Dispose());
                }
            });
        },
        // A project's key is stored only while its project exists.
        beforeStart: target is null ? null : async services =>
        {
            var store = services.GetRequiredService<AuthStore>();
            foreach (var project in Projects)
            {
                if (await store.GetAsync(AuthStore.ProjectCollection, OidcIssuer.InstanceId, project) is null)
                {
                    await store.CreateAsync(AuthStore.ProjectCollection, OidcIssuer.InstanceId, project, TerraBaseFixture.Json($$"""{"id":"{{project}}"}"""));
                }
            }
        });

    private static string Issuer(AuthOptions options, string instance) => new Uri(options.PublicOrigin, instance).ToString();

    // Every member the JWK carries, so a leaked private part ("d") would show up.
    private static string Serialize(JsonWebKey jwk) => JsonSerializer.Serialize(new Dictionary<string, string?>
    {
        ["kty"] = jwk.Kty,
        ["crv"] = jwk.Crv,
        ["use"] = jwk.Use,
        ["alg"] = jwk.Alg,
        ["kid"] = jwk.Kid,
        ["x"] = jwk.X,
        ["y"] = jwk.Y,
        ["d"] = jwk.D,
    });

    [Test]
    public async Task First_use_creates_one_sealed_key_and_publishes_only_its_public_part()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = RandomNumberGenerator.GetBytes(32);
        await using var server = await StartAsync(target, Convert.ToBase64String(masterKey));

        var jwks = await server.Client.GetFromJsonAsync<JsonElement>("/__test/instances/ins_a/jwks");
        var again = await server.Client.GetFromJsonAsync<JsonElement>("/__test/instances/ins_a/jwks");
        var key = jwks.GetProperty("keys").EnumerateArray().Single();
        var kid = key.GetProperty("kid").GetString()!;

        await Assert.That(again.GetProperty("keys").EnumerateArray().Single().GetProperty("kid").GetString()).IsEqualTo(kid);
        await Assert.That(TerraScaleIds.IsValid(kid)).IsTrue();
        await Assert.That(key.GetProperty("kty").GetString()).IsEqualTo("EC");
        await Assert.That(key.GetProperty("crv").GetString()).IsEqualTo("P-256");
        await Assert.That(key.GetProperty("alg").GetString()).IsEqualTo("ES256");
        await Assert.That(key.GetProperty("use").GetString()).IsEqualTo("sig");
        await Assert.That(key.GetProperty("d").ValueKind).IsEqualTo(JsonValueKind.Null);

        // TerraBase holds the private key only as a v1 box under TS_AUTH_MASTER_KEY, bound to its kid and instance.
        var stored = (await StoredAsync(target, "ins_a")).Single();
        var boxed = stored["secretBox"]!.GetValue<string>();
        var secret = SecretBox.Open(masterKey, boxed, $"signing-key:{kid}:ins_a");
        await Assert.That(stored["id"]!.GetValue<string>()).IsEqualTo(kid);
        await Assert.That(stored["algorithm"]!.GetValue<string>()).IsEqualTo("ES256");
        await Assert.That(boxed).StartsWith("v1:");
        await Assert.That(secret.Length).IsEqualTo(96);
        await Assert.That(Base64UrlEncoder.Encode(secret[32..64])).IsEqualTo(key.GetProperty("x").GetString());
        await Assert.That(Base64UrlEncoder.Encode(secret[64..])).IsEqualTo(key.GetProperty("y").GetString());
        await Assert.That(stored.ToJsonString()).DoesNotContain(Convert.ToBase64String(secret[..32]));
        await Assert.That(() => SecretBox.Open(masterKey, boxed, $"signing-key:{kid}:ins_b")).Throws<CryptographicException>();
    }

    [Test]
    public async Task Scheduled_rotation_publishes_the_next_key_before_it_signs_and_retires_the_old_one_later()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var server = await StartAsync(target, masterKey, clock);
        await using var replica = await StartAsync(target, masterKey, clock);
        var first = KeyId(await SignAsync(server, "ins_a", AccessType));

        clock.Advance(KeyRing.RotationAge - TimeSpan.FromSeconds(1));
        await Assert.That(await RotateIfDueAsync(server)).IsFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await RotateIfDueAsync(server)).IsTrue();
        await Assert.That(await RotateIfDueAsync(replica)).IsFalse();

        // Published at once on every replica, but the old key keeps signing for the whole overlap.
        var next = (await KeyIdsAsync(replica)).First();
        await Assert.That(next).IsNotEqualTo(first);
        await Assert.That(await KeyIdsAsync(server)).IsEquivalentTo([next, first]);
        clock.Advance(KeyRing.Overlap - TimeSpan.FromSeconds(1));
        await Assert.That(KeyId(await SignAsync(replica, "ins_a", AccessType))).IsEqualTo(first);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(KeyId(await SignAsync(replica, "ins_a", AccessType))).IsEqualTo(next);
        await Assert.That(KeyId(await SignAsync(server, "ins_a", AccessType))).IsEqualTo(next);

        // The old key stays published until its successor has signed for the overlap, then the next rotation retires it.
        clock.Advance(KeyRing.RotationAge - KeyRing.Overlap);
        await Assert.That(await RotateIfDueAsync(server)).IsTrue();
        var afterRetirement = await KeyIdsAsync(server);
        await Assert.That(afterRetirement).DoesNotContain(first);
        await Assert.That(afterRetirement).Contains(next);
        await Assert.That(afterRetirement.Count).IsEqualTo(2);
    }

    [Test]
    public async Task An_unscheduled_key_is_published_one_refresh_interval_before_it_signs()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var refresh = TimeSpan.FromMinutes(1);
        await using var server = await StartAsync(target, masterKey, clock, refresh);
        var first = KeyId(await SignAsync(server, "ins_a", AccessType));

        var rotated = await RotateAsync(server);

        await Assert.That(await KeyIdsAsync(server)).Contains(rotated);
        await Assert.That(KeyId(await SignAsync(server, "ins_a", AccessType))).IsEqualTo(first);
        clock.Advance(refresh);
        await Assert.That(KeyId(await SignAsync(server, "ins_a", AccessType))).IsEqualTo(rotated);
    }

    [Test]
    public async Task Rotation_retires_a_key_only_after_its_successor_has_signed_for_the_overlap()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var server = await StartAsync(await SharedStateTarget.CreateAsync(fixture), clock: clock);
        var first = (await KeyIdsAsync(server)).Single();
        var second = await RotateAsync(server);

        // The second key signed from the start, so the first key's tokens are past the overlap only after it.
        clock.Advance(KeyRing.Overlap - TimeSpan.FromSeconds(1));
        var third = await RotateAsync(server);
        var afterThird = await KeyIdsAsync(server);

        clock.Advance(TimeSpan.FromSeconds(1));
        var fourth = await RotateAsync(server);
        var afterFourth = await KeyIdsAsync(server);

        await Assert.That(afterThird).IsEquivalentTo([third, second, first]);
        await Assert.That(afterFourth).IsEquivalentTo([fourth, third, second]);
    }

    [Test]
    public async Task Replicas_creating_first_keys_at_once_keep_both_and_agree_on_the_signer()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var server = await StartAsync(target, masterKey);
        await using var replica = await StartAsync(target, masterKey);

        var tokens = await Task.WhenAll(SignAsync(server, "ins_a", AccessType), SignAsync(replica, "ins_a", AccessType));

        var published = await KeyIdsAsync(server);
        await Assert.That(published).Contains(KeyId(tokens[0]));
        await Assert.That(published).Contains(KeyId(tokens[1]));
        await Assert.That(await ValidateAsync(replica, "ins_a", tokens[0])).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await ValidateAsync(server, "ins_a", tokens[1])).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(KeyId(await SignAsync(server, "ins_a", AccessType))).IsEqualTo(KeyId(await SignAsync(replica, "ins_a", AccessType)));
    }

    [Test]
    public async Task A_replica_with_another_master_key_fails_closed_and_never_adds_keys()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target);
        var token = await SignAsync(server, "ins_a", AccessType);
        await using var foreign = await StartAsync(target);

        using var signing = await foreign.Client.PostAsJsonAsync("/__test/instances/ins_a/tokens", new SignRequest(AccessType, new() { ["sub"] = "user_1" }));
        using var rotation = await foreign.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);

        await Assert.That(signing.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(await ValidateAsync(foreign, "ins_a", token)).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That((await StoredAsync(target, "ins_a")).Count).IsEqualTo(1);
        await Assert.That(await ValidateAsync(server, "ins_a", token)).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_key_copied_to_another_instance_is_never_used()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target);
        var token = await SignAsync(server, "ins_a", AccessType);

        // Storage-level tampering: instance B's address holds a copy of A's key, which still names A.
        var copied = (await StoredAsync(target, "ins_a")).Single();
        await target.PutAsync(AuthStore.SigningKeyCollection, "ins_b", copied["id"]!.GetValue<string>(), JsonSerializer.SerializeToElement(copied));

        // B serves only keys it owns: it creates its own, and A's tokens never validate as B's.
        var other = await SignAsync(server, "ins_b", AccessType);
        await Assert.That(KeyId(other)).IsNotEqualTo(KeyId(token));
        await Assert.That(await KeyIdsAsync(server, "ins_b")).DoesNotContain(KeyId(token));
        await Assert.That(await ValidateAsync(server, "ins_b", token)).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Public_metadata_cannot_be_rebound_to_another_key_or_instance(bool rebindTenant)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target);
        var token = await SignAsync(server, "ins_a", AccessType);
        var record = (await StoredAsync(target, "ins_a")).Single();
        var originalId = record["id"]!.GetValue<string>();
        var instance = rebindTenant ? "ins_b" : "ins_a";
        if (rebindTenant)
        {
            record["instanceId"] = instance;
        }
        else
        {
            record["id"] = AuthStore.NewId();
        }

        // A storage writer can change ownership or kid, but cannot replace the original MAC.
        await target.PutAsync(AuthStore.SigningKeyCollection, instance, record["id"]!.GetValue<string>(), JsonSerializer.SerializeToElement(record));

        using var lookup = await server.Client.GetAsync($"/__test/instances/{instance}/jwks");
        await Assert.That(lookup.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(await ValidateAsync(server, instance, token)).IsEqualTo(HttpStatusCode.InternalServerError);
        using var signing = await server.Client.PostAsJsonAsync($"/__test/instances/{instance}/tokens", new SignRequest(AccessType, new() { ["sub"] = "user_1" }));
        await Assert.That(signing.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        using var rotation = await server.Client.PostAsync($"/__test/instances/{instance}/keys/rotate", null);
        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        if (rebindTenant)
        {
            await Assert.That(await ValidateAsync(server, "ins_a", token)).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await StoredAsync(target, "ins_a")).Single()["id"]!.GetValue<string>()).IsEqualTo(originalId);
        }
    }

    [Test]
    public async Task A_stored_key_whose_public_point_does_not_match_its_private_key_is_rejected()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = RandomNumberGenerator.GetBytes(32);
        await using var server = await StartAsync(target, Convert.ToBase64String(masterKey));
        await SignAsync(server, "ins_a", AccessType);

        // Correctly sealed under the master key, but with one bit of Y flipped.
        var record = (await StoredAsync(target, "ins_a")).Single();
        var kid = record["id"]!.GetValue<string>();
        var secret = SecretBox.Open(masterKey, record["secretBox"]!.GetValue<string>(), $"signing-key:{kid}:ins_a");
        secret[80] ^= 1;
        record["secretBox"] = SecretBox.Seal(masterKey, secret, $"signing-key:{kid}:ins_a");
        await target.PutAsync(AuthStore.SigningKeyCollection, "ins_a", kid, JsonSerializer.SerializeToElement(record));

        using var response = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var signing = await server.Client.PostAsJsonAsync("/__test/instances/ins_a/tokens", new SignRequest(AccessType, new() { ["sub"] = "user_1" }));
        await Assert.That(signing.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task Rotation_at_capacity_is_refused_until_superseded_keys_pass_the_overlap()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target, clock: clock);
        await KeyIdsAsync(server);
        for (var added = 1; added < KeyRing.MaxKeys; added++)
        {
            await RotateAsync(server);
        }

        using var refused = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await StoredAsync(target, "ins_a")).Count).IsEqualTo(KeyRing.MaxKeys);

        clock.Advance(KeyRing.Overlap);
        await RotateAsync(server);
        await Assert.That((await StoredAsync(target, "ins_a")).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Tokens_validate_only_with_es256_the_right_key_issuer_audience_type_and_lifetime()
    {
        await using var server = await StartAsync(await SharedStateTarget.CreateAsync(fixture));
        var token = await SignAsync(server, "ins_a", AccessType);
        var parts = token.Split('.');
        var header = JsonDocument.Parse(Base64UrlEncoder.Decode(parts[0])).RootElement;
        var kid = header.GetProperty("kid").GetString()!;

        await Assert.That(header.GetProperty("alg").GetString()).IsEqualTo("ES256");
        await Assert.That(header.GetProperty("typ").GetString()).IsEqualTo(AccessType);
        await Assert.That(await ValidateAsync(server, "ins_a", token)).IsEqualTo(HttpStatusCode.OK);

        // An ID token is not an access token, and audiences and instances do not mix.
        await Assert.That(await ValidateAsync(server, "ins_a", await SignAsync(server, "ins_a", "id_token+jwt"))).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ValidateAsync(server, "ins_a", token, audience: "other-audience")).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ValidateAsync(server, "ins_b", token)).IsEqualTo(HttpStatusCode.Unauthorized);

        // Expired by one second, with no clock skew allowance.
        await Assert.That(await ValidateAsync(server, "ins_a", await SignAsync(server, "ins_a", AccessType, ageSeconds: 601))).IsEqualTo(HttpStatusCode.Unauthorized);

        // A changed payload, a stripped signature and alg=none are all rejected.
        var forged = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(Base64UrlEncoder.Decode(parts[1]).Replace("user_1", "admin_1", StringComparison.Ordinal)));
        await Assert.That(await ValidateAsync(server, "ins_a", $"{parts[0]}.{forged}.{parts[2]}")).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ValidateAsync(server, "ins_a", $"{parts[0]}.{parts[1]}.")).IsEqualTo(HttpStatusCode.Unauthorized);
        var none = Base64UrlEncoder.Encode($"{{\"alg\":\"none\",\"typ\":\"{AccessType}\",\"kid\":\"{kid}\"}}");
        await Assert.That(await ValidateAsync(server, "ins_a", $"{none}.{parts[1]}.")).IsEqualTo(HttpStatusCode.Unauthorized);

        // HS256 keyed with the public JWK (the classic algorithm-confusion forgery) is rejected.
        var jwk = (await server.Client.GetFromJsonAsync<JsonElement>("/__test/instances/ins_a/jwks")).GetProperty("keys")[0].GetRawText();
        var hsHeader = Base64UrlEncoder.Encode($"{{\"alg\":\"HS256\",\"typ\":\"{AccessType}\",\"kid\":\"{kid}\"}}");
        var hsSignature = Base64UrlEncoder.Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwk), Encoding.ASCII.GetBytes($"{hsHeader}.{parts[1]}")));
        await Assert.That(await ValidateAsync(server, "ins_a", $"{hsHeader}.{parts[1]}.{hsSignature}")).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Correctly_signed_tokens_still_need_a_ring_kid_and_es256()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = RandomNumberGenerator.GetBytes(32);
        await using var server = await StartAsync(target, Convert.ToBase64String(masterKey));
        var payload = (await SignAsync(server, "ins_a", AccessType)).Split('.')[1];

        // The test signs with the instance's own private key, so only the header differs.
        var record = (await StoredAsync(target, "ins_a")).Single();
        var kid = record["id"]!.GetValue<string>();
        using var key = Es256Key.Import(kid, SecretBox.Open(masterKey, record["secretBox"]!.GetValue<string>(), $"signing-key:{kid}:ins_a"));
        string Sign(string header, HashAlgorithmName hash)
        {
            var input = $"{Base64UrlEncoder.Encode(header)}.{payload}";
            return $"{input}.{Base64UrlEncoder.Encode(key.SecurityKey.ECDsa.SignData(Encoding.ASCII.GetBytes(input), hash))}";
        }

        await Assert.That(await ValidateAsync(server, "ins_a", Sign($"{{\"alg\":\"ES256\",\"typ\":\"{AccessType}\",\"kid\":\"{kid}\"}}", HashAlgorithmName.SHA256)))
            .IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await ValidateAsync(server, "ins_a", Sign($"{{\"alg\":\"ES256\",\"typ\":\"{AccessType}\",\"kid\":\"01M4EQ2FRJ8S8B29E8TQVWNW4N\"}}", HashAlgorithmName.SHA256)))
            .IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ValidateAsync(server, "ins_a", Sign($"{{\"alg\":\"ES256\",\"typ\":\"{AccessType}\"}}", HashAlgorithmName.SHA256)))
            .IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ValidateAsync(server, "ins_a", Sign($"{{\"alg\":\"ES384\",\"typ\":\"{AccessType}\",\"kid\":\"{kid}\"}}", HashAlgorithmName.SHA384)))
            .IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Audiences_match_exactly_without_trailing_slash_folding()
    {
        await using var server = await StartAsync(await SharedStateTarget.CreateAsync(fixture));

        var slashed = await SignAsync(server, "ins_a", AccessType, audience: Audience + "/");
        var plain = await SignAsync(server, "ins_a", AccessType);

        await Assert.That(await ValidateAsync(server, "ins_a", slashed)).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ValidateAsync(server, "ins_a", plain, audience: Audience + "/")).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ValidateAsync(server, "ins_a", slashed, audience: Audience + "/")).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(0, HttpStatusCode.BadRequest)]
    [Arguments(86_401, HttpStatusCode.BadRequest)]
    [Arguments(86_400, HttpStatusCode.OK)]
    public async Task Token_lifetimes_are_positive_and_at_most_a_day(int lifetimeSeconds, HttpStatusCode expected)
    {
        await using var server = await StartAsync(await SharedStateTarget.CreateAsync(fixture));

        using var response = await server.Client.PostAsJsonAsync(
            "/__test/instances/ins_a/tokens",
            new SignRequest(AccessType, new() { ["sub"] = "user_1" }, LifetimeSeconds: lifetimeSeconds));

        await Assert.That(response.StatusCode).IsEqualTo(expected);
    }

    [Test]
    public async Task Claims_cannot_override_issuer_audience_or_lifetime()
    {
        await using var server = await StartAsync(await SharedStateTarget.CreateAsync(fixture));

        foreach (var reserved in new[] { "iss", "aud", "exp", "nbf", "iat" })
        {
            using var response = await server.Client.PostAsJsonAsync(
                "/__test/instances/ins_a/tokens",
                new SignRequest(AccessType, new Dictionary<string, string> { ["sub"] = "user_1", [reserved] = "9999999999" }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
    }

    [Test]
    public async Task Public_key_objects_cannot_export_a_secret_or_sign()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target);
        await SignAsync(server, "ins_a", AccessType);
        var keys = await server.App.Services.GetRequiredService<KeyRing>().GetPublicAsync("ins_a", CancellationToken.None);
        try
        {
            await Assert.That(() => keys[0].SecurityKey.ECDsa.ExportParameters(true)).Throws<CryptographicException>();
            await Assert.That(() => keys[0].SecurityKey.ECDsa.SignData([1, 2, 3], HashAlgorithmName.SHA256)).Throws<CryptographicException>();
        }
        finally
        {
            keys.ToList().ForEach(key => key.Dispose());
        }
    }

    [Test]
    public async Task Unreachable_storage_fails_signing_closed_with_503()
    {
        await using var server = await StartAsync(target: null);

        using var response = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("temporarily_unavailable");
    }

    // The instance's stored key documents, read directly from TerraBase.
    private static async Task<List<JsonObject>> StoredAsync(SharedStateTarget target, string instance) =>
        [.. (await target.Database.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items
            .Select(document => JsonNode.Parse(document.Document.GetRawText())!.AsObject())
            .Where(document => document["instanceId"]!.GetValue<string>() == instance)];

    private static string KeyId(string token) =>
        JsonDocument.Parse(Base64UrlEncoder.Decode(token.Split('.')[0])).RootElement.GetProperty("kid").GetString()!;

    private static async Task<List<string>> KeyIdsAsync(AuthTestServer server, string instance = "ins_a") =>
        [.. (await server.Client.GetFromJsonAsync<JsonElement>($"/__test/instances/{instance}/jwks")).GetProperty("keys").EnumerateArray().Select(key => key.GetProperty("kid").GetString()!)];

    private static async Task<string> RotateAsync(AuthTestServer server)
    {
        using var response = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kid").GetString()!;
    }

    private static async Task<bool> RotateIfDueAsync(AuthTestServer server)
    {
        using var response = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate-if-due", null);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rotated").GetBoolean();
    }

    private static async Task<string> SignAsync(AuthTestServer server, string instance, string type, int ageSeconds = 0, string? audience = null)
    {
        using var response = await server.Client.PostAsJsonAsync(
            $"/__test/instances/{instance}/tokens",
            new SignRequest(type, new() { ["sub"] = "user_1" }, ageSeconds, audience));
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static async Task<HttpStatusCode> ValidateAsync(AuthTestServer server, string instance, string token, string audience = Audience)
    {
        using var response = await server.Client.PostAsJsonAsync($"/__test/instances/{instance}/tokens/validate", new ValidateRequest(token, AccessType, audience));
        return response.StatusCode;
    }

    private sealed record SignRequest(string Type, Dictionary<string, string> Claims, int AgeSeconds = 0, string? Audience = null, int LifetimeSeconds = 600, bool ReplicaClock = false);

    private sealed record ValidateRequest(string Token, string Type, string Audience);
}
