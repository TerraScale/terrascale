using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Authentication.Features.WebAuthn;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.WebAuthn;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PasskeyEnrollTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Enrolling_a_passkey_with_a_none_attestation_persists_the_credential()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (_, token) = await oidc.SignInUserAsync("passkey-happy@example.test");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (sealedChallenge, ceremony) = await OptionsAsync(oidc, token);
        var credentialId = RandomNumberGenerator.GetBytes(32);

        using var verified = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/account/passkeys/verify", token,
            Attestation(sealedChallenge, ceremony, credentialId, ecdsa));
        await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var enrolled = await OidcHarness.JsonAsync(verified);
        await Assert.That(enrolled.GetProperty("id").GetString()).IsEqualTo(PasskeyCredentials.Base64UrlEncode(credentialId));

        using var next = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/account/passkeys/options", token);
        await Assert.That(next.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var options = await OidcHarness.JsonAsync(next);
        await Assert.That(options.GetProperty("options").GetProperty("excludeCredentials").GetArrayLength()).IsEqualTo(1);
        await Assert.That(options.GetProperty("options").GetProperty("excludeCredentials")[0].GetProperty("id").GetString())
            .IsEqualTo(PasskeyCredentials.Base64UrlEncode(credentialId));
    }

    [Test]
    public async Task A_verify_without_a_proof_a_reused_challenge_or_a_stale_epoch_is_refused()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, token) = await oidc.SignInUserAsync("passkey-stale@example.test");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (sealedChallenge, ceremony) = await OptionsAsync(oidc, token);

        using var unproven = new HttpRequestMessage(HttpMethod.Post, "/v1/terrascale/account/passkeys/verify")
        {
            Content = JsonContent.Create(Attestation(sealedChallenge, ceremony, RandomNumberGenerator.GetBytes(32), ecdsa)),
        };
        unproven.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var refused = await oidc.Client.SendAsync(unproven);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(refused)).IsEqualTo("step_up_required");

        using var enrolled = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/account/passkeys/verify", token,
            Attestation(sealedChallenge, ceremony, RandomNumberGenerator.GetBytes(32), ecdsa));
        await Assert.That(enrolled.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var reused = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/account/passkeys/verify", token,
            Attestation(sealedChallenge, ceremony, RandomNumberGenerator.GetBytes(32), ecdsa));
        await Assert.That(reused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(reused)).IsEqualTo("passkey_denied");

        using var signedOut = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/account/sign-out", token);
        await Assert.That(signedOut.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        user = await oidc.Server.App.Services.GetRequiredService<Users>().GetAsync(user.InstanceId, user.Id) ?? user;
        var (_, reissued) = await oidc.CreateSessionAndRedeemAsync(user);
        var fresh = reissued.GetProperty("access_token").GetString()!;
        using var stale = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/account/passkeys/verify", fresh,
            Attestation(sealedChallenge, ceremony, RandomNumberGenerator.GetBytes(32), ecdsa));
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(stale)).IsEqualTo("passkey_denied");
    }

    private static async Task<(string Sealed, string Ceremony)> OptionsAsync(OidcHarness oidc, string token)
    {
        using var response = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/account/passkeys/options", token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var options = await OidcHarness.JsonAsync(response);
        return (options.GetProperty("challenge").GetString()!, options.GetProperty("options").GetProperty("challenge").GetString()!);
    }

    internal static object Attestation(string sealedChallenge, string ceremonyChallenge, byte[] credentialId, ECDsa ecdsa)
    {
        var parameters = ecdsa.ExportParameters(false);
        var cose = new List<byte> { 0xa5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20 };
        cose.AddRange(parameters.Q.X!);
        cose.AddRange([0x22, 0x58, 0x20]);
        cose.AddRange(parameters.Q.Y!);
        var authData = new List<byte>();
        authData.AddRange(SHA256.HashData(Encoding.ASCII.GetBytes("test.invalid")));
        authData.Add(0x45);
        authData.AddRange([(byte)0, (byte)0, (byte)0, (byte)0]);
        authData.AddRange(new byte[16]);
        authData.AddRange([(byte)0, 0x20]);
        authData.AddRange(credentialId);
        authData.AddRange(cose);
        var attestation = new List<byte> { 0xa3, 0x63 };
        attestation.AddRange(Encoding.ASCII.GetBytes("fmt"));
        attestation.Add(0x64);
        attestation.AddRange(Encoding.ASCII.GetBytes("none"));
        attestation.Add(0x67);
        attestation.AddRange(Encoding.ASCII.GetBytes("attStmt"));
        attestation.Add(0xa0);
        attestation.Add(0x68);
        attestation.AddRange(Encoding.ASCII.GetBytes("authData"));
        attestation.Add(0x58);
        attestation.Add((byte)authData.Count);
        attestation.AddRange(authData);
        var clientData = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["type"] = "webauthn.create",
            ["challenge"] = ceremonyChallenge,
            ["origin"] = OidcHarness.PublicOrigin,
        });
        var id = PasskeyCredentials.Base64UrlEncode(credentialId);
        return new
        {
            challenge = sealedChallenge,
            name = "Test key",
            response = new
            {
                id,
                rawId = id,
                type = "public-key",
                response = new
                {
                    clientDataJSON = PasskeyCredentials.Base64UrlEncode(clientData),
                    attestationObject = PasskeyCredentials.Base64UrlEncode([.. attestation]),
                    transports = new[] { "internal" },
                },
            },
        };
    }
}
