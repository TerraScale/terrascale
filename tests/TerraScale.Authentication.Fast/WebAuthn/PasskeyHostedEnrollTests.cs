using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.WebAuthn;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.WebAuthn;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PasskeyHostedEnrollTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Enrolling_a_passkey_from_the_security_page_lists_it()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = await oidc.CreateUserAsync("passkey-hosted@example.test");
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var session = await sessions.CreateAsync(user);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentialId = RandomNumberGenerator.GetBytes(32);

        using var options = await SendAsync(oidc, "/ui/terrascale/security/passkeys/options", session.Token);
        await Assert.That(options.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await OidcHarness.JsonAsync(options);
        var sealedChallenge = body.GetProperty("challenge").GetString()!;
        var ceremony = body.GetProperty("options").GetProperty("challenge").GetString()!;

        using var verify = await SendAsync(oidc, "/ui/terrascale/security/passkeys/verify", session.Token, Attestation(sealedChallenge, ceremony, credentialId, ecdsa));
        await Assert.That(verify.StatusCode).IsEqualTo(HttpStatusCode.Created);

        using var page = await SendAsync(oidc, "/ui/terrascale/security", session.Token);
        var html = await page.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("Test key");
        await Assert.That(html).Contains("Never used");
    }

    private static Task<HttpResponseMessage> SendAsync(OidcHarness oidc, string path, string session, object? json = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Cookie", $"tsauth_session={session}");
        request.Headers.Add("Origin", OidcHarness.PublicOrigin);
        if (json is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
        }

        return oidc.Client.SendAsync(request);
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
