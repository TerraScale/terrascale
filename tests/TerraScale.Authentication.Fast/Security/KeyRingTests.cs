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

namespace TerraScale.Authentication.Fast.Security;

/// <summary>
/// Per-instance ES256 signing keys: sealed storage, JWT signing and validation, rotation seen by
/// every replica, and tenant isolation. Test routes sign and validate through the key ring.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class KeyRingTests(TerraBaseFixture fixture)
{
    private const string Audience = "test-audience";
    private const string AccessType = "at+jwt";

    // Binds to the real TerraBase target and its vault; without one the server's storage and vault are unreachable.
    // The clock is this replica's and, unless vaultClock is false, the vault's too.
    private static Task<AuthTestServer> StartAsync(SharedStateTarget? target, string? masterKey = null, TimeProvider? clock = null, TimeSpan? refresh = null, bool vaultClock = true) => AuthTestServer.StartAsync(
        settings: values =>
        {
            target?.Bind(values);
            if (target is not null && clock is not null && vaultClock)
            {
                target.Vault.Clock = clock;
            }

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
            app.MapPost("/__test/instances/{instance}/keys/prune", async (string instance, ISigningKeyVault vault, CancellationToken cancellationToken) =>
                new { scheduled = await vault.PruneAsync(instance, cancellationToken) });
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

        // Storage holds the private key only as a v1 box under TS_AUTH_MASTER_KEY, bound to its kid and instance.
        var stored = JsonNode.Parse(target.Vault.Read("ins_a")!.Value.GetRawText())!["keys"]!.AsArray().Single()!;
        var boxed = stored["secretBox"]!.GetValue<string>();
        var secret = SecretBox.Open(masterKey, boxed, $"signing-key:{kid}:ins_a");
        await Assert.That(stored["algorithm"]!.GetValue<string>()).IsEqualTo("ES256");
        await Assert.That(boxed).StartsWith("v1:");
        await Assert.That(secret.Length).IsEqualTo(96);
        await Assert.That(Base64UrlEncoder.Encode(secret[32..64])).IsEqualTo(key.GetProperty("x").GetString());
        await Assert.That(Base64UrlEncoder.Encode(secret[64..])).IsEqualTo(key.GetProperty("y").GetString());
        await Assert.That(target.Vault.Read("ins_a")!.Value.GetRawText()).DoesNotContain(Convert.ToBase64String(secret[..32]));
        await Assert.That(() => SecretBox.Open(masterKey, boxed, $"signing-key:{kid}:ins_b")).Throws<CryptographicException>();
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
        var record = JsonNode.Parse(target.Vault.Read("ins_a")!.Value.GetRawText())!["keys"]![0]!;
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

        await Assert.That(string.Join(',', afterThird)).IsEqualTo(string.Join(',', third, second, first));
        await Assert.That(string.Join(',', afterFourth)).IsEqualTo(string.Join(',', fourth, third, second));
    }

    [Test]
    public async Task Scheduled_rotation_publishes_the_next_key_before_it_signs_and_retires_the_old_one_later()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var server = await StartAsync(target, masterKey, clock);
        await using var replica = await StartAsync(target, masterKey, clock);
        var first = (await KeyIdsAsync(server)).Single();

        await Assert.That(await RotateIfDueAsync(server)).IsFalse();
        clock.Advance(KeyRing.RotationAge);
        // Both replicas run the schedule at once; the conditional write lets only one add a key.
        var rotated = await Task.WhenAll(RotateIfDueAsync(server), RotateIfDueAsync(replica));
        await Assert.That(rotated.Count(added => added)).IsEqualTo(1);
        var published = await KeyIdsAsync(replica);
        await Assert.That(published.Count).IsEqualTo(2);
        await Assert.That(published[1]).IsEqualTo(first);
        var next = published[0];

        // Published but not yet signing: verifiers fetch it before any token needs it.
        var beforeActivation = await SignAsync(replica, "ins_a", AccessType);
        await Assert.That(KeyId(beforeActivation)).IsEqualTo(first);
        // Activation rounds up to the second.
        clock.Advance(KeyRing.Overlap + TimeSpan.FromSeconds(1));
        var afterActivation = await SignAsync(server, "ins_a", AccessType);
        await Assert.That(KeyId(afterActivation)).IsEqualTo(next);
        await Assert.That(await ValidateAsync(replica, "ins_a", beforeActivation)).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await RotateIfDueAsync(server)).IsFalse();

        // The next scheduled rotation retires the first key, whose successor has signed for longer than the overlap.
        clock.Advance(KeyRing.RotationAge);
        await Assert.That(await RotateIfDueAsync(replica)).IsTrue();
        var later = await KeyIdsAsync(server);
        await Assert.That(later.Count).IsEqualTo(2);
        await Assert.That(later[1]).IsEqualTo(next);
        await Assert.That(later).DoesNotContain(first);
        await Assert.That(await ValidateAsync(server, "ins_a", beforeActivation)).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ValidateAsync(server, "ins_a", afterActivation)).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Rotation_at_capacity_is_refused_unchanged_until_superseded_keys_pass_the_overlap()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var server = await StartAsync(target, clock: clock);
        await KeyIdsAsync(server);
        for (var rotation = 1; rotation < KeyRing.MaxKeys; rotation++)
        {
            await RotateAsync(server);
        }

        var token = await SignAsync(server, "ins_a", AccessType);
        var before = target.Vault.Read("ins_a")!.Value.GetRawText();
        using var refused = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        var held = await KeyIdsAsync(server);

        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("signing_key_limit");
        await Assert.That(held.Count).IsEqualTo(KeyRing.MaxKeys);
        await Assert.That(target.Vault.Read("ins_a")!.Value.GetRawText()).IsEqualTo(before);
        await Assert.That(await ValidateAsync(server, "ins_a", token)).IsEqualTo(HttpStatusCode.OK);

        // Every key but the signing one has been superseded for the overlap: only the signing key stays.
        clock.Advance(KeyRing.Overlap);
        var added = await RotateAsync(server);
        await Assert.That(string.Join(',', await KeyIdsAsync(server))).IsEqualTo(string.Join(',', added, held[0]));
        await Assert.That(await ValidateAsync(server, "ins_a", token)).IsEqualTo(HttpStatusCode.OK);
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
    public async Task Rotation_signs_with_a_new_key_that_every_replica_sees_while_old_tokens_stay_valid()
    {
        // Replicas share the storage authority and the master key.
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var first = await StartAsync(target, masterKey);
        await using var replica = await StartAsync(target, masterKey);

        var before = await SignAsync(first, "ins_a", AccessType);
        using var rotation = await first.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);
        var rotated = (await rotation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kid").GetString();
        var after = await SignAsync(replica, "ins_a", AccessType);
        var jwks = (await replica.Client.GetFromJsonAsync<JsonElement>("/__test/instances/ins_a/jwks")).GetProperty("keys");

        await Assert.That(KeyId(after)).IsEqualTo(rotated);
        await Assert.That(KeyId(before)).IsNotEqualTo(rotated);
        await Assert.That(jwks.GetArrayLength()).IsEqualTo(2);
        await Assert.That(jwks[0].GetProperty("kid").GetString()).IsEqualTo(rotated);
        await Assert.That(jwks[1].GetProperty("kid").GetString()).IsEqualTo(KeyId(before));
        await Assert.That(await ValidateAsync(replica, "ins_a", before)).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await ValidateAsync(first, "ins_a", after)).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_key_ring_copied_to_another_instance_is_never_used()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target);
        var token = await SignAsync(server, "ins_a", AccessType);

        // Storage-level tampering: instance B's ring replaced by a copy of A's.
        target.Vault.Put("ins_b", target.Vault.Read("ins_a")!.Value);
        using var sign = await server.Client.PostAsJsonAsync("/__test/instances/ins_b/tokens", new SignRequest(AccessType, new() { ["sub"] = "user_1" }));
        using var validate = await server.Client.PostAsJsonAsync("/__test/instances/ins_b/tokens/validate", new ValidateRequest(token, AccessType, Audience));

        await Assert.That(sign.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(validate.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Public_metadata_cannot_be_rebound_to_another_key_or_instance(bool rebindTenant)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target);
        var token = await SignAsync(server, "ins_a", AccessType);
        var original = target.Vault.Read("ins_a")!.Value;
        var ring = JsonNode.Parse(original.GetRawText())!;
        var instance = rebindTenant ? "ins_b" : "ins_a";
        if (rebindTenant)
        {
            ring["instanceId"] = instance;
        }
        else
        {
            ring["keys"]![0]!["id"] = AuthStore.NewId();
        }

        // A storage writer can change ownership or kid, but cannot replace the original MAC.
        target.Vault.Put(instance, JsonSerializer.SerializeToElement(ring));
        var id = instance;
        var before = target.Vault.Current(id);

        using var lookup = await server.Client.GetAsync($"/__test/instances/{instance}/jwks");
        await Assert.That(lookup.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(await ValidateAsync(server, instance, token)).IsEqualTo(HttpStatusCode.InternalServerError);
        using var signing = await server.Client.PostAsJsonAsync($"/__test/instances/{instance}/tokens", new SignRequest(AccessType, new() { ["sub"] = "user_1" }));
        await Assert.That(signing.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        using var rotation = await server.Client.PostAsync($"/__test/instances/{instance}/keys/rotate", null);
        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        var after = target.Vault.Current(id);
        await Assert.That(after.Version).IsEqualTo(before.Version);
        await Assert.That(after.Document.GetRawText()).IsEqualTo(before.Document.GetRawText());
        if (rebindTenant)
        {
            await Assert.That(await ValidateAsync(server, "ins_a", token)).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(target.Vault.Read("ins_a")!.Value.GetRawText()).IsEqualTo(original.GetRawText());
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
        var ring = JsonNode.Parse(target.Vault.Read("ins_a")!.Value.GetRawText())!;
        var record = ring["keys"]![0]!;
        var kid = record["id"]!.GetValue<string>();
        var secret = SecretBox.Open(masterKey, record["secretBox"]!.GetValue<string>(), $"signing-key:{kid}:ins_a");
        secret[80] ^= 1;
        record["secretBox"] = SecretBox.Seal(masterKey, secret, $"signing-key:{kid}:ins_a");
        target.Vault.Put("ins_a", JsonSerializer.SerializeToElement(ring));

        using var response = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var signing = await server.Client.PostAsJsonAsync("/__test/instances/ins_a/tokens", new SignRequest(AccessType, new() { ["sub"] = "user_1" }));
        await Assert.That(signing.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
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

    private static string KeyId(string token) =>
        JsonDocument.Parse(Base64UrlEncoder.Decode(token.Split('.')[0])).RootElement.GetProperty("kid").GetString()!;

    private static async Task<List<string>> KeyIdsAsync(AuthTestServer server) =>
        [.. (await server.Client.GetFromJsonAsync<JsonElement>("/__test/instances/ins_a/jwks")).GetProperty("keys").EnumerateArray().Select(key => key.GetProperty("kid").GetString()!)];

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
