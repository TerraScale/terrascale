namespace TerraScale.Auth.WebAuthn;

using System.Formats.Cbor;
using System.Security.Cryptography.X509Certificates;
using TerraScale.Auth.Crypto;

/// <summary>
/// WebAuthn (W3C Level 2) registration and assertion verification, written
/// from scratch: clientDataJSON checks, authenticator data parsing, COSE keys,
/// and "none" / "packed" attestation.
/// </summary>
public static class WebAuthnVerifier
{
    /// <summary>Parsed authenticator data.</summary>
    public sealed record AuthenticatorData(
        byte[] Raw,
        byte[] RpIdHash,
        bool UserPresent,
        bool UserVerified,
        bool AttestedCredentialDataPresent,
        uint SignCount,
        byte[]? Aaguid,
        byte[]? CredentialId,
        CoseKey? CredentialPublicKey);

    /// <summary>Parses and structurally validates authenticator data.</summary>
    public static AuthenticatorData ParseAuthenticatorData(ReadOnlySpan<byte> authData)
    {
        if (authData.Length < 37)
            throw new FormatException("authenticator data too short");

        var rpIdHash = authData[..32].ToArray();
        var flags = authData[32];
        var signCount = BinaryPrimitives.ReadUInt32BigEndian(authData.Slice(33, 4));

        byte[]? aaguid = null;
        byte[]? credentialId = null;
        CoseKey? credentialKey = null;
        var offset = 37;

        if ((flags & 0x40) != 0) // AT: attested credential data
        {
            if (authData.Length < offset + 18)
                throw new FormatException("attested credential data truncated");
            aaguid = authData.Slice(offset, 16).ToArray();
            offset += 16;
            var credentialIdLength = BinaryPrimitives.ReadUInt16BigEndian(authData.Slice(offset, 2));
            offset += 2;
            if (credentialIdLength is 0 or > 1023 || authData.Length < offset + credentialIdLength)
                throw new FormatException("credential id invalid");
            credentialId = authData.Slice(offset, credentialIdLength).ToArray();
            offset += credentialIdLength;

            credentialKey = CoseKey.Parse(authData.ToArray(), offset, out _);
        }

        return new AuthenticatorData(
            authData.ToArray(),
            rpIdHash,
            UserPresent: (flags & 0x01) != 0,
            UserVerified: (flags & 0x04) != 0,
            AttestedCredentialDataPresent: (flags & 0x40) != 0,
            signCount,
            aaguid,
            credentialId,
            credentialKey);
    }

    /// <summary>Validates clientDataJSON: type, challenge, and origin.</summary>
    public static void ValidateClientData(string clientDataJson, string expectedType, string expectedChallenge, string expectedOrigin)
    {
        using var document = JsonDocument.Parse(clientDataJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != expectedType)
            throw new InvalidOperationException("clientData type mismatch");
        if (!root.TryGetProperty("challenge", out var challenge) || challenge.GetString() != expectedChallenge)
            throw new InvalidOperationException("clientData challenge mismatch");
        if (!root.TryGetProperty("origin", out var origin) || !OriginMatches(origin.GetString(), expectedOrigin))
            throw new InvalidOperationException("clientData origin mismatch");
        if (root.TryGetProperty("crossOrigin", out var crossOrigin) && crossOrigin.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("cross-origin ceremonies are not accepted");
    }

    private static bool OriginMatches(string? actual, string expected)
    {
        if (string.IsNullOrEmpty(actual))
            return false;
        return string.Equals(actual.TrimEnd('/'), expected.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies a registration (attestation) ceremony. Returns the stored credential material.</summary>
    public static (byte[] CredentialId, CoseKey PublicKey, uint InitialSignCount) VerifyRegistration(
        string clientDataJson,
        byte[] attestationObject,
        string expectedChallenge,
        string expectedRpId,
        string expectedOrigin,
        bool requireUserVerification = false)
    {
        ValidateClientData(clientDataJson, "webauthn.create", expectedChallenge, expectedOrigin);

        string format;
        Dictionary<string, object> attestationStatement;
        AuthenticatorData authData;
        byte[] authDataBytes;
        try
        {
            var reader = new CborReader(attestationObject);
            reader.ReadStartMap(); // definite map: fmt, attStmt, authData
            string? fmt = null;
            Dictionary<string, object>? attStmt = null;
            byte[]? parsedAuthData = null;
            while (reader.PeekState() != CborReaderState.EndMap)
            {
                var label = ReadAttestationLabel(reader);
                switch (label)
                {
                    case "fmt": fmt = reader.ReadTextString(); break;
                    case "attStmt": attStmt = ReadAttestationStatement(reader); break;
                    case "authData": parsedAuthData = reader.ReadByteString(); break;
                    default: reader.SkipValue(); break;
                }
            }
            reader.ReadEndMap();
            format = fmt ?? throw new FormatException("attestation object missing fmt");
            attestationStatement = attStmt ?? throw new FormatException("attestation object missing attStmt");
            authDataBytes = parsedAuthData ?? throw new FormatException("attestation object missing authData");
            authData = ParseAuthenticatorData(authDataBytes);
        }
        catch (CborContentException exception)
        {
            throw new FormatException("malformed attestation object", exception);
        }

        var rpIdHash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(expectedRpId));
        if (!rpIdHash.AsSpan().SequenceEqual(authData.RpIdHash))
            throw new InvalidOperationException("rpId hash mismatch");
        if (!authData.UserPresent)
            throw new InvalidOperationException("user not present");
        if (requireUserVerification && !authData.UserVerified)
            throw new InvalidOperationException("user verification required");
        if (!authData.AttestedCredentialDataPresent || authData.CredentialId is not { Length: > 0 } || authData.CredentialPublicKey is null)
            throw new InvalidOperationException("attested credential data missing");

        var signedData = BuildSignedData(authDataBytes, clientDataJson);

        switch (format)
        {
            case "none":
                if (attestationStatement.Count != 0)
                    throw new InvalidOperationException("none attestation must have an empty statement");
                break;
            case "packed":
                VerifyPackedAttestation(attestationStatement, signedData, authData.CredentialPublicKey!);
                break;
            default:
                // ponytail: reject unparseable attestation formats rather than accepting on faith;
                // add fido-u2f / android-key / tpm if relying parties need them.
                throw new InvalidOperationException($"unsupported attestation format {format}");
        }

        return (authData.CredentialId, authData.CredentialPublicKey, authData.SignCount);
    }

    private static byte[] BuildSignedData(byte[] authDataBytes, string clientDataJson)
    {
        var clientDataHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(clientDataJson));
        var signed = new byte[authDataBytes.Length + clientDataHash.Length];
        authDataBytes.CopyTo(signed, 0);
        clientDataHash.CopyTo(signed, authDataBytes.Length);
        return signed;
    }

    /// <summary>
    /// Reads an attestation-object map key. The W3C WebAuthn API encodes the keys as
    /// text ("fmt"/"attStmt"/"authData" — what every real browser delivers); raw CTAP2
    /// responses use the integers 1/2/3 instead. Both forms are accepted.
    /// </summary>
    private static string ReadAttestationLabel(CborReader reader) =>
        reader.PeekState() switch
        {
            CborReaderState.TextString => reader.ReadTextString(),
            CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger => reader.ReadInt32() switch
            {
                1 => "fmt",
                2 => "attStmt",
                3 => "authData",
                _ => "",
            },
            _ => throw new FormatException("attestation object map key must be text or 1/2/3"),
        };

    private static Dictionary<string, object> ReadAttestationStatement(CborReader reader)
    {
        var statement = new Dictionary<string, object>();
        var count = reader.ReadStartMap();
        var definite = count is not null;
        var remaining = count ?? 0;
        while (!definite || remaining > 0)
        {
            var key = reader.ReadTextString();
            statement[key] = reader.PeekState() switch
            {
                CborReaderState.ByteString => reader.ReadByteString(),
                CborReaderState.TextString => reader.ReadTextString(),
                CborReaderState.NegativeInteger => reader.ReadInt64(),
                CborReaderState.UnsignedInteger => reader.ReadInt64(),
                CborReaderState.StartArray => ReadCertificateArray(reader),
                _ => throw new FormatException($"unsupported attStmt value for {key}"),
            };
            if (definite) remaining--;
            else if (reader.PeekState() == CborReaderState.EndMap) break;
        }
        reader.ReadEndMap();
        return statement;
    }

    private static List<byte[]> ReadCertificateArray(CborReader reader)
    {
        var certificates = new List<byte[]>();
        var count = reader.ReadStartArray();
        var definite = count is not null;
        var remaining = count ?? 0;
        while (!definite || remaining > 0)
        {
            certificates.Add(reader.ReadByteString());
            if (definite) remaining--;
            else if (reader.PeekState() == CborReaderState.EndArray) break;
        }
        reader.ReadEndArray();
        return certificates;
    }

    private static void VerifyPackedAttestation(Dictionary<string, object> statement, byte[] signedData, CoseKey credentialKey)
    {
        if (!statement.TryGetValue("sig", out var sigValue) || sigValue is not byte[] signature)
            throw new InvalidOperationException("packed attestation missing signature");

        if (statement.TryGetValue("x5c", out var chainValue) && chainValue is List<byte[]> { Count: > 0 } chain)
        {
            // Basic packed attestation: verify with the attestation certificate's key.
            using var certificate = X509CertificateLoader.LoadCertificate(chain[0]);
            if (certificate.GetECDsaPublicKey() is { } ecdsa && CoseKey.VerifyEs256(ecdsa, signedData, signature))
                return;
            if (certificate.GetRSAPublicKey() is { } rsa
                && rsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return;
            throw new InvalidOperationException("packed attestation signature invalid");
        }

        // Self attestation: signature verifies with the credential key itself.
        if (!credentialKey.Verify(signedData, signature))
            throw new InvalidOperationException("packed self-attestation signature invalid");
    }

    /// <summary>Result of a verified assertion.</summary>
    public sealed record AssertionResult(uint SignCount, bool UserVerified);

    /// <summary>Verifies an authentication (assertion) ceremony against a stored credential.</summary>
    public static AssertionResult VerifyAssertion(
        string clientDataJson,
        byte[] authenticatorData,
        byte[] signature,
        string expectedChallenge,
        string expectedRpId,
        string expectedOrigin,
        byte[] credentialId,
        CoseKey credentialPublicKey,
        uint storedSignCount,
        bool requireUserVerification = false)
    {
        ValidateClientData(clientDataJson, "webauthn.get", expectedChallenge, expectedOrigin);

        var authData = ParseAuthenticatorData(authenticatorData);
        var rpIdHash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(expectedRpId));
        if (!rpIdHash.AsSpan().SequenceEqual(authData.RpIdHash))
            throw new InvalidOperationException("rpId hash mismatch");
        if (!authData.UserPresent)
            throw new InvalidOperationException("user not present");
        if (requireUserVerification && !authData.UserVerified)
            throw new InvalidOperationException("user verification required");
        if (authData.CredentialId is not null && !authData.CredentialId.AsSpan().SequenceEqual(credentialId))
            throw new InvalidOperationException("assertion user handle/credential mismatch");

        var clientDataHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(clientDataJson));
        var signed = new byte[authenticatorData.Length + clientDataHash.Length];
        authenticatorData.CopyTo(signed, 0);
        clientDataHash.CopyTo(signed, authenticatorData.Length);

        if (!credentialPublicKey.Verify(signed, signature))
            throw new InvalidOperationException("assertion signature invalid");

        // W3C L2 §7.2 step 21: if either counter is nonzero, the new one must be strictly
        // greater. 0 means "counter unsupported" (both zero passes); but a credential that
        // stored a nonzero counter must never be allowed to zero it out — a clone can
        // otherwise suppress detection by always reporting 0.
        if ((authData.SignCount != 0 || storedSignCount != 0) && authData.SignCount <= storedSignCount)
            throw new InvalidOperationException("authenticator may be cloned (sign count did not advance)");

        return new AssertionResult(authData.SignCount, authData.UserVerified);
    }

}
