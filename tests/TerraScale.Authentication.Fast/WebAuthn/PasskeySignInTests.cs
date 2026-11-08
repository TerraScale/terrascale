using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.WebAuthn;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.WebAuthn;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PasskeySignInTests(TerraBaseFixture fixture)
{
    private const string Slug = "terrascale";

    [Test]
    public async Task A_user_verified_passkey_sign_in_opens_a_session_that_satisfies_mfa()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, token) = await oidc.SignInUserAsync("passkey-signin-happy@example.test");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentialId = await EnrollAsync(oidc, token, ecdsa);

        var options = await OidcHarness.JsonAsync(await OptionsResponseAsync(oidc, user.Email));
        await Assert.That(options.GetProperty("options").GetProperty("allowCredentials")[0].GetProperty("id").GetString())
            .IsEqualTo(PasskeyCredentials.Base64UrlEncode(credentialId));

        using var signedIn = await oidc.Client.SendAsync(Assertion(options.GetProperty("challenge").GetString()!,
            options.GetProperty("options").GetProperty("challenge").GetString()!, credentialId, ecdsa, 5, user.Email));
        await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var session = OidcHarness.SessionCookie(signedIn) ?? throw new InvalidOperationException("No session cookie.");

        var (verifier, challenge) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, challenge), verifier);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokenSet = await OidcHarness.JsonAsync(redeemed);
        var accessToken = tokenSet.GetProperty("access_token").GetString()!;
        await Assert.That(new JsonWebToken(accessToken).GetClaim(OidcIssuer.MfaClaim)?.Value).IsEqualTo("passkey");
    }

    [Test]
    public async Task A_replayed_challenge_or_a_frozen_sign_count_is_refused()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, token) = await oidc.SignInUserAsync("passkey-signin-refused@example.test");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentialId = await EnrollAsync(oidc, token, ecdsa);

        var (sealedChallenge, ceremony) = await OptionsAsync(oidc, user.Email);
        using var first = await oidc.Client.SendAsync(Assertion(sealedChallenge, ceremony, credentialId, ecdsa, 7, user.Email));
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var replayed = await oidc.Client.SendAsync(Assertion(sealedChallenge, ceremony, credentialId, ecdsa, 8, user.Email));
        await Assert.That(replayed.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(replayed)).IsEqualTo("passkey_denied");

        var (freshSealed, freshCeremony) = await OptionsAsync(oidc, user.Email);
        using var cloned = await oidc.Client.SendAsync(Assertion(freshSealed, freshCeremony, credentialId, ecdsa, 7, user.Email));
        await Assert.That(cloned.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(cloned)).IsEqualTo("passkey_denied");
    }

    private static async Task<byte[]> EnrollAsync(OidcHarness oidc, string token, ECDsa ecdsa)
    {
        using var request = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/account/passkeys/options", token);
        var enrollment = await OidcHarness.JsonAsync(request);
        var credentialId = RandomNumberGenerator.GetBytes(32);
        using var verified = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/account/passkeys/verify", token,
            PasskeyEnrollTests.Attestation(enrollment.GetProperty("challenge").GetString()!,
                enrollment.GetProperty("options").GetProperty("challenge").GetString()!, credentialId, ecdsa));
        await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.Created);
        return credentialId;
    }

    private static Task<HttpResponseMessage> OptionsResponseAsync(OidcHarness oidc, string email) =>
        oidc.PostFormAsync($"/ui/{Slug}/sign-in/passkey/options", new() { ["email"] = email });

    private static async Task<(string Sealed, string Ceremony)> OptionsAsync(OidcHarness oidc, string email)
    {
        var options = await OidcHarness.JsonAsync(await OptionsResponseAsync(oidc, email));
        return (options.GetProperty("challenge").GetString()!, options.GetProperty("options").GetProperty("challenge").GetString()!);
    }

    private static HttpRequestMessage Assertion(string sealedChallenge, string ceremonyChallenge, byte[] credentialId, ECDsa ecdsa, uint signCount, string email)
    {
        var authData = new List<byte>();
        authData.AddRange(SHA256.HashData(Encoding.ASCII.GetBytes("test.invalid")));
        authData.Add(0x05);
        authData.AddRange([(byte)(signCount >> 24), (byte)(signCount >> 16), (byte)(signCount >> 8), (byte)signCount]);
        var clientData = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["type"] = "webauthn.get",
            ["challenge"] = ceremonyChallenge,
            ["origin"] = OidcHarness.PublicOrigin,
        });
        var rawSignature = ecdsa.SignHash(SHA256.HashData([.. authData, .. SHA256.HashData(clientData)]));
        var r = new byte[32];
        var t = new byte[32];
        Array.Copy(rawSignature, r, 32);
        Array.Copy(rawSignature, 32, t, 0, 32);
        byte[] Der(byte[] v) => [0x02, (byte)v.Length, .. v];
        var signature = new byte[2 + Der(r).Length + Der(t).Length];
        signature[0] = 0x30;
        signature[1] = (byte)(Der(r).Length + Der(t).Length);
        Der(r).CopyTo(signature, 2);
        Der(t).CopyTo(signature, 2 + Der(r).Length);
        var id = PasskeyCredentials.Base64UrlEncode(credentialId);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/ui/{Slug}/sign-in/passkey")
        {
            Content = JsonContent.Create(new
            {
                challenge = sealedChallenge,
                email,
                response = new
                {
                    id,
                    rawId = id,
                    type = "public-key",
                    response = new
                    {
                        clientDataJSON = PasskeyCredentials.Base64UrlEncode(clientData),
                        authenticatorData = PasskeyCredentials.Base64UrlEncode([.. authData]),
                        signature = PasskeyCredentials.Base64UrlEncode(signature),
                    },
                },
            }),
        };
        request.Headers.Add("Origin", OidcHarness.PublicOrigin);
        return request;
    }
}
