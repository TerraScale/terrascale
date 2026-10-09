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
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.SigningKeys;
using TerraScale.Auth.Security;
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
        await Assert.That(kid).Matches(@"\Askey_[A-Za-z0-9]{40}\z");
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
    public async Task A_replica_serves_its_last_vault_read_until_the_refresh_interval_passes()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var cached = await StartAsync(target, masterKey, clock, refresh: TimeSpan.FromMinutes(1));
        await using var writer = await StartAsync(target, masterKey, clock);
        var first = (await KeyIdsAsync(cached)).Single();
        var rotated = await RotateAsync(writer);

        clock.Advance(TimeSpan.FromSeconds(59));
        await Assert.That(string.Join(',', await KeyIdsAsync(cached))).IsEqualTo(first);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(string.Join(',', await KeyIdsAsync(cached))).IsEqualTo(string.Join(',', rotated, first));
        // Wall-clock steps neither extend nor expire a read; only elapsed time does.
        await RotateAsync(writer);
        clock.Step(TimeSpan.FromMinutes(-5));
        await Assert.That((await KeyIdsAsync(cached)).Count).IsEqualTo(2);
        clock.Step(TimeSpan.FromDays(3));
        await Assert.That((await KeyIdsAsync(cached)).Count).IsEqualTo(2);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That((await KeyIdsAsync(cached)).Count).IsEqualTo(3);
    }

    [Test]
    public async Task A_wall_clock_step_after_a_read_never_activates_a_pending_key()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var vault = new ManualClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        target.Vault.Clock = vault;
        await using var scheduler = await StartAsync(target, masterKey, vault);
        var replica = new SteppingClock(vault.GetUtcNow());
        await using var stepped = await StartAsync(target, masterKey, replica, refresh: TimeSpan.FromMinutes(1), vaultClock: false);
        var first = (await KeyIdsAsync(scheduler)).Single();
        vault.Advance(KeyRing.RotationAge);
        replica.Advance(KeyRing.RotationAge);
        await Assert.That(await RotateIfDueAsync(scheduler)).IsTrue();
        await Assert.That(KeyId(await SignAsync(stepped, "ins_a", AccessType))).IsEqualTo(first);

        // Three days later by its own wall clock, but no time has passed: it refuses to sign rather than let the next
        // key sign early, and signs with the first key again once its clock is back.
        replica.Step(TimeSpan.FromDays(3));
        await Assert.That(await SignStatusAsync(stepped, "ins_a")).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        replica.Step(TimeSpan.FromDays(-3));
        await Assert.That(KeyId(await SignAsync(stepped, "ins_a", AccessType))).IsEqualTo(first);
    }

    [Test]
    public async Task An_unscheduled_rotation_signs_only_once_every_replica_can_verify_it()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var writer = await StartAsync(target, masterKey, clock, refresh: TimeSpan.FromMinutes(1));
        await using var verifier = await StartAsync(target, masterKey, clock, refresh: TimeSpan.FromMinutes(1));
        var before = await SignAsync(writer, "ins_a", AccessType);
        await Assert.That(await ValidateAsync(verifier, "ins_a", before)).IsEqualTo(HttpStatusCode.OK);

        var rotated = await RotateAsync(writer);
        var during = await SignAsync(writer, "ins_a", AccessType);
        await Assert.That(KeyId(during)).IsEqualTo(KeyId(before));
        await Assert.That(await ValidateAsync(verifier, "ins_a", during)).IsEqualTo(HttpStatusCode.OK);

        clock.Advance(TimeSpan.FromMinutes(1) + KeyRing.ActivationMargin);
        var after = await SignAsync(writer, "ins_a", AccessType);
        await Assert.That(KeyId(after)).IsEqualTo(rotated);
        await Assert.That(await ValidateAsync(verifier, "ins_a", after)).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_read_answered_after_a_rotation_never_replaces_the_newer_cached_ring()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var server = await StartAsync(target, clock: clock, refresh: TimeSpan.FromMinutes(1));
        var first = (await KeyIdsAsync(server)).Single();
        clock.Advance(TimeSpan.FromMinutes(1));

        var (reached, release) = target.Vault.StallNextCurrentRead("ins_a");
        var stale = KeyIdsAsync(server);
        await reached.WaitAsync(TimeSpan.FromSeconds(30));
        var rotated = await RotateAsync(server);
        release();

        // The delayed lookup answers with the newer ring this replica already holds.
        await Assert.That(string.Join(',', await stale)).IsEqualTo(string.Join(',', rotated, first));
        await Assert.That(string.Join(',', await KeyIdsAsync(server))).IsEqualTo(string.Join(',', rotated, first));
    }

    [Test]
    public async Task A_token_naming_a_key_missing_from_the_cached_ring_rereads_the_vault()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var writer = await StartAsync(target, masterKey, clock, refresh: TimeSpan.FromMinutes(1));
        // A replica refreshing less often than the writer assumes (two minutes against one).
        await using var verifier = await StartAsync(target, masterKey, clock, refresh: TimeSpan.FromMinutes(2));
        var before = await SignAsync(writer, "ins_a", AccessType);
        await Assert.That(await ValidateAsync(verifier, "ins_a", before)).IsEqualTo(HttpStatusCode.OK);

        // At second 65 the writer signs with the new key, which the verifier's cached ring still lacks.
        var rotated = await RotateAsync(writer);
        clock.Advance(TimeSpan.FromMinutes(1) + KeyRing.ActivationMargin);
        var after = await SignAsync(writer, "ins_a", AccessType);
        await Assert.That(KeyId(after)).IsEqualTo(rotated);
        await Assert.That(await ValidateAsync(verifier, "ins_a", after)).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_slow_write_never_lets_its_key_sign_before_replicas_holding_the_old_ring_refresh()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var writer = await StartAsync(target, masterKey, clock, refresh: TimeSpan.FromMinutes(1));
        await using var verifier = await StartAsync(target, masterKey, clock, refresh: TimeSpan.FromMinutes(1));
        var first = KeyId(await SignAsync(writer, "ins_a", AccessType));

        // The rotation's write takes nine seconds; at second 8 the verifier reads the old ring.
        var (reached, release) = target.Vault.StallNextWrite("ins_a");
        var rotation = RotateAsync(writer);
        await reached.WaitAsync(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(8));
        await Assert.That(await ValidateAsync(verifier, "ins_a", await SignAsync(writer, "ins_a", AccessType))).IsEqualTo(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromSeconds(1));
        release();
        var rotated = await rotation;

        // The verifier spends its unknown-key reread at second 60; at 65 its cached ring still lacks the new key, so
        // the writer keeps signing with the first one.
        clock.Advance(TimeSpan.FromSeconds(51));
        target.Vault.Failing = "read";
        await Assert.That(await ValidateAsync(verifier, "ins_a", UnknownKeyToken("skey_unknown"))).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        target.Vault.Failing = null;
        clock.Advance(TimeSpan.FromSeconds(5));
        var token = await SignAsync(writer, "ins_a", AccessType);
        await Assert.That(KeyId(token)).IsEqualTo(first);
        await Assert.That(await ValidateAsync(verifier, "ins_a", token)).IsEqualTo(HttpStatusCode.OK);

        // The new key signs a refresh interval plus the margin after the vault first served it (second 74).
        clock.Advance(TimeSpan.FromSeconds(9));
        var later = await SignAsync(writer, "ins_a", AccessType);
        await Assert.That(KeyId(later)).IsEqualTo(rotated);
        await Assert.That(await ValidateAsync(verifier, "ins_a", later)).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_slow_answer_carrying_the_old_ring_expires_before_the_new_key_signs()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var writer = await StartAsync(target, masterKey, clock, refresh: TimeSpan.FromMinutes(1));
        await using var verifier = await StartAsync(target, masterKey, clock, refresh: TimeSpan.FromMinutes(1));
        var before = await SignAsync(writer, "ins_a", AccessType);

        // The verifier asks for the ring just before the rotation is published, and the old ring reaches it nine
        // seconds later.
        var (reached, release) = target.Vault.StallNextCurrentRead("ins_a");
        var slow = ValidateAsync(verifier, "ins_a", before);
        await reached.WaitAsync(TimeSpan.FromSeconds(30));
        var rotated = await RotateAsync(writer);
        clock.Advance(TimeSpan.FromSeconds(9));
        release();
        await Assert.That(await slow).IsEqualTo(HttpStatusCode.OK);

        // It spends its unknown-key reread at second 59; at 65 the writer signs with the new key.
        clock.Advance(TimeSpan.FromSeconds(50));
        target.Vault.Failing = "read";
        await Assert.That(await ValidateAsync(verifier, "ins_a", UnknownKeyToken("skey_unknown"))).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        target.Vault.Failing = null;
        clock.Advance(TimeSpan.FromSeconds(6));
        var after = await SignAsync(writer, "ins_a", AccessType);
        await Assert.That(KeyId(after)).IsEqualTo(rotated);
        await Assert.That(await ValidateAsync(verifier, "ins_a", after)).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_refresh_interval_beyond_the_retention_bound_is_refused_at_startup()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => StartAsync(target, refresh: TimeSpan.FromDays(3)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => StartAsync(target, refresh: KeyRingRefresh.Max + TimeSpan.FromSeconds(1)));

        await using var bounded = await StartAsync(target, refresh: KeyRingRefresh.Max);
        await Assert.That(await KeyIdsAsync(bounded)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task A_key_whose_publication_cannot_be_confirmed_keeps_its_scheduled_activation()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var writer = await StartAsync(target, clock: clock, refresh: TimeSpan.FromMinutes(1));
        var first = KeyId(await SignAsync(writer, "ins_a", AccessType));

        // OCI keeps serving the previous version for as long as the rotation looks for its key.
        target.Vault.PublishDelayed = true;
        var rotated = await RotateAsync(writer);
        target.Vault.PublishDelayed = false;

        clock.Advance(TimeSpan.FromMinutes(2));
        await Assert.That(await KeyIdsAsync(writer)).Contains(rotated);
        await Assert.That(KeyId(await SignAsync(writer, "ins_a", AccessType))).IsEqualTo(first);
        clock.Advance(KeyRing.Overlap);
        await Assert.That(KeyId(await SignAsync(writer, "ins_a", AccessType))).IsEqualTo(rotated);
    }

    [Test]
    public async Task A_cancelled_lookup_leaves_its_shared_vault_read_for_the_next_one()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var server = await StartAsync(target, clock: clock, refresh: TimeSpan.FromMinutes(1));
        await KeyIdsAsync(server);
        clock.Advance(TimeSpan.FromMinutes(1));
        var ring = server.App.Services.GetRequiredService<KeyRing>();
        var reads = target.Vault.CurrentReads("ins_a");

        var (reached, release) = target.Vault.StallNextCurrentRead("ins_a");
        using var cancel = new CancellationTokenSource();
        var abandoned = ring.GetPublicAsync("ins_a", cancel.Token);
        await reached.WaitAsync(TimeSpan.FromSeconds(30));
        await cancel.CancelAsync();
        var cancelled = false;
        try
        {
            (await abandoned).ToList().ForEach(key => key.Dispose());
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        var joined = ring.GetPublicAsync("ins_a", CancellationToken.None);
        release();
        (await joined).ToList().ForEach(key => key.Dispose());

        await Assert.That(cancelled).IsTrue();
        await Assert.That(target.Vault.CurrentReads("ins_a")).IsEqualTo(reads + 1);
    }

    [Test]
    public async Task The_worker_prunes_a_full_secret_before_rotating()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var vault = new ManualClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        target.Vault.Clock = vault;
        await using (var first = await StartAsync(target, masterKey, vault))
        {
            await first.App.Services.GetRequiredService<SigningKeyRotation>().Bootstrapped.WaitAsync(TimeSpan.FromSeconds(30));
        }

        // A due rotation on a secret already at its version limit: {} and three versions of the ring.
        var ring = target.Vault.Read(OidcIssuer.InstanceId)!.Value;
        target.Vault.Put(OidcIssuer.InstanceId, ring);
        target.Vault.Put(OidcIssuer.InstanceId, ring);
        target.Vault.MaxVersions = 4;
        vault.Advance(KeyRing.RotationAge);

        await using var restarted = await StartAsync(target, masterKey, vault);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (target.Vault.Current(OidcIssuer.InstanceId).Version < 5 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        await Assert.That(string.Join(',', target.Vault.Scheduled(OidcIssuer.InstanceId))).IsEqualTo("1,2");
        await Assert.That(target.Vault.Current(OidcIssuer.InstanceId).Version).IsEqualTo(5);
        await Assert.That(target.Vault.Read(OidcIssuer.InstanceId)!.Value.GetProperty("keys").GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task Vault_outages_never_turn_lookups_or_unknown_keys_into_repeated_vault_reads()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var server = await StartAsync(target, clock: clock, refresh: TimeSpan.FromMinutes(1));
        await KeyIdsAsync(server);
        target.Vault.Failing = "read";
        clock.Advance(TimeSpan.FromSeconds(30));

        // Unauthenticated tokens naming arbitrary keys: one reread attempt, then the cached ring answers.
        var reads = target.Vault.CurrentReads("ins_a");
        var statuses = new List<HttpStatusCode>();
        for (var index = 0; index < 5; index++)
        {
            statuses.Add(await ValidateAsync(server, "ins_a", UnknownKeyToken($"skey_unknown{index}")));
        }

        await Assert.That(target.Vault.CurrentReads("ins_a")).IsEqualTo(reads + 1);
        await Assert.That(statuses[0]).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(statuses.Skip(1).All(status => status == HttpStatusCode.Unauthorized)).IsTrue();

        // Once the cache expires, a failed read holds lookups at 503 without asking the vault again for the backoff.
        clock.Advance(TimeSpan.FromSeconds(30));
        reads = target.Vault.CurrentReads("ins_a");
        for (var index = 0; index < 3; index++)
        {
            using var lookup = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
            await Assert.That(lookup.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }

        await Assert.That(target.Vault.CurrentReads("ins_a")).IsEqualTo(reads + 1);
        clock.Advance(KeyRing.FailedReadBackoff);
        using var retried = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        await Assert.That(retried.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(target.Vault.CurrentReads("ins_a")).IsEqualTo(reads + 2);
    }

    [Test]
    public async Task A_recreated_secret_replaces_the_cached_ring_although_its_versions_start_again()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var server = await StartAsync(target, clock: clock, refresh: TimeSpan.FromMinutes(1));
        await KeyIdsAsync(server);
        await RotateAsync(server);
        await RotateAsync(server);
        await RotateAsync(server);
        // {}, the first ring, then two versions per rotation: published, then its activation brought forward.
        await Assert.That(target.Vault.Current("ins_a").Version).IsEqualTo(8);

        // The operator recreates the secret holding the two oldest keys: version 1 of a new secret.
        var ring = JsonNode.Parse(target.Vault.Read("ins_a")!.Value.GetRawText())!;
        ring["keys"] = new JsonArray([.. ring["keys"]!.AsArray().Take(2).Select(key => key!.DeepClone())]);
        target.Vault.Recreate("ins_a", JsonSerializer.SerializeToElement(ring));
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That((await KeyIdsAsync(server)).Count).IsEqualTo(2);
        var reads = target.Vault.CurrentReads("ins_a");
        await Assert.That((await KeyIdsAsync(server)).Count).IsEqualTo(2);
        await Assert.That(target.Vault.CurrentReads("ins_a")).IsEqualTo(reads);
    }

    [Test]
    public async Task A_write_to_a_replaced_secret_never_displaces_the_recreated_secret_this_replica_read()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var server = await StartAsync(target, clock: clock, refresh: TimeSpan.FromMinutes(1));
        var first = (await KeyIdsAsync(server)).Single();

        // The rotation's write reaches the old secret after the operator recreated it and this replica read the new
        // one (a token naming an unknown key rereads at once), all within one refresh interval.
        var (reached, release) = target.Vault.StallNextWrite("ins_a");
        var rotation = RotateAsync(server);
        await reached.WaitAsync(TimeSpan.FromSeconds(30));
        target.Vault.Recreate("ins_a", target.Vault.Read("ins_a")!.Value);
        var reads = target.Vault.CurrentReads("ins_a");
        await Assert.That(await ValidateAsync(server, "ins_a", UnknownKeyToken("skey_unknown"))).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(target.Vault.CurrentReads("ins_a")).IsEqualTo(reads + 1);
        release();
        await rotation;

        await Assert.That(string.Join(',', await KeyIdsAsync(server))).IsEqualTo(first);
    }

    [Test]
    public async Task A_delayed_read_of_a_replaced_secret_never_displaces_the_recreated_one()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var server = await StartAsync(target, clock: clock, refresh: TimeSpan.FromMinutes(1));
        var first = (await KeyIdsAsync(server)).Single();
        clock.Advance(TimeSpan.FromMinutes(1));

        // A lookup's read of the old secret is answered only after the operator recreated it and a rotation on this
        // replica read the new secret and wrote to it.
        var (reached, release) = target.Vault.StallNextCurrentRead("ins_a");
        var stale = KeyIdsAsync(server);
        await reached.WaitAsync(TimeSpan.FromSeconds(30));
        target.Vault.Recreate("ins_a", target.Vault.Read("ins_a")!.Value);
        var rotated = await RotateAsync(server);
        release();

        await Assert.That(string.Join(',', await stale)).IsEqualTo(string.Join(',', rotated, first));
        await Assert.That(string.Join(',', await KeyIdsAsync(server))).IsEqualTo(string.Join(',', rotated, first));
    }

    [Test]
    public async Task A_damaged_ring_of_another_master_key_is_never_replaced_at_startup()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        // Another master key's ring whose only key record lost its sealed box, coordinates and timestamps.
        target.Vault.Put(OidcIssuer.InstanceId, JsonSerializer.SerializeToElement(new
        {
            instanceId = OidcIssuer.InstanceId,
            masterKeyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            keys = new[] { new { id = "skey_damaged", algorithm = "ES256" } },
        }));
        var before = target.Vault.Current(OidcIssuer.InstanceId);

        await using var misconfigured = await StartAsync(target);
        var bootstrap = misconfigured.App.Services.GetRequiredService<SigningKeyRotation>().Bootstrapped;
        await Task.WhenAny(bootstrap, Task.Delay(TimeSpan.FromSeconds(2)));

        await Assert.That(bootstrap.IsCompleted).IsFalse();
        await Assert.That(target.Vault.Current(OidcIssuer.InstanceId).Version).IsEqualTo(before.Version);
    }

    [Test]
    public async Task An_unscheduled_key_waits_out_the_whole_second_resolution_of_the_vault_clock()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var writer = await StartAsync(target, masterKey, clock, refresh: TimeSpan.FromMinutes(1));
        await using var signer = await StartAsync(target, masterKey, clock, refresh: TimeSpan.FromMinutes(1));
        await using var verifier = await StartAsync(target, masterKey, clock, refresh: TimeSpan.FromMinutes(1));
        var first = KeyId(await SignAsync(signer, "ins_a", AccessType));

        // The verifier reads the ring just before the writer rotates at 12:00:00.999, which the vault's Date header
        // reports as 12:00:00.
        clock.Advance(TimeSpan.FromMilliseconds(998));
        await Assert.That(await ValidateAsync(verifier, "ins_a", await SignAsync(signer, "ins_a", AccessType))).IsEqualTo(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await RotateAsync(writer);

        // The verifier spends its unknown-key reread five seconds before its cached ring is due for a refresh.
        clock.Advance(TimeSpan.FromMilliseconds(54_001));
        target.Vault.Failing = "read";
        await Assert.That(await ValidateAsync(verifier, "ins_a", UnknownKeyToken("skey_unknown"))).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        target.Vault.Failing = null;

        // At 12:01:00 the signer rereads the ring with an exact Date: its tokens still name a key the verifier holds.
        clock.Advance(TimeSpan.FromSeconds(5));
        var token = await SignAsync(signer, "ins_a", AccessType);
        await Assert.That(KeyId(token)).IsEqualTo(first);
        await Assert.That(await ValidateAsync(verifier, "ins_a", token)).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_delayed_signing_lookup_signs_with_the_recreated_secret_this_replica_already_read()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var server = await StartAsync(target, clock: clock, refresh: TimeSpan.FromMinutes(1));
        await KeyIdsAsync(server);
        clock.Advance(TimeSpan.FromMinutes(1));

        // A signing lookup's read of the old secret is answered after the operator recreated it empty and a rotation
        // on this replica wrote the replacement's first key.
        var (reached, release) = target.Vault.StallNextCurrentRead("ins_a");
        var signing = SignAsync(server, "ins_a", AccessType);
        await reached.WaitAsync(TimeSpan.FromSeconds(30));
        target.Vault.Recreate("ins_a", JsonDocument.Parse("{}").RootElement);
        var replacement = await RotateAsync(server);
        release();

        await Assert.That(KeyId(await signing)).IsEqualTo(replacement);
    }

    [Test]
    public async Task A_replica_signs_only_while_its_clock_agrees_with_the_vault_so_tokens_expire_before_their_key_retires()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var vault = new ManualClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        target.Vault.Clock = vault;
        // As far ahead of the vault as the bound allows.
        var replica = new SteppingClock(vault.GetUtcNow() + KeyRing.MaxClockSkew - TimeSpan.FromSeconds(1));
        await using var server = await StartAsync(target, clock: replica, refresh: TimeSpan.FromMinutes(1), vaultClock: false);
        void Advance(TimeSpan by)
        {
            vault.Advance(by);
            replica.Advance(by);
        }

        var first = (await KeyIdsAsync(server)).Single();
        var second = await RotateAsync(server);

        // The first key's last token, a second before its successor signs, for the longest lifetime, by the replica's clock.
        Advance(TimeSpan.FromMinutes(1) + KeyRing.ActivationMargin - TimeSpan.FromSeconds(1));
        using var response = await server.Client.PostAsJsonAsync("/__test/instances/ins_a/tokens",
            new SignRequest(AccessType, new() { ["sub"] = "user_1" }, LifetimeSeconds: (int)Jwt.MaxLifetime.TotalSeconds, ReplicaClock: true));
        var last = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        await Assert.That(KeyId(last)).IsEqualTo(first);
        var expires = DateTimeOffset.FromUnixTimeSeconds(JsonDocument.Parse(Base64UrlEncoder.Decode(last.Split('.')[1])).RootElement.GetProperty("exp").GetInt64());

        // The first key retires once its successor has signed for the overlap: its last token has expired by then.
        Advance(TimeSpan.FromSeconds(1) + KeyRing.Overlap);
        await RotateAsync(server);
        await Assert.That(await KeyIdsAsync(server)).DoesNotContain(first);
        await Assert.That(await KeyIdsAsync(server)).Contains(second);
        await Assert.That(expires).IsLessThan(vault.GetUtcNow());

        // Further ahead it refuses to sign and is not ready; verification keeps working.
        replica.Step(TimeSpan.FromSeconds(2));
        await Assert.That(await SignStatusAsync(server, "ins_a")).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var status = HttpStatusCode.OK;
        for (var deadline = DateTimeOffset.UtcNow.AddSeconds(10); status == HttpStatusCode.OK && DateTimeOffset.UtcNow < deadline; await Task.Delay(TimeSpan.FromMilliseconds(200)))
        {
            using var ready = await server.Client.GetAsync("/readyz");
            status = ready.StatusCode;
        }

        await Assert.That(status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await KeyIdsAsync(server)).Contains(second);
    }

    [Test]
    public async Task A_version_another_replica_prunes_first_never_stops_the_rest_of_the_pass()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target);
        await KeyIdsAsync(server);
        await RotateAsync(server);
        await RotateAsync(server);
        await RotateAsync(server);

        // Versions 1 to 3 are deprecated; the other replica schedules version 3 right after this one lists it.
        target.Vault.RaceNextListing("ins_a");

        await Assert.That(await PruneAsync(server)).IsEqualTo(2);
        await Assert.That(string.Join(',', target.Vault.Scheduled("ins_a"))).IsEqualTo("1,2,3");
    }

    [Test]
    public async Task A_secret_whose_pending_deletions_are_full_stays_unready_until_the_vault_deletes_them()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var vault = new ManualClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        target.Vault.Clock = vault;
        // Four versions of {}: v1 is pending deletion and fills that quota; v2 to v4 fill the active quota.
        var empty = JsonDocument.Parse("{}").RootElement;
        target.Vault.Put(OidcIssuer.InstanceId, empty);
        target.Vault.Put(OidcIssuer.InstanceId, empty);
        target.Vault.Put(OidcIssuer.InstanceId, empty);
        target.Vault.ScheduleDeletion(OidcIssuer.InstanceId, 1);
        target.Vault.MaxVersions = 3;
        target.Vault.MaxPendingDeletions = 1;

        // Neither the bootstrap's write nor the prune that would free a version succeeds: it retries, unready.
        await using var server = await StartAsync(target, clock: vault);
        var bootstrapped = server.App.Services.GetRequiredService<SigningKeyRotation>().Bootstrapped;
        await Task.WhenAny(bootstrapped, Task.Delay(TimeSpan.FromSeconds(7)));
        using var unready = await server.Client.GetAsync("/readyz");
        await Assert.That(bootstrapped.IsCompleted).IsFalse();
        await Assert.That(unready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(string.Join(',', target.Vault.Scheduled(OidcIssuer.InstanceId))).IsEqualTo("1");

        // Once OCI deletes v1, the next attempts prune v2 and write the ring.
        vault.Advance(target.Vault.DeletionDelay);
        await bootstrapped.WaitAsync(TimeSpan.FromSeconds(30));
        var status = HttpStatusCode.ServiceUnavailable;
        for (var deadline = DateTimeOffset.UtcNow.AddSeconds(10); status != HttpStatusCode.OK && DateTimeOffset.UtcNow < deadline; await Task.Delay(TimeSpan.FromMilliseconds(200)))
        {
            using var ready = await server.Client.GetAsync("/readyz");
            status = ready.StatusCode;
        }

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(string.Join(',', target.Vault.Scheduled(OidcIssuer.InstanceId))).IsEqualTo("1,2");
        await Assert.That(target.Vault.Read(OidcIssuer.InstanceId)!.Value.GetProperty("keys").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task Bootstrap_prunes_a_full_secret_to_replace_an_empty_ring()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var empty = JsonDocument.Parse("{}").RootElement;
        target.Vault.Put(OidcIssuer.InstanceId, empty);
        target.Vault.Put(OidcIssuer.InstanceId, empty);
        target.Vault.Put(OidcIssuer.InstanceId, empty);
        target.Vault.MaxVersions = 4;

        await using var server = await StartAsync(target);
        await server.App.Services.GetRequiredService<SigningKeyRotation>().Bootstrapped.WaitAsync(TimeSpan.FromSeconds(30));

        // The bootstrap pruned versions 1 and 2; the first hourly prune, right after it, may already have taken 3.
        await Assert.That(string.Join(',', target.Vault.Scheduled(OidcIssuer.InstanceId).Take(2))).IsEqualTo("1,2");
        await Assert.That(target.Vault.Read(OidcIssuer.InstanceId)!.Value.GetProperty("keys").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task Pruning_deprecated_versions_keeps_the_ring_secret_under_the_version_limit()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        target.Vault.MaxVersions = 4;
        await using var server = await StartAsync(target);
        await KeyIdsAsync(server);
        await RotateAsync(server);
        await RotateAsync(server);

        // {} and three rings: the next version would exceed the limit.
        using var exhausted = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);
        await Assert.That(exhausted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);

        await Assert.That(await PruneAsync(server)).IsEqualTo(2);
        await RotateAsync(server);
        await RotateAsync(server);
        await Assert.That(await PruneAsync(server)).IsEqualTo(2);
        await Assert.That(await PruneAsync(server)).IsEqualTo(0);
        // The current and previous versions are never scheduled.
        await Assert.That(string.Join(',', target.Vault.Scheduled("ins_a"))).IsEqualTo("1,2,3,4");
        await Assert.That(target.Vault.Current("ins_a").Version).IsEqualTo(6);
    }

    [Test]
    public async Task Replica_clock_skew_never_retires_or_activates_a_key_early()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var vault = new ManualClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        target.Vault.Clock = vault;
        await using var accurate = await StartAsync(target, masterKey, vault);
        // Five days ahead: by its own clock the first key's successor would have signed for longer than the overlap.
        var ahead = new ManualClock(vault.GetUtcNow().AddDays(5));
        await using var skewed = await StartAsync(target, masterKey, ahead, vaultClock: false);
        var first = (await KeyIdsAsync(accurate)).Single();
        vault.Advance(KeyRing.RotationAge);
        ahead.Advance(KeyRing.RotationAge);
        await Assert.That(await RotateIfDueAsync(accurate)).IsTrue();
        var token = await SignAsync(accurate, "ins_a", AccessType);
        await Assert.That(KeyId(token)).IsEqualTo(first);
        await Assert.That(await SignStatusAsync(skewed, "ins_a")).IsEqualTo(HttpStatusCode.ServiceUnavailable);

        await RotateAsync(skewed);

        var keys = await KeyIdsAsync(accurate);
        await Assert.That(keys.Count).IsEqualTo(3);
        await Assert.That(keys[^1]).IsEqualTo(first);
        await Assert.That(await ValidateAsync(accurate, "ins_a", token)).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_replica_with_another_master_key_never_replaces_the_ring_until_an_operator_empties_it()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var serving = await StartAsync(target);
        await serving.App.Services.GetRequiredService<SigningKeyRotation>().Bootstrapped.WaitAsync(TimeSpan.FromSeconds(30));
        var token = await SignAsync(serving, OidcIssuer.InstanceId, AccessType);
        var before = target.Vault.Current(OidcIssuer.InstanceId);

        // Each test server has its own random master key: this replica is misconfigured.
        await using var misconfigured = await StartAsync(target);
        var bootstrap = misconfigured.App.Services.GetRequiredService<SigningKeyRotation>().Bootstrapped;
        await Task.Delay(TimeSpan.FromSeconds(2));
        await Assert.That(bootstrap.IsCompleted).IsFalse();
        await Assert.That(target.Vault.Current(OidcIssuer.InstanceId).Version).IsEqualTo(before.Version);
        await Assert.That(await ValidateAsync(serving, OidcIssuer.InstanceId, token)).IsEqualTo(HttpStatusCode.OK);
        using var ready = await misconfigured.Client.GetAsync("/readyz");
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);

        // The explicit recovery: an operator writes {} and the next bootstrap attempt replaces the ring.
        target.Vault.Put(OidcIssuer.InstanceId, JsonDocument.Parse("{}").RootElement);
        await bootstrap.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(target.Vault.Read(OidcIssuer.InstanceId)!.Value.GetProperty("masterKeyId").GetString())
            .IsNotEqualTo(before.Document.GetProperty("masterKeyId").GetString());
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_oversized_stored_ring_is_refused_on_lookup_and_rotation_without_pruning(bool ancient)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target);
        await KeyIdsAsync(server);

        var ring = JsonNode.Parse(target.Vault.Read("ins_a")!.Value.GetRawText())!;
        var record = ring["keys"]![0]!;
        var start = DateTimeOffset.UtcNow.AddHours(ancient ? -72 : 0).ToUnixTimeSeconds();
        ring["keys"] = new JsonArray([.. Enumerable.Range(0, KeyRing.MaxKeys + 1).Select(index =>
        {
            var copy = record.DeepClone();
            copy["createdAt"] = start + index;
            return copy;
        })]);
        target.Vault.Put("ins_a", JsonSerializer.SerializeToElement(ring));
        var before = target.Vault.Read("ins_a")!.Value.GetRawText();

        using var response = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(target.Vault.Read("ins_a")!.Value.GetRawText()).IsEqualTo(before);
        using var rotation = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);

        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(target.Vault.Read("ins_a")!.Value.GetRawText()).IsEqualTo(before);
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

        var ring = JsonNode.Parse(target.Vault.Read("ins_a")!.Value.GetRawText())!;
        // An ancient malformed successor would previously prune the live token's key.
        var createdAt = DateTimeOffset.UtcNow.AddHours(-72).ToUnixTimeSeconds();
        ring["keys"]![0]!["createdAt"] = createdAt;
        ring["keys"]![1]!["createdAt"] = createdAt + successorOffset;
        target.Vault.Put("ins_a", JsonSerializer.SerializeToElement(ring));
        var before = target.Vault.Read("ins_a")!.Value.GetRawText();

        using var lookup = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        await Assert.That(lookup.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(await ValidateAsync(server, "ins_a", token)).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(target.Vault.Read("ins_a")!.Value.GetRawText()).IsEqualTo(before);
        using var rotation = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);
        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(target.Vault.Read("ins_a")!.Value.GetRawText()).IsEqualTo(before);
    }

    [Test]
    [Arguments(long.MinValue)]
    [Arguments(long.MaxValue)]
    [Arguments(-62_135_596_801L)]
    [Arguments(253_402_300_800L)]
    public async Task Invalid_unix_timestamps_never_produce_a_rotation(long createdAt)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target);
        await KeyIdsAsync(server);

        var ring = JsonNode.Parse(target.Vault.Read("ins_a")!.Value.GetRawText())!;
        ring["keys"]![0]!["createdAt"] = createdAt;
        target.Vault.Put("ins_a", JsonSerializer.SerializeToElement(ring));
        var before = target.Vault.Read("ins_a")!.Value.GetRawText();

        using var lookup = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        await Assert.That(lookup.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        using var rotation = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);
        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(target.Vault.Read("ins_a")!.Value.GetRawText()).IsEqualTo(before);
    }

    [Test]
    public async Task A_key_created_at_the_maximum_unix_timestamp_remains_readable_but_cannot_rotate()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target, clock: new ManualClock(DateTimeOffset.MaxValue));
        await KeyIdsAsync(server);
        var id = "ins_a";
        var before = target.Vault.Current(id);
        await Assert.That(before.Document.GetProperty("keys")[0].GetProperty("createdAt").GetInt64()).IsEqualTo(253_402_300_799L);

        using var lookup = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        await Assert.That(lookup.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var rotation = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);
        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        var after = target.Vault.Current(id);
        await Assert.That(after.Version).IsEqualTo(before.Version);
        await Assert.That(after.Document.GetRawText()).IsEqualTo(before.Document.GetRawText());
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
        var ring = JsonNode.Parse(target.Vault.Read("ins_a")!.Value.GetRawText())!;
        var record = ring["keys"]![0]!;
        record["secretBox"] = SecretBox.Seal(RandomNumberGenerator.GetBytes(32), new byte[96], $"signing-key:{record["id"]!.GetValue<string>()}:ins_a");
        target.Vault.Put("ins_a", JsonSerializer.SerializeToElement(ring));
        var corrupted = target.Vault.Read("ins_a")!.Value.GetRawText();

        using var lookup = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        await Assert.That(lookup.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var rotation = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);

        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(target.Vault.Read("ins_a")!.Value.GetRawText()).IsEqualTo(corrupted);
    }

    [Test]
    [Arguments("read")]
    [Arguments("write")]
    public async Task Vault_failures_fail_closed_with_503(string failing)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        target.Vault.Failing = failing;
        await using var server = await StartAsync(target);

        using var lookup = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        using var rotation = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);
        var body = await rotation.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(lookup.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
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
            ring["keys"]![0]!["id"] = "skey_" + new string('a', 40);
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
    public async Task Valid_public_coordinates_must_match_private_material_before_signing_or_rotation()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = RandomNumberGenerator.GetBytes(32);
        await using var server = await StartAsync(target, Convert.ToBase64String(masterKey));
        var originalToken = await SignAsync(server, "ins_a", AccessType);
        await RotateAsync(server);
        var token = await SignAsync(server, "ins_a", AccessType);
        var ring = JsonNode.Parse(target.Vault.Read("ins_a")!.Value.GetRawText())!;
        var original = ring["keys"]![0]!;
        var replacement = ring["keys"]![1]!;
        var originalKid = original["id"]!.GetValue<string>();
        var replacementKid = replacement["id"]!.GetValue<string>();
        var secret = SecretBox.Open(masterKey, original["secretBox"]!.GetValue<string>(), $"signing-key:{originalKid}:ins_a");
        try
        {
            // Keep the source-generated public metadata and MAC; only reseal the other private key.
            replacement["secretBox"] = SecretBox.Seal(masterKey, secret, $"signing-key:{replacementKid}:ins_a");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        target.Vault.Put("ins_a", JsonSerializer.SerializeToElement(ring));
        var id = "ins_a";
        var before = target.Vault.Current(id);

        var jwks = await server.Client.GetFromJsonAsync<JsonElement>("/__test/instances/ins_a/jwks");
        await Assert.That(jwks.GetProperty("keys")[0].GetProperty("kid").GetString()).IsEqualTo(replacementKid);
        await Assert.That(jwks.GetProperty("keys")[0].GetProperty("x").GetString()).IsEqualTo(replacement["x"]!.GetValue<string>());
        await Assert.That(jwks.GetProperty("keys")[0].GetProperty("y").GetString()).IsEqualTo(replacement["y"]!.GetValue<string>());
        await Assert.That(await ValidateAsync(server, "ins_a", token)).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await ValidateAsync(server, "ins_a", originalToken)).IsEqualTo(HttpStatusCode.OK);
        using var signing = await server.Client.PostAsJsonAsync("/__test/instances/ins_a/tokens", new SignRequest(AccessType, new() { ["sub"] = "user_1" }));
        await Assert.That(signing.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        using var rotation = await server.Client.PostAsync("/__test/instances/ins_a/keys/rotate", null);
        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        var after = target.Vault.Current(id);
        await Assert.That(after.Version).IsEqualTo(before.Version);
        await Assert.That(after.Document.GetRawText()).IsEqualTo(before.Document.GetRawText());
    }

    [Test]
    [Arguments("padding")]
    [Arguments("noncanonical")]
    [Arguments("off_curve")]
    public async Task Public_coordinates_must_be_canonical_and_on_the_curve(string corruption)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var masterKey = RandomNumberGenerator.GetBytes(32);
        await using var server = await StartAsync(target, Convert.ToBase64String(masterKey));
        await SignAsync(server, "ins_a", AccessType);
        var ring = JsonNode.Parse(target.Vault.Read("ins_a")!.Value.GetRawText())!;
        var record = ring["keys"]![0]!;
        var metadataKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: "ts-auth signing public metadata v1"u8.ToArray());
        string PublicMetadataMac() => Convert.ToHexStringLower(HMACSHA256.HashData(metadataKey, JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            "ins_a", record["id"]!.GetValue<string>(), record["algorithm"]!.GetValue<string>(),
            record["createdAt"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture),
            record["activatesAt"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture),
            record["x"]!.GetValue<string>(), record["y"]!.GetValue<string>(),
        })));
        // Check the fixture's independent MAC encoding against genuine service-generated metadata.
        await Assert.That(PublicMetadataMac()).IsEqualTo(record["publicMetadataMac"]!.GetValue<string>());
        var x = record["x"]!.GetValue<string>();
        if (corruption == "padding")
        {
            record["x"] = x + "=";
        }
        else if (corruption == "noncanonical")
        {
            // A nonzero pad bit decodes to the same bytes but must not be accepted as canonical.
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
            record["x"] = x[..^1] + alphabet[alphabet.IndexOf(x[^1], StringComparison.Ordinal) + 1];
        }
        else
        {
            record["x"] = Base64UrlEncoder.Encode(new byte[32]);
            record["y"] = Base64UrlEncoder.Encode(new byte[32]);
        }

        // Authenticate the malformed fixture so the HTTP path reaches coordinate import.
        record["publicMetadataMac"] = PublicMetadataMac();
        await Assert.That(() =>
        {
            using var rejected = Es256Key.ImportPublic(record["id"]!.GetValue<string>(), record["x"]!.GetValue<string>(), record["y"]!.GetValue<string>());
        }).Throws<CryptographicException>();
        target.Vault.Put("ins_a", JsonSerializer.SerializeToElement(ring));
        var id = "ins_a";
        var before = target.Vault.Current(id);
        using var lookup = await server.Client.GetAsync("/__test/instances/ins_a/jwks");
        await Assert.That(lookup.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        var after = target.Vault.Current(id);
        await Assert.That(after.Version).IsEqualTo(before.Version);
        await Assert.That(after.Document.GetRawText()).IsEqualTo(before.Document.GetRawText());
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

    // A well-formed token naming a key no ring has; its signature is never reached.
    private static string UnknownKeyToken(string kid) =>
        $"{Base64UrlEncoder.Encode($"{{\"alg\":\"ES256\",\"typ\":\"{AccessType}\",\"kid\":\"{kid}\"}}")}.{Base64UrlEncoder.Encode("{\"sub\":\"user_1\"}")}.{Base64UrlEncoder.Encode(new byte[64])}";

    private static async Task<int> PruneAsync(AuthTestServer server)
    {
        using var response = await server.Client.PostAsync("/__test/instances/ins_a/keys/prune", null);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scheduled").GetInt32();
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

    private static async Task<HttpStatusCode> SignStatusAsync(AuthTestServer server, string instance)
    {
        using var response = await server.Client.PostAsJsonAsync($"/__test/instances/{instance}/tokens", new SignRequest(AccessType, new() { ["sub"] = "user_1" }));
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> ValidateAsync(AuthTestServer server, string instance, string token, string audience = Audience)
    {
        using var response = await server.Client.PostAsJsonAsync($"/__test/instances/{instance}/tokens/validate", new ValidateRequest(token, AccessType, audience));
        return response.StatusCode;
    }

    private sealed record SignRequest(string Type, Dictionary<string, string> Claims, int AgeSeconds = 0, string? Audience = null, int LifetimeSeconds = 600, bool ReplicaClock = false);

    private sealed record ValidateRequest(string Token, string Type, string Audience);
}
