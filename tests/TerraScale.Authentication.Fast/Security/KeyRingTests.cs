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
                if (await store.GetAsync(AuthStore.ProjectTable, OidcIssuer.InstanceId, project) is null)
                {
                    await store.CreateAsync(AuthStore.ProjectTable, OidcIssuer.InstanceId, project, TerraBaseFixture.Json($$"""{"id":"{{project}}"}"""));
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
