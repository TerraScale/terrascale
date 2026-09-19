namespace TerraScale.Auth.Tests;

using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.WebAuthn;

public sealed class WebAuthnTests
{
    private const string RpId = "login.example.com";
    private const string Origin = "https://login.example.com";

    [Test]
    public async Task RegistrationAndAssertionRoundTrip()
    {
        using var authenticator = new FakeAuthenticator();
        var challenge = NewChallenge();

        var (credentialId, publicKey, signCount) = WebAuthnVerifier.VerifyRegistration(
            authenticator.ClientDataJson("webauthn.create", challenge),
            authenticator.AttestationObject(),
            challenge, RpId, Origin);
        await Assert.That(signCount).IsEqualTo(5u);
        await Assert.That(credentialId.Length).IsEqualTo(32);

        var challenge2 = NewChallenge();
        var (assertionData, assertionSignature) = authenticator.Assertion(challenge2);
        var result = WebAuthnVerifier.VerifyAssertion(
            authenticator.ClientDataJson("webauthn.get", challenge2),
            assertionData,
            assertionSignature,
            challenge2, RpId, Origin, credentialId, publicKey, storedSignCount: 5);
        await Assert.That(result.SignCount).IsEqualTo(6u);

        // Replayed sign count is rejected (clone detection).
        var challenge3 = NewChallenge();
        var (replayedData, replayedSignature) = authenticator.Assertion(challenge3);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => WebAuthnVerifier.VerifyAssertion(
            authenticator.ClientDataJson("webauthn.get", challenge3),
            replayedData,
            replayedSignature,
            challenge3, RpId, Origin, credentialId, publicKey, storedSignCount: 7)));
    }

    [Test]
    public async Task RegistrationRejectsWrongOrigin()
    {
        using var authenticator = new FakeAuthenticator();
        var challenge = NewChallenge();
        var clientDataJson = $$"""{"type":"webauthn.create","challenge":"{{challenge}}","origin":"https://evil.example"}""";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
            WebAuthnVerifier.VerifyRegistration(clientDataJson, authenticator.AttestationObject(), challenge, RpId, Origin)));
    }

    [Test]
    public async Task RegistrationRejectsWrongChallenge()
    {
        using var authenticator = new FakeAuthenticator();
        var challenge = NewChallenge();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
            WebAuthnVerifier.VerifyRegistration(
                authenticator.ClientDataJson("webauthn.create", challenge),
                authenticator.AttestationObject(),
                "other-challenge", RpId, Origin)));
    }

    [Test]
    public async Task RegistrationRejectsWrongRpId()
    {
        using var authenticator = new FakeAuthenticator();
        var challenge = NewChallenge();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
            WebAuthnVerifier.VerifyRegistration(
                authenticator.ClientDataJson("webauthn.create", challenge),
                authenticator.AttestationObject(),
                challenge, "other-rp.example.com", Origin)));
    }

    [Test]
    public async Task AssertionRejectsBadSignature()
    {
        using var authenticator = new FakeAuthenticator();
        var challenge = NewChallenge();
        var (credentialId, publicKey, _) = WebAuthnVerifier.VerifyRegistration(
            authenticator.ClientDataJson("webauthn.create", challenge),
            authenticator.AttestationObject(), challenge, RpId, Origin);

        var authData = authenticator.AuthenticatorData(attested: false);
        var clientDataJson = authenticator.ClientDataJson("webauthn.get", NewChallenge());
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = authData.Concat(SHA256.HashData(Encoding.UTF8.GetBytes(clientDataJson))).ToArray();
        var signature = stranger.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
            WebAuthnVerifier.VerifyAssertion(
                clientDataJson, authData, signature, challenge, RpId, Origin, credentialId, publicKey, storedSignCount: 5)));
    }

    [Test]
    public async Task FirstAssertionWithZeroSignCountsIsAccepted()
    {
        // Counter-less authenticators report 0 forever: register at 0, assert at 0 — no clone flag.
        using var authenticator = new FakeAuthenticator();
        var challenge = NewChallenge();
        var (credentialId, publicKey, signCount) = WebAuthnVerifier.VerifyRegistration(
            authenticator.ClientDataJson("webauthn.create", challenge),
            authenticator.AttestationObject(signCountOverride: 0),
            challenge, RpId, Origin);
        await Assert.That(signCount).IsEqualTo(0u);

        var challenge2 = NewChallenge();
        var (authData, signature) = authenticator.Assertion(challenge2, signCountOverride: 0);
        var result = WebAuthnVerifier.VerifyAssertion(
            authenticator.ClientDataJson("webauthn.get", challenge2), authData, signature,
            challenge2, RpId, Origin, credentialId, publicKey, storedSignCount: 0);
        await Assert.That(result.SignCount).IsEqualTo(0u);
    }

    [Test]
    public async Task AssertionZeroingTheCounterAgainstAStoredCountIsRejected()
    {
        // A credential that stored a nonzero counter must not be allowed to zero it out:
        // otherwise a clone suppresses detection by always reporting 0.
        using var authenticator = new FakeAuthenticator();
        var challenge = NewChallenge();
        var (credentialId, publicKey, _) = WebAuthnVerifier.VerifyRegistration(
            authenticator.ClientDataJson("webauthn.create", challenge),
            authenticator.AttestationObject(), challenge, RpId, Origin);

        var challenge2 = NewChallenge();
        var (authData, signature) = authenticator.Assertion(challenge2, signCountOverride: 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
            WebAuthnVerifier.VerifyAssertion(
                authenticator.ClientDataJson("webauthn.get", challenge2), authData, signature,
                challenge2, RpId, Origin, credentialId, publicKey, storedSignCount: 5)));
    }

    [Test]
    public async Task AssertionRejectsMismatchedCredentialIdInAuthData()
    {
        using var authenticator = new FakeAuthenticator();
        var challenge = NewChallenge();
        var (credentialId, publicKey, _) = WebAuthnVerifier.VerifyRegistration(
            authenticator.ClientDataJson("webauthn.create", challenge),
            authenticator.AttestationObject(), challenge, RpId, Origin);

        // Validly signed by the real key, but authData carries a foreign credential id.
        var challenge2 = NewChallenge();
        var (authData, signature) = authenticator.Assertion(challenge2, credentialIdOverride: RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
            WebAuthnVerifier.VerifyAssertion(
                authenticator.ClientDataJson("webauthn.get", challenge2), authData, signature,
                challenge2, RpId, Origin, credentialId, publicKey, storedSignCount: 5)));
    }

    [Test]
    public async Task PackedSelfAttestationSignatureIsVerified()
    {
        using var authenticator = new FakeAuthenticator();
        var challenge = NewChallenge();
        var clientDataJson = authenticator.ClientDataJson("webauthn.create", challenge);
        var attestation = authenticator.AttestationObject(clientDataJson, format: "packed");

        var verified = WebAuthnVerifier.VerifyRegistration(clientDataJson, attestation, challenge, RpId, Origin);
        await Assert.That(verified.CredentialId.SequenceEqual(authenticator.CredentialId)).IsTrue();

        var tampered = authenticator.AttestationObject(clientDataJson, format: "packed", tamperSignature: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
            WebAuthnVerifier.VerifyRegistration(clientDataJson, tampered, challenge, RpId, Origin)));
    }

    [Test]
    public async Task StoredCborRoundTrips()
    {
        using var authenticator = new FakeAuthenticator();
        var challenge = NewChallenge();
        var (_, publicKey, _) = WebAuthnVerifier.VerifyRegistration(
            authenticator.ClientDataJson("webauthn.create", challenge),
            authenticator.AttestationObject(), challenge, RpId, Origin);

        var restored = CoseKey.FromStored(publicKey.RawCbor);
        await Assert.That(restored.Algorithm).IsEqualTo("ES256");
        var (storedData, storedSignature) = authenticator.Assertion(challenge);
        var signedInput = storedData.Concat(SHA256.HashData(Encoding.UTF8.GetBytes(authenticator.ClientDataJson("webauthn.get", challenge)))).ToArray();
        await Assert.That(restored.Verify(signedInput, storedSignature)).IsTrue();
    }

    [Test]
    public async Task RsaCredentialsRegisterAssertAndRoundTrip()
    {
        // RSA COSE keys (RFC 8230) carry the modulus at label -1 as a byte string,
        // where EC2 keys carry the curve as an int; both paths must work.
        foreach (var algorithm in new[] { "RS256", "PS256" })
        {
            using var authenticator = new FakeAuthenticator(algorithm);
            var challenge = NewChallenge();
            var (credentialId, publicKey, _) = WebAuthnVerifier.VerifyRegistration(
                authenticator.ClientDataJson("webauthn.create", challenge),
                authenticator.AttestationObject(),
                challenge, RpId, Origin);
            await Assert.That(publicKey.Algorithm).IsEqualTo(algorithm);

            var restored = CoseKey.FromStored(publicKey.RawCbor);
            await Assert.That(restored.Algorithm).IsEqualTo(algorithm);
            await Assert.That(restored.RawCbor.AsSpan().SequenceEqual(publicKey.RawCbor)).IsTrue();

            var challenge2 = NewChallenge();
            var (authData, signature) = authenticator.Assertion(challenge2);
            var result = WebAuthnVerifier.VerifyAssertion(
                authenticator.ClientDataJson("webauthn.get", challenge2), authData, signature,
                challenge2, RpId, Origin, credentialId, restored, storedSignCount: 5);
            await Assert.That(result.SignCount).IsEqualTo(6u);

            var challenge3 = NewChallenge();
            var (tamperedData, tamperedSignature) = authenticator.Assertion(challenge3);
            tamperedSignature[10] ^= 0xFF;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => WebAuthnVerifier.VerifyAssertion(
                authenticator.ClientDataJson("webauthn.get", challenge3), tamperedData, tamperedSignature,
                challenge3, RpId, Origin, credentialId, restored, storedSignCount: 6)));
        }
    }

    [Test]
    public async Task RegistrationRequiresUserPresentFlag()
    {
        using var authenticator = new FakeAuthenticator();
        var challenge = NewChallenge();
        var clientDataJson = authenticator.ClientDataJson("webauthn.create", challenge);
        // AT set but UP clear: the authenticator claims nobody was present.
        var attestation = authenticator.AttestationObject(flagsOverride: 0x44);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
            WebAuthnVerifier.VerifyRegistration(clientDataJson, attestation, challenge, RpId, Origin)));
    }

    [Test]
    public async Task NoneAttestationMustHaveEmptyStatement()
    {
        using var authenticator = new FakeAuthenticator();
        var challenge = NewChallenge();
        var clientDataJson = authenticator.ClientDataJson("webauthn.create", challenge);

        var writer = new CborWriter();
        writer.WriteStartMap(3);
        writer.WriteInt32(1); writer.WriteTextString("none");
        writer.WriteInt32(2);
        writer.WriteStartMap(1);
        writer.WriteTextString("sig"); writer.WriteByteString(new byte[64]); // junk in a "none" attStmt
        writer.WriteEndMap();
        writer.WriteInt32(3); writer.WriteByteString(authenticator.AuthenticatorData(attested: true));
        writer.WriteEndMap();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
            WebAuthnVerifier.VerifyRegistration(clientDataJson, writer.Encode(), challenge, RpId, Origin)));
    }

    private static string NewChallenge() => Encodings.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Builds authenticator-side bytes with a real P-256 or RSA keypair, signing where the protocol signs.</summary>
    private sealed class FakeAuthenticator(string keyType = "ES256") : IDisposable
    {
        private readonly ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly RSA? rsa = keyType == "ES256" ? null : RSA.Create(2048);
        private uint signCount = 5;

        public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

        public string ClientDataJson(string type, string challenge) =>
            $$"""{"type":"{{type}}","challenge":"{{challenge}}","origin":"{{Origin}}"}""";

        public byte[] AuthenticatorData(bool attested, byte? flagsOverride = null, uint? signCountOverride = null, byte[]? credentialIdOverride = null)
        {
            using var stream = new MemoryStream();
            stream.Write(SHA256.HashData(Encoding.ASCII.GetBytes(RpId)));
            var flags = attested ? (byte)0x45 : (byte)0x05; // UP | UV [| AT]
            stream.WriteByte(flagsOverride ?? flags);
            var count = signCountOverride ?? signCount;
            stream.Write([(byte)(count >> 24), (byte)(count >> 16), (byte)(count >> 8), (byte)count]);
            if (attested)
            {
                var credentialId = credentialIdOverride ?? CredentialId;
                stream.Write(new byte[16]);
                stream.Write([(byte)(credentialId.Length >> 8), (byte)credentialId.Length]);
                stream.Write(credentialId);
                if (rsa is null)
                {
                    var parameters = ecdsa.ExportParameters(false);
                    stream.Write(CoseKey.EncodeEc2(parameters.Q.X!, parameters.Q.Y!));
                }
                else
                {
                    var parameters = rsa.ExportParameters(false);
                    var writer = new CborWriter();
                    writer.WriteStartMap(4);
                    writer.WriteInt32(1); writer.WriteInt32(3); // kty: RSA
                    writer.WriteInt32(3); writer.WriteInt32(keyType == "RS256" ? -257 : -37);
                    writer.WriteInt32(-1); writer.WriteByteString(parameters.Modulus!); // n (bstr per RFC 8230)
                    writer.WriteInt32(-2); writer.WriteByteString(parameters.Exponent!); // e
                    writer.WriteEndMap();
                    stream.Write(writer.Encode());
                }
            }
            return stream.ToArray();
        }

        private byte[] Sign(byte[] data) => rsa is null
            ? ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            : rsa.SignData(data, HashAlgorithmName.SHA256, keyType == "RS256" ? RSASignaturePadding.Pkcs1 : RSASignaturePadding.Pss);

        public byte[] AttestationObject(string clientDataJson = "", string format = "none", bool tamperSignature = false, byte? flagsOverride = null, uint? signCountOverride = null)
        {
            var authData = AuthenticatorData(attested: true, flagsOverride, signCountOverride);
            var writer = new CborWriter();
            writer.WriteStartMap(3);
            writer.WriteInt32(1); writer.WriteTextString(format);
            writer.WriteInt32(2);
            writer.WriteStartMap(format == "packed" ? 1 : 0);
            if (format == "packed")
            {
                var signed = authData.Concat(SHA256.HashData(Encoding.UTF8.GetBytes(clientDataJson))).ToArray();
                var signature = Sign(signed);
                if (tamperSignature)
                    signature[10] ^= 0xFF;
                writer.WriteTextString("sig"); writer.WriteByteString(signature);
            }
            writer.WriteEndMap();
            writer.WriteInt32(3); writer.WriteByteString(authData);
            writer.WriteEndMap();
            return writer.Encode();
        }

        public (byte[] AuthData, byte[] Signature) Assertion(string challenge, uint? signCountOverride = null, byte[]? credentialIdOverride = null)
        {
            signCount++; // advance the counter like a real token
            var authData = AuthenticatorData(attested: credentialIdOverride is not null, signCountOverride: signCountOverride, credentialIdOverride: credentialIdOverride);
            var signed = authData.Concat(SHA256.HashData(Encoding.UTF8.GetBytes(ClientDataJson("webauthn.get", challenge)))).ToArray();
            var signature = Sign(signed);
            return (authData, signature);
        }

        public void Dispose() => ecdsa.Dispose();
    }
}
