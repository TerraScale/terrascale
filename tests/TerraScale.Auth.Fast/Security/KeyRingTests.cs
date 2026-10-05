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
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Features.SigningKeys;
using TerraScale.Auth.Security;
using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Fast.Security;

/// <summary>
/// Per-instance ES256 signing keys: sealed storage, JWT signing and validation, rotation seen by
/// every replica, and tenant isolation. Test routes sign and validate through the key ring.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class KeyRingTests(TerraBaseFixture fixture)
{
    private const string Audience = "test-audience";
    private const string AccessType = "at+jwt";

    // Binds to the real TerraBase target; without one the server's storage is unreachable.
    // failing makes that store operation throw, as an outage would.
    private static Task<AuthTestServer> StartAsync(SharedStateTarget? target, string? masterKey = null, TimeProvider? clock = null, string? failing = null) => AuthTestServer.StartAsync(
        settings: values =>
        {
            target?.Bind(values);
            if (masterKey is not null)
            {
                values["master-key"] = masterKey;
            }
        },
        services: services =>
        {
            target?.Register(services);
            if (failing is not null)
            {
                services.AddSingleton<IDocumentStore>(provider => new FailingDocumentStore(new TerraBaseDocumentStore(provider.GetRequiredService<AuthStore>()), failing));
            }

            if (clock is not null)
            {
                services.AddSingleton(clock);
            }
        },
        routes: app =>
        {
            app.MapPost("/__test/instances/{instance}/tokens", async (string instance, SignRequest request, KeyRing ring, AuthOptions options, CancellationToken cancellationToken) =>
            {
                var keys = await ring.GetActiveAsync(instance, cancellationToken);
                try
                {
                    var claims = request.Claims.ToDictionary(pair => pair.Key, pair => (object)pair.Value);
                    var issuedAt = DateTimeOffset.UtcNow.AddSeconds(-request.AgeSeconds);
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
                var keys = await ring.GetActiveAsync(instance, cancellationToken);
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
            app.MapGet("/__test/instances/{instance}/jwks", async (string instance, KeyRing ring, CancellationToken cancellationToken) =>
            {
                var keys = await ring.GetActiveAsync(instance, cancellationToken);
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
        await Assert.That(kid).Matches(@"\Askey_[A-Za-z0-9]{40}\z");
        await Assert.That(key.GetProperty("kty").GetString()).IsEqualTo("EC");
        await Assert.That(key.GetProperty("crv").GetString()).IsEqualTo("P-256");
        await Assert.That(key.GetProperty("alg").GetString()).IsEqualTo("ES256");
        await Assert.That(key.GetProperty("use").GetString()).IsEqualTo("sig");
        await Assert.That(key.GetProperty("d").ValueKind).IsEqualTo(JsonValueKind.Null);

        // Storage holds the private key only as a v1 box under TS_AUTH_MASTER_KEY, bound to its kid and instance.
        var stored = JsonNode.Parse((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText())!["keys"]!.AsArray().Single()!;
        var boxed = stored["secretBox"]!.GetValue<string>();
        var secret = SecretBox.Open(masterKey, boxed, $"signing-key:{kid}:ins_a");
        await Assert.That(stored["algorithm"]!.GetValue<string>()).IsEqualTo("ES256");
        await Assert.That(boxed).StartsWith("v1:");
        await Assert.That(secret.Length).IsEqualTo(96);
        await Assert.That(Base64UrlEncoder.Encode(secret[32..64])).IsEqualTo(key.GetProperty("x").GetString());
        await Assert.That(Base64UrlEncoder.Encode(secret[64..])).IsEqualTo(key.GetProperty("y").GetString());
        await Assert.That((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText()).DoesNotContain(Convert.ToBase64String(secret[..32]));
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
        var record = JsonNode.Parse((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText())!["keys"]![0]!;
        var kid = record["id"]!.GetValue<string>();
        using var key = Es256Key.Import(kid, SecretBox.Open(masterKey, record["secretBox"]!.GetValue<string>(), $"signing-key:{kid}:ins_a"));
        string Sign(string header, HashAlgorithmName hash)
        {
            var input = $"{Base64UrlEncoder.Encode(header)}.{payload}";
            return $"{input}.{Base64UrlEncoder.Encode(key.SecurityKey.ECDsa.SignData(Encoding.ASCII.GetBytes(input), hash))}";
        }

        await Assert.That(await ValidateAsync(server, "ins_a", Sign($"{{\"alg\":\"ES256\",\"typ\":\"{AccessType}\",\"kid\":\"{kid}\"}}", HashAlgorithmName.SHA256)))
            .IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await ValidateAsync(server, "ins_a", Sign($"{{\"alg\":\"ES256\",\"typ\":\"{AccessType}\",\"kid\":\"skey_unknown\"}}", HashAlgorithmName.SHA256)))
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
    public async Task Rotation_never_retires_keys_based_on_elapsed_replica_wall_time()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var server = await StartAsync(await SharedStateTarget.CreateAsync(fixture), clock: clock);
        var first = await KeyIdsAsync(server);
        var second = await RotateAsync(server);

        // Replica wall time cannot prove supersession age, even beyond the former retention.
        clock.Advance(TimeSpan.FromHours(47));
        var third = await RotateAsync(server);
        var afterThird = await KeyIdsAsync(server);

        clock.Advance(TimeSpan.FromHours(2));
        var fourth = await RotateAsync(server);
        var afterFourth = await KeyIdsAsync(server);

        await Assert.That(string.Join(',', afterThird)).IsEqualTo(string.Join(',', third, second, first.Single()));
        await Assert.That(string.Join(',', afterFourth)).IsEqualTo(string.Join(',', fourth, third, second, first.Single()));
    }

    [Test]
    public async Task Rotation_at_capacity_stays_refused_after_local_time_advances_without_changing_the_ring()
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
        var before = (await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText();
        using var refused = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        var held = await KeyIdsAsync(server);

        clock.Advance(TimeSpan.FromDays(365));
        using var stillRefused = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);

        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("signing_key_limit");
        await Assert.That(held.Count).IsEqualTo(KeyRing.MaxKeys);
        await Assert.That(stillRefused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText()).IsEqualTo(before);
        await Assert.That(await ValidateAsync(server, "ins_a", token)).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_oversized_stored_ring_is_refused_on_lookup_and_rotation_without_pruning(bool ancient)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target);
        await KeyIdsAsync(server);

        var ring = JsonNode.Parse((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText())!;
        var record = ring["keys"]![0]!;
        var start = DateTimeOffset.UtcNow.AddHours(ancient ? -72 : 0).ToUnixTimeSeconds();
        ring["keys"] = new JsonArray([.. Enumerable.Range(0, KeyRing.MaxKeys + 1).Select(index =>
        {
            var copy = record.DeepClone();
            copy["createdAt"] = start + index;
            return copy;
        })]);
        await target.PutAsync(KeyRing.Collection, "ins_a", "ring", JsonSerializer.SerializeToElement(ring));
        var before = (await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText();

        using var response = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText()).IsEqualTo(before);
        using var rotation = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);

        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText()).IsEqualTo(before);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    public async Task A_ring_without_strict_creation_order_fails_lookup_and_rotation_unchanged(int successorOffset)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target);
        var token = await SignAsync(server, "ins_a", AccessType);
        await RotateAsync(server);

        var ring = JsonNode.Parse((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText())!;
        // An ancient malformed successor would previously prune the live token's key.
        var createdAt = DateTimeOffset.UtcNow.AddHours(-72).ToUnixTimeSeconds();
        ring["keys"]![0]!["createdAt"] = createdAt;
        ring["keys"]![1]!["createdAt"] = createdAt + successorOffset;
        await target.PutAsync(KeyRing.Collection, "ins_a", "ring", JsonSerializer.SerializeToElement(ring));
        var before = (await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText();

        using var lookup = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        await Assert.That(lookup.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(await ValidateAsync(server, "ins_a", token)).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText()).IsEqualTo(before);
        using var rotation = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);
        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText()).IsEqualTo(before);
    }

    [Test]
    [Arguments(long.MinValue, HttpStatusCode.InternalServerError)]
    [Arguments(long.MaxValue, HttpStatusCode.InternalServerError)]
    [Arguments(-62_135_596_801L, HttpStatusCode.InternalServerError)]
    [Arguments(253_402_300_800L, HttpStatusCode.InternalServerError)]
    [Arguments(253_402_300_799L, HttpStatusCode.OK)]
    public async Task Invalid_or_exhausted_unix_timestamps_never_produce_a_rotation(long createdAt, HttpStatusCode lookupStatus)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target);
        await KeyIdsAsync(server);

        var ring = JsonNode.Parse((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText())!;
        ring["keys"]![0]!["createdAt"] = createdAt;
        await target.PutAsync(KeyRing.Collection, "ins_a", "ring", JsonSerializer.SerializeToElement(ring));
        var before = (await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText();

        using var lookup = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        await Assert.That(lookup.StatusCode).IsEqualTo(lookupStatus);
        using var rotation = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);
        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText()).IsEqualTo(before);
    }

    [Test]
    public async Task An_old_active_key_keeps_its_fresh_token_valid_after_slow_then_accurate_rotations()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        var accurateClock = new ManualClock(now.AddHours(-72));
        await using var accurate = await StartAsync(target, masterKey, accurateClock);
        await using var slow = await StartAsync(target, masterKey, new ManualClock(now.AddHours(-49)));

        var first = (await KeyIdsAsync(accurate)).Single();
        accurateClock.Advance(TimeSpan.FromHours(72));
        var token = await SignAsync(accurate, "ins_a", AccessType);
        await Assert.That(KeyId(token)).IsEqualTo(first);
        await Assert.That(await ValidateAsync(accurate, "ins_a", token)).IsEqualTo(HttpStatusCode.OK);
        var second = await RotateAsync(slow);
        var third = await RotateAsync(accurate);

        await Assert.That(string.Join(',', await KeyIdsAsync(accurate))).IsEqualTo(string.Join(',', third, second, first));
        await Assert.That(await ValidateAsync(accurate, "ins_a", token)).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await ValidateAsync(slow, "ins_a", token)).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Rotation_never_commits_a_ring_with_an_unreadable_key()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddHours(-72));
        await using var server = await StartAsync(target, clock: clock);
        await KeyIdsAsync(server);
        await RotateAsync(server);
        clock.Advance(TimeSpan.FromHours(72));

        // The oldest inactive key looks past retention but is unreadable: never prune it away
        // before validating the complete original ring.
        var ring = JsonNode.Parse((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText())!;
        var record = ring["keys"]![0]!;
        record["secretBox"] = SecretBox.Seal(RandomNumberGenerator.GetBytes(32), new byte[96], $"signing-key:{record["id"]!.GetValue<string>()}:ins_a");
        await target.PutAsync(KeyRing.Collection, "ins_a", "ring", JsonSerializer.SerializeToElement(ring));
        var corrupted = (await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText();

        using var lookup = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        await Assert.That(lookup.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        using var rotation = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);

        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText()).IsEqualTo(corrupted);
    }

    [Test]
    [Arguments("get", HttpStatusCode.ServiceUnavailable)]
    [Arguments("create", HttpStatusCode.ServiceUnavailable)]
    [Arguments("replace", HttpStatusCode.OK)]
    public async Task Storage_failures_fail_closed_with_503(string failing, HttpStatusCode firstUse)
    {
        await using var server = await StartAsync(await SharedStateTarget.CreateAsync(fixture), failing: failing);

        using var lookup = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        using var rotation = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);
        var body = await rotation.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(lookup.StatusCode).IsEqualTo(firstUse);
        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("temporarily_unavailable");
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
        await target.PutAsync(KeyRing.Collection, "ins_b", "ring", (await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value);
        using var sign = await server.Client.PostAsJsonAsync("/__test/instances/ins_b/tokens", new SignRequest(AccessType, new() { ["sub"] = "user_1" }));
        using var validate = await server.Client.PostAsJsonAsync("/__test/instances/ins_b/tokens/validate", new ValidateRequest(token, AccessType, Audience));

        await Assert.That(sign.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(validate.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task A_stored_key_whose_public_point_does_not_match_its_private_key_is_rejected()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = RandomNumberGenerator.GetBytes(32);
        await using var server = await StartAsync(target, Convert.ToBase64String(masterKey));
        await SignAsync(server, "ins_a", AccessType);

        // Correctly sealed under the master key, but with one bit of Y flipped.
        var ring = JsonNode.Parse((await target.ReadAsync(KeyRing.Collection, "ins_a", "ring"))!.Value.GetRawText())!;
        var record = ring["keys"]![0]!;
        var kid = record["id"]!.GetValue<string>();
        var secret = SecretBox.Open(masterKey, record["secretBox"]!.GetValue<string>(), $"signing-key:{kid}:ins_a");
        secret[80] ^= 1;
        record["secretBox"] = SecretBox.Seal(masterKey, secret, $"signing-key:{kid}:ins_a");
        await target.PutAsync(KeyRing.Collection, "ins_a", "ring", JsonSerializer.SerializeToElement(ring));

        using var response = await server.Client.GetAsync("/__test/instances/ins_a/jwks");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
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

    private sealed record SignRequest(string Type, Dictionary<string, string> Claims, int AgeSeconds = 0, string? Audience = null, int LifetimeSeconds = 600);

    private sealed record ValidateRequest(string Token, string Type, string Audience);
}
