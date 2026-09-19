namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>Passkey registration and discoverable sign-in through the real service.</summary>
[NotInParallel]
public sealed class PasskeyE2eTests
{
    private const string RpId = "auth.test";
    private const string Origin = "https://auth.test";

    [Test]
    public async Task RegistersAndSignsInWithADiscoverableCredential()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, _, user, cookie) = await ArrangeAsync();
        using var authenticator = new FakeAuthenticator();

        var unauthenticated = await service.Http.PostAsync($"/ui/{slug}/passkeys/register/options", content: null);
        await Assert.That(unauthenticated.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        var options = await OptionsAsync(service, slug, cookie);
        await Assert.That(options.GetProperty("publicKey").GetProperty("rp").GetProperty("id").GetString()).IsEqualTo(RpId);
        await Assert.That(options.GetProperty("publicKey").GetProperty("rp").GetProperty("name").GetString()).IsEqualTo("ts-auth");
        await Assert.That(options.GetProperty("publicKey").GetProperty("excludeCredentials").GetArrayLength()).IsEqualTo(0);
        var userHandle = options.GetProperty("publicKey").GetProperty("user").GetProperty("id").GetString()!;
        await Assert.That(Encoding.UTF8.GetString(Encodings.Base64UrlDecode(userHandle))).IsEqualTo(user.Id);

        var registration = await RegisterAsync(service, slug, cookie, authenticator);
        await Assert.That(registration.Algorithm).IsEqualTo("ES256");
        await Assert.That(registration.Nickname).IsEqualTo("Test key");
        await Assert.That(registration.Transports).IsEqualTo("internal");
        await Assert.That(registration.UserId).IsEqualTo(user.Id);
        await Assert.That(registration.PublicKeyCborBase64).IsNotNull();

        // A registered credential is excluded from the next registration ceremony.
        var second = await OptionsAsync(service, slug, cookie);
        var excluded = second.GetProperty("publicKey").GetProperty("excludeCredentials");
        await Assert.That(excluded.GetArrayLength()).IsEqualTo(1);
        await Assert.That(excluded[0].GetProperty("id").GetString()).IsEqualTo(authenticator.CredentialKey);

        var before = await service.Store.ListSessionsByUserAsync(user.Id);
        var assertionOptions = await service.Http.PostAsync($"/ui/{slug}/passkeys/assert/options", content: null);
        await Assert.That(assertionOptions.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var assertionBody = await assertionOptions.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(assertionBody.GetProperty("publicKey").GetProperty("rpId").GetString()).IsEqualTo(RpId);
        var challenge = assertionBody.GetProperty("publicKey").GetProperty("challenge").GetString()!;

        var (authData, signature) = authenticator.Assertion(challenge);
        var verification = await service.Http.PostAsJsonAsync($"/ui/{slug}/passkeys/assert/verify", new
        {
            id = authenticator.CredentialKey,
            response = new
            {
                clientDataJSON = Encodings.Base64UrlEncode(Encoding.UTF8.GetBytes(authenticator.ClientDataJson("webauthn.get", challenge))),
                authenticatorData = Encodings.Base64UrlEncode(authData),
                signature = Encodings.Base64UrlEncode(signature),
            },
        });
        await Assert.That(verification.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var verified = await verification.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(verified.GetProperty("redirect").GetString()).IsEqualTo($"/ui/{slug}/account");

        var after = await service.Store.ListSessionsByUserAsync(user.Id);
        await Assert.That(after.Count).IsEqualTo(before.Count + 1);
        var setCookie = string.Join("; ", verification.Headers.GetValues("Set-Cookie"));
        await Assert.That(setCookie.Contains("tsauth_session=")).IsTrue();
        var used = await service.Store.FindPasskeyByCredentialIdAsync(authenticator.CredentialKey);
        await Assert.That(used!.LastUsedAt).IsNotNull();
        await Assert.That(used.SignCount).IsEqualTo(registration.SignCount + 1);

        var signedIn = await service.Store.GetUserAsync(user.Id);
        await Assert.That(signedIn!.LastSignInAt).IsNotNull();
        var audits = await service.Store.ListAuditEventsAsync(instanceId, 100);
        var signIn = audits.FirstOrDefault(e => e.Type == "user.signed_in" && e.ActorId == user.Id);
        await Assert.That(signIn is not null && signIn.Data.ContainsKey("passkeyId")).IsTrue();
    }

    [Test]
    public async Task DuplicateCredentialIsRejected()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (_, slug, _, _, cookie) = await ArrangeAsync();
        using var authenticator = new FakeAuthenticator();

        await RegisterAsync(service, slug, cookie, authenticator);

        var options = await OptionsAsync(service, slug, cookie);
        var challenge = options.GetProperty("publicKey").GetProperty("challenge").GetString()!;
        var duplicate = await service.Http.SendAsync(POST($"/ui/{slug}/passkeys/register", cookie, new
        {
            id = authenticator.CredentialKey,
            nickname = "Again",
            response = new
            {
                clientDataJSON = Encodings.Base64UrlEncode(Encoding.UTF8.GetBytes(authenticator.ClientDataJson("webauthn.create", challenge))),
                attestationObject = Encodings.Base64UrlEncode(authenticator.AttestationObject()),
            },
        }));
        await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task RegistrationChallengeIsSingleUse()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (_, slug, _, user, cookie) = await ArrangeAsync();
        using var authenticator = new FakeAuthenticator();
        using var replaying = new FakeAuthenticator();

        var challenge = (await OptionsAsync(service, slug, cookie))
            .GetProperty("publicKey").GetProperty("challenge").GetString()!;
        var first = await service.Http.SendAsync(POST($"/ui/{slug}/passkeys/register", cookie, new
        {
            id = authenticator.CredentialKey,
            response = new
            {
                clientDataJSON = Encodings.Base64UrlEncode(Encoding.UTF8.GetBytes(authenticator.ClientDataJson("webauthn.create", challenge))),
                attestationObject = Encodings.Base64UrlEncode(authenticator.AttestationObject()),
            },
        }));
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // A fresh credential replaying the consumed challenge must be rejected, not registered.
        var replay = await service.Http.SendAsync(POST($"/ui/{slug}/passkeys/register", cookie, new
        {
            id = replaying.CredentialKey,
            response = new
            {
                clientDataJSON = Encodings.Base64UrlEncode(Encoding.UTF8.GetBytes(replaying.ClientDataJson("webauthn.create", challenge))),
                attestationObject = Encodings.Base64UrlEncode(replaying.AttestationObject()),
            },
        }));
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await service.Store.ListPasskeysAsync(user.Id)).Count).IsEqualTo(1);
    }

    [Test]
    public async Task RegistrationRejectsForeignOrigin()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (_, slug, _, _, cookie) = await ArrangeAsync();
        using var authenticator = new FakeAuthenticator();

        var options = await OptionsAsync(service, slug, cookie);
        var challenge = options.GetProperty("publicKey").GetProperty("challenge").GetString()!;
        var registration = await service.Http.SendAsync(POST($"/ui/{slug}/passkeys/register", cookie, new
        {
            id = authenticator.CredentialKey,
            response = new
            {
                clientDataJSON = Encodings.Base64UrlEncode(Encoding.UTF8.GetBytes(
                    authenticator.ClientDataJson("webauthn.create", challenge, originOverride: "https://evil.example"))),
                attestationObject = Encodings.Base64UrlEncode(authenticator.AttestationObject()),
            },
        }));
        await Assert.That(registration.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await service.Store.FindPasskeyByCredentialIdAsync(authenticator.CredentialKey)).IsNull();
    }

    [Test]
    public async Task AssertionRejectsForeignOrigin()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (_, slug, _, _, cookie) = await ArrangeAsync();
        using var authenticator = new FakeAuthenticator();
        await RegisterAsync(service, slug, cookie, authenticator);

        var challenge = await AssertOptionsChallengeAsync(service, slug);
        var (authData, signature) = authenticator.Assertion(challenge);
        var verification = await service.Http.PostAsJsonAsync($"/ui/{slug}/passkeys/assert/verify", new
        {
            id = authenticator.CredentialKey,
            response = new
            {
                clientDataJSON = Encodings.Base64UrlEncode(Encoding.UTF8.GetBytes(
                    authenticator.ClientDataJson("webauthn.get", challenge, originOverride: "https://evil.example"))),
                authenticatorData = Encodings.Base64UrlEncode(authData),
                signature = Encodings.Base64UrlEncode(signature),
            },
        });
        await Assert.That(verification.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        var body = await verification.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsNotNull();
    }

    [Test]
    public async Task AssertionRejectsUnknownCredential()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (_, slug, _, _, _) = await ArrangeAsync();
        using var authenticator = new FakeAuthenticator();

        var challenge = await AssertOptionsChallengeAsync(service, slug);
        var (authData, signature) = authenticator.Assertion(challenge);
        var verification = await service.Http.PostAsJsonAsync($"/ui/{slug}/passkeys/assert/verify", new
        {
            id = Encodings.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            response = new
            {
                clientDataJSON = Encodings.Base64UrlEncode(Encoding.UTF8.GetBytes(authenticator.ClientDataJson("webauthn.get", challenge))),
                authenticatorData = Encodings.Base64UrlEncode(authData),
                signature = Encodings.Base64UrlEncode(signature),
            },
        });
        await Assert.That(verification.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task ManagementApiListsAndDeletesPasskeys()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey, user, cookie) = await ArrangeAsync();
        using var authenticator = new FakeAuthenticator();
        var registered = await RegisterAsync(service, slug, cookie, authenticator);

        var unauthorized = await service.Http.GetAsync($"/v1/{slug}/users/{user.Id}/passkeys");
        await Assert.That(unauthorized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/{slug}/users/{user.Id}/passkeys");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var listResponse = await service.Http.SendAsync(listRequest);
        await Assert.That(listResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var passkeys = list.GetProperty("passkeys");
        await Assert.That(passkeys.GetArrayLength()).IsEqualTo(1);
        var entry = passkeys[0];
        await Assert.That(entry.GetProperty("id").GetString()).IsEqualTo(registered.Id);
        await Assert.That(entry.GetProperty("nickname").GetString()).IsEqualTo("Test key");
        await Assert.That(entry.GetProperty("algorithm").GetString()).IsEqualTo("ES256");
        await Assert.That(entry.EnumerateObject().Any(p => p.Name.Contains("publicKey") || p.Name.Contains("credentialKey"))).IsFalse();

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/v1/{slug}/users/{user.Id}/passkeys/{registered.Id}");
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var deleteResponse = await service.Http.SendAsync(deleteRequest);
        await Assert.That(deleteResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await service.Store.FindPasskeyByCredentialIdAsync(authenticator.CredentialKey)).IsNull();

        using var listAgain = new HttpRequestMessage(HttpMethod.Get, $"/v1/{slug}/users/{user.Id}/passkeys");
        listAgain.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var empty = await (await service.Http.SendAsync(listAgain)).Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(empty.GetProperty("passkeys").GetArrayLength()).IsEqualTo(0);

        var audits = await service.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Any(e => e.Type == "passkey.created")).IsTrue();
        await Assert.That(audits.Any(e => e.Type == "passkey.deleted")).IsTrue();
    }

    [Test]
    public async Task ManagePageListsAndDeletesOwnPasskeys()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (_, slug, _, user, cookie) = await ArrangeAsync();
        using var authenticator = new FakeAuthenticator();
        var registered = await RegisterAsync(service, slug, cookie, authenticator);

        var anonymous = await service.Http.GetAsync($"/ui/{slug}/passkeys/manage");
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        using var page = new HttpRequestMessage(HttpMethod.Get, $"/ui/{slug}/passkeys/manage");
        page.Headers.Add("Cookie", cookie);
        var pageResponse = await service.Http.SendAsync(page);
        await Assert.That(pageResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await pageResponse.Content.ReadAsStringAsync();
        await Assert.That(html.Contains("Test key")).IsTrue();
        await Assert.That(html.Contains($"/ui/{slug}/passkeys/manage/{registered.Id}/delete")).IsTrue();

        using var delete = new HttpRequestMessage(HttpMethod.Post, $"/ui/{slug}/passkeys/manage/{registered.Id}/delete");
        delete.Headers.Add("Cookie", cookie);
        var deleteResponse = await service.Http.SendAsync(delete);
        await Assert.That(deleteResponse.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(deleteResponse.Headers.Location?.ToString()).IsEqualTo($"/ui/{slug}/passkeys/manage");
        await Assert.That(await service.Store.FindPasskeyByCredentialIdAsync(authenticator.CredentialKey)).IsNull();
        await Assert.That((await service.Store.ListPasskeysAsync(user.Id)).Count).IsEqualTo(0);
    }

    // ---- Arranging and driving the ceremonies --------------------------------------------

    private static async Task<(string InstanceId, string Slug, string ApiKey, User User, string Cookie)> ArrangeAsync()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await service.CreateInstanceAsync("passkeys-" + Guid.NewGuid().ToString("N")[..8]);

        var sessions = new SessionService(service.Store);
        var user = await sessions.CreateUserAsync(instanceId, $"pk-{Guid.NewGuid().ToString("N")[..8]}@example.com", null, null, "Pat Doe");
        var (_, token) = await sessions.CreateSessionAsync(instanceId, user.Id, userAgent: null, ipAddress: null);
        return (instanceId, slug, apiKey, user, $"tsauth_session={token}");
    }

    private static async Task<JsonElement> OptionsAsync(AuthServiceFixture service, string slug, string cookie)
    {
        using var request = POST($"/ui/{slug}/passkeys/register/options", cookie, null);
        var response = await service.Http.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Passkey> RegisterAsync(AuthServiceFixture service, string slug, string cookie, FakeAuthenticator authenticator)
    {
        var challenge = (await OptionsAsync(service, slug, cookie))
            .GetProperty("publicKey").GetProperty("challenge").GetString()!;
        var response = await service.Http.SendAsync(POST($"/ui/{slug}/passkeys/register", cookie, new
        {
            id = authenticator.CredentialKey,
            nickname = "Test key",
            response = new
            {
                clientDataJSON = Encodings.Base64UrlEncode(Encoding.UTF8.GetBytes(authenticator.ClientDataJson("webauthn.create", challenge))),
                attestationObject = Encodings.Base64UrlEncode(authenticator.AttestationObject()),
                transports = new[] { "internal" },
            },
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var stored = await service.Store.FindPasskeyByCredentialIdAsync(authenticator.CredentialKey);
        await Assert.That(stored).IsNotNull();
        return stored!;
    }

    private static async Task<string> AssertOptionsChallengeAsync(AuthServiceFixture service, string slug)
    {
        var response = await service.Http.PostAsync($"/ui/{slug}/passkeys/assert/options", content: null);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("publicKey").GetProperty("challenge").GetString()!;
    }

    private static HttpRequestMessage POST(string url, string? cookie, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    /// <summary>Builds authenticator-side material with a real P-256 keypair for the test RP.</summary>
    internal sealed class FakeAuthenticator(string rpId = RpId, string origin = Origin) : IDisposable
    {
        private readonly ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private uint signCount = 5;

        public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

        public string CredentialKey => Encodings.Base64UrlEncode(CredentialId);

        public string ClientDataJson(string type, string challenge, string? originOverride = null) =>
            $$"""{"type":"{{type}}","challenge":"{{challenge}}","origin":"{{originOverride ?? origin}}"}""";

        public byte[] AttestationObject()
        {
            var authData = AuthenticatorData(attested: true);
            var writer = new System.Formats.Cbor.CborWriter();
            writer.WriteStartMap(3);
            writer.WriteInt32(1); writer.WriteTextString("none");
            writer.WriteInt32(2); writer.WriteStartMap(0); writer.WriteEndMap();
            writer.WriteInt32(3); writer.WriteByteString(authData);
            writer.WriteEndMap();
            return writer.Encode();
        }

        public (byte[] AuthData, byte[] Signature) Assertion(string challenge)
        {
            signCount++; // advance the counter like a real token
            var authData = AuthenticatorData(attested: false);
            var clientDataJson = ClientDataJson("webauthn.get", challenge);
            var signed = authData.Concat(SHA256.HashData(Encoding.UTF8.GetBytes(clientDataJson))).ToArray();
            var signature = ecdsa.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return (authData, signature);
        }

        private byte[] AuthenticatorData(bool attested)
        {
            using var stream = new MemoryStream();
            stream.Write(SHA256.HashData(Encoding.ASCII.GetBytes(rpId)));
            stream.WriteByte(attested ? (byte)0x45 : (byte)0x05); // UP | UV [| AT]
            var count = signCount;
            stream.Write([(byte)(count >> 24), (byte)(count >> 16), (byte)(count >> 8), (byte)count]);
            if (attested)
            {
                stream.Write(new byte[16]);
                stream.Write([(byte)(CredentialId.Length >> 8), (byte)CredentialId.Length]);
                stream.Write(CredentialId);
                var parameters = ecdsa.ExportParameters(false);
                stream.Write(WebAuthn.CoseKey.EncodeEc2(parameters.Q.X!, parameters.Q.Y!));
            }
            return stream.ToArray();
        }

        public void Dispose() => ecdsa.Dispose();
    }
}
