using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Authentication.Features.WebAuthn;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.WebAuthn;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PasskeyManageTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Renaming_and_deleting_a_passkey_updates_the_list()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (_, token) = await oidc.SignInUserAsync("passkey-manage@example.test");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (sealedChallenge, ceremony) = await OptionsAsync(oidc, token);
        using var enrolled = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/account/passkeys/verify", token,
            Attestation(sealedChallenge, ceremony, RandomNumberGenerator.GetBytes(32), ecdsa));
        await Assert.That(enrolled.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var id = (await OidcHarness.JsonAsync(enrolled)).GetProperty("id").GetString()!;

        using var renamed = await oidc.CallAsync(HttpMethod.Patch, "/v1/terrascale/account/passkeys/" + id, token, new { name = "Front door key" });
        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await OidcHarness.JsonAsync(renamed)).GetProperty("name").GetString()).IsEqualTo("Front door key");

        using var listed = await oidc.CallAsync(HttpMethod.Get, "/v1/terrascale/account/passkeys", token);
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var passkeys = (await OidcHarness.JsonAsync(listed)).GetProperty("passkeys");
        await Assert.That(passkeys.GetArrayLength()).IsEqualTo(1);
        await Assert.That(passkeys[0].GetProperty("name").GetString()).IsEqualTo("Front door key");
        await Assert.That(passkeys[0].TryGetProperty("public_key", out _)).IsFalse();

        using var deleted = await oidc.CallAsync(HttpMethod.Delete, "/v1/terrascale/account/passkeys/" + id, token);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var after = await oidc.CallAsync(HttpMethod.Get, "/v1/terrascale/account/passkeys", token);
        await Assert.That((await OidcHarness.JsonAsync(after)).GetProperty("passkeys").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Deleting_a_passkey_without_a_step_up_proof_is_refused()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (_, token) = await oidc.SignInUserAsync("passkey-noproof@example.test");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (sealedChallenge, ceremony) = await OptionsAsync(oidc, token);
        using var enrolled = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/account/passkeys/verify", token,
            Attestation(sealedChallenge, ceremony, RandomNumberGenerator.GetBytes(32), ecdsa));
        await Assert.That(enrolled.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var id = (await OidcHarness.JsonAsync(enrolled)).GetProperty("id").GetString()!;

        var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/terrascale/account/passkeys/" + id);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var refused = await oidc.Client.SendAsync(request);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(refused)).IsEqualTo("step_up_required");
        using var kept = await oidc.CallAsync(HttpMethod.Get, "/v1/terrascale/account/passkeys", token);
        await Assert.That((await OidcHarness.JsonAsync(kept)).GetProperty("passkeys").GetArrayLength()).IsEqualTo(1);
    }

    private static async Task<(string Sealed, string Ceremony)> OptionsAsync(OidcHarness oidc, string token)
    {
        using var response = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/account/passkeys/options", token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var options = await OidcHarness.JsonAsync(response);
        return (options.GetProperty("challenge").GetString()!, options.GetProperty("options").GetProperty("challenge").GetString()!);
    }

    private static object Attestation(string sealedChallenge, string ceremonyChallenge, byte[] credentialId, ECDsa ecdsa)
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
