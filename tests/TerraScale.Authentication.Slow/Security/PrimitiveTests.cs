using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Security;

/// <summary>
/// The security primitives as the service runs them: each test-only route calls the primitive
/// inside the real application, with the configured master key.
/// </summary>
public sealed class PrimitiveTests
{
    private const string Password = "correct horse battery staple";

    // Work factors past the bounds, wrong variant or version, truncated parts and plain text.
    private static readonly string[] MalformedHashes =
    [
        "$argon2id$v=19$m=999999999,t=1,p=1$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA",
        "$argon2id$v=19$m=1048577,t=1,p=1$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA",
        "$argon2id$v=19$m=64,t=33,p=1$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA",
        "$argon2id$v=19$m=64,t=1,p=17$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA",
        "$argon2id$v=19$m=64,t=0,p=1$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA",
        "$argon2id$v=19$m=32,t=1,p=1$c2FsdA$AA",
        "$argon2i$v=19$m=64,t=1,p=1$dHMtYXV0aC12ZWN0b3IxNg$DSktwS9SktskGJvD3C/lWK0F3U9f7EbVnqf4UsOE8FQ",
        "$argon2id$v=16$m=64,t=1,p=1$dHMtYXV0aC12ZWN0b3IxNg$DSktwS9SktskGJvD3C/lWK0F3U9f7EbVnqf4UsOE8FQ",
        "$argon2id$v=19$m=64,t=1,p=1$dHMtYXV0aC12ZWN0b3IxNg$DSktwS9SktskGJvD3C/lWK0F3U9f7EbVnqf4UsOE8FQ$extra",
        "",
        "plaintext-password",
    ];

    // Correct hashes of Password from the reference implementation whose cost exceeds the
    // verification budget: 128 MiB, 64 MiB × 4 passes, 11 passes and 9 lanes. Without the
    // bounds each would verify.
    private static readonly string[] OverBudgetHashes =
    [
        "$argon2id$v=19$m=131072,t=1,p=1$dHMtYXV0aC12ZWN0b3IxNg$tt2wwJC8/m6ufALksdn/2GybOb3UiVRjvGXfYib7yco",
        "$argon2id$v=19$m=65536,t=4,p=1$dHMtYXV0aC12ZWN0b3IxNg$rDhxOyHN3yJkLeWzoB+vafyxh7NtrmdYHQJkKPFFprU",
        "$argon2id$v=19$m=1024,t=11,p=1$dHMtYXV0aC12ZWN0b3IxNg$cTl77kbAwgUBNUDJEHC37BRU20hDLYYYnDLvSPsbZfs",
        "$argon2id$v=19$m=128,t=1,p=9$dHMtYXV0aC12ZWN0b3IxNg$j5FrzplZrWaAQEZmsL+RMIG+7+yxqhwE3ib+6yUVQVc",
    ];

    private static Task<AuthTestServer> StartAsync(string? masterKey = null) => AuthTestServer.StartAsync(
        settings: values =>
        {
            if (masterKey is not null)
            {
                values["master-key"] = masterKey;
            }
        },
        routes: app =>
        {
            app.MapPost("/__test/passwords/hash", (Input input) => new { phc = Passwords.Hash(input.Value!) });
            app.MapPost("/__test/passwords/verify", (Input input) => new { valid = Passwords.Verify(input.Value!, input.Hash!) });
            app.MapPost("/__test/secrets/seal", (Input input, AuthOptions options) =>
                new { boxed = SecretBox.Seal(options.MasterKey, Encoding.UTF8.GetBytes(input.Value!), input.Context!) });
            app.MapPost("/__test/secrets/open", (Input input, AuthOptions options) =>
            {
                try
                {
                    return Results.Ok(new { value = Encoding.UTF8.GetString(SecretBox.Open(options.MasterKey, input.Value!, input.Context!)) });
                }
                catch (CryptographicException)
                {
                    return Results.UnprocessableEntity();
                }
            });
            app.MapGet("/__test/tokens", () =>
            {
                var (id, secret, combined) = Tokens.NewSecret("sk");
                return new
                {
                    token = Tokens.RandomToken(64),
                    digits = Tokens.RandomDigits(6),
                    id,
                    secret,
                    combined,
                    abc = Tokens.Sha256Hex("abc"),
                    same = Tokens.FixedTimeEquals(combined, $"{id}_{secret}"),
                    different = Tokens.FixedTimeEquals(combined, combined[..^1] + "#"),
                    shorter = Tokens.FixedTimeEquals(combined, combined[..^1]),
                };
            });
        });

    [Test]
    public async Task Passwords_hash_to_argon2id_phc_with_the_production_cost_and_a_fresh_salt()
    {
        await using var server = await StartAsync();

        var first = await HashAsync(server, Password);
        var second = await HashAsync(server, Password);

        // 16-byte salt and 32-byte tag, unpadded base64.
        await Assert.That(first).Matches(@"\A\$argon2id\$v=19\$m=19456,t=2,p=1\$[A-Za-z0-9+/]{22}\$[A-Za-z0-9+/]{43}\z");
        await Assert.That(second).IsNotEqualTo(first);
        await Assert.That(await VerifyAsync(server, Password, first)).IsTrue();
        await Assert.That(await VerifyAsync(server, Password, second)).IsTrue();
        await Assert.That(await VerifyAsync(server, "correct horse battery stapler", first)).IsFalse();
        await Assert.That(await VerifyAsync(server, string.Empty, first)).IsFalse();
    }

    [Test]
    public async Task Password_verification_accepts_standard_phc_from_the_reference_implementation()
    {
        await using var server = await StartAsync();

        // Produced by the RFC 9106 reference implementation (argon2-cffi), not by libsodium, so
        // hashes written by the Rust service or any conforming encoder verify here.
        const string oneLane = "$argon2id$v=19$m=64,t=1,p=1$dHMtYXV0aC12ZWN0b3IxNg$DSktwS9SktskGJvD3C/lWK0F3U9f7EbVnqf4UsOE8FQ";
        const string fourLanes = "$argon2id$v=19$m=64,t=1,p=4$dHMtYXV0aC12ZWN0b3IxNg$/+z3Ms6NUF5Pc1Xx6Xy8eOwa7jF5Oy+y01IVyAycTfI";
        // The RFC 9106 second recommended profile (64 MiB, t=3, p=4) is the costliest accepted.
        const string budgetCeiling = "$argon2id$v=19$m=65536,t=3,p=4$dHMtYXV0aC12ZWN0b3IxNg$jxQeh9iuFgQmI+yQ4I+Rf28VCUBceSHSbJTVU0E1p9A";

        await Assert.That(await VerifyAsync(server, Password, oneLane)).IsTrue();
        await Assert.That(await VerifyAsync(server, Password, fourLanes)).IsTrue();
        await Assert.That(await VerifyAsync(server, "wrong", oneLane)).IsFalse();
        await Assert.That(await VerifyAsync(server, "wrong", fourLanes)).IsFalse();
        await Assert.That(await VerifyAsync(server, Password, budgetCeiling)).IsTrue();
        foreach (var phc in OverBudgetHashes)
        {
            await Assert.That(await VerifyAsync(server, Password, phc)).IsFalse();
        }
    }

    [Test]
    public async Task Malformed_or_oversized_password_hashes_never_verify()
    {
        await using var server = await StartAsync();

        foreach (var phc in MalformedHashes)
        {
            await Assert.That(await VerifyAsync(server, Password, phc)).IsFalse();
        }
    }

    [Test]
    public async Task Secrets_seal_as_v1_aes_gcm_and_open_only_under_the_same_key_and_context()
    {
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var server = await StartAsync(masterKey);
        await using var otherKey = await StartAsync();

        var boxed = await SealAsync(server, "totp seed", "webhook:wh_1:ins_a");
        var raw = Convert.FromBase64String(boxed["v1:".Length..]);

        await Assert.That(boxed).StartsWith("v1:");
        await Assert.That(raw.Length).IsEqualTo(12 + "totp seed".Length + 16);
        await Assert.That(boxed).DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("totp seed")));
        await Assert.That(await SealAsync(server, "totp seed", "webhook:wh_1:ins_a")).IsNotEqualTo(boxed);
        await Assert.That(await OpenAsync(server, boxed, "webhook:wh_1:ins_a")).IsEqualTo("totp seed");

        // The box was sealed by the service under TS_AUTH_MASTER_KEY with the context as associated data.
        var opened = new byte["totp seed".Length];
        using (var aes = new AesGcm(Convert.FromBase64String(masterKey), 16))
        {
            aes.Decrypt(raw[..12], raw[12..^16], raw[^16..], opened, Encoding.UTF8.GetBytes("webhook:wh_1:ins_a"));
        }

        await Assert.That(Encoding.UTF8.GetString(opened)).IsEqualTo("totp seed");

        var tampered = (byte[])raw.Clone();
        tampered[^1] ^= 1;
        await Assert.That(await OpenAsync(server, "v1:" + Convert.ToBase64String(tampered), "webhook:wh_1:ins_a")).IsNull();
        await Assert.That(await OpenAsync(server, boxed, "webhook:wh_1:ins_b")).IsNull();
        await Assert.That(await OpenAsync(otherKey, boxed, "webhook:wh_1:ins_a")).IsNull();
        await Assert.That(await OpenAsync(server, "v2:" + boxed[3..], "webhook:wh_1:ins_a")).IsNull();
        await Assert.That(await OpenAsync(server, "v1:AA==", "webhook:wh_1:ins_a")).IsNull();
        await Assert.That(await OpenAsync(server, "v1:not base64!", "webhook:wh_1:ins_a")).IsNull();
    }

    [Test]
    public async Task Tokens_are_random_alphanumeric_and_secrets_compare_in_fixed_time()
    {
        await using var server = await StartAsync();

        var first = await server.Client.GetFromJsonAsync<JsonElement>("/__test/tokens");
        var second = await server.Client.GetFromJsonAsync<JsonElement>("/__test/tokens");

        await Assert.That(first.GetProperty("token").GetString()).Matches(@"\A[A-Za-z0-9]{64}\z");
        await Assert.That(first.GetProperty("token").GetString()).IsNotEqualTo(second.GetProperty("token").GetString());
        await Assert.That(first.GetProperty("digits").GetString()).Matches(@"\A[0-9]{6}\z");
        await Assert.That(first.GetProperty("id").GetString()).Matches(@"\Ask_[A-Za-z0-9]{22}\z");
        await Assert.That(first.GetProperty("secret").GetString()).Matches(@"\A[A-Za-z0-9]{40}\z");
        await Assert.That(first.GetProperty("combined").GetString())
            .IsEqualTo($"{first.GetProperty("id").GetString()}_{first.GetProperty("secret").GetString()}");
        await Assert.That(first.GetProperty("abc").GetString()).IsEqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        await Assert.That(first.GetProperty("same").GetBoolean()).IsTrue();
        await Assert.That(first.GetProperty("different").GetBoolean()).IsFalse();
        await Assert.That(first.GetProperty("shorter").GetBoolean()).IsFalse();
    }

    private static async Task<string> HashAsync(AuthTestServer server, string password)
    {
        using var response = await server.Client.PostAsJsonAsync("/__test/passwords/hash", new Input(password));
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("phc").GetString()!;
    }

    private static async Task<bool> VerifyAsync(AuthTestServer server, string password, string phc)
    {
        using var response = await server.Client.PostAsJsonAsync("/__test/passwords/verify", new Input(password, Hash: phc));
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("valid").GetBoolean();
    }

    private static async Task<string> SealAsync(AuthTestServer server, string value, string context)
    {
        using var response = await server.Client.PostAsJsonAsync("/__test/secrets/seal", new Input(value, Context: context));
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("boxed").GetString()!;
    }

    private static async Task<string?> OpenAsync(AuthTestServer server, string boxed, string context)
    {
        using var response = await server.Client.PostAsJsonAsync("/__test/secrets/open", new Input(boxed, Context: context));
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("value").GetString();
        }

        // A refused box is the handled CryptographicException, never an unexpected failure.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        return null;
    }

    private sealed record Input(string? Value, string? Hash = null, string? Context = null);
}
