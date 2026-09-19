namespace TerraScale.Auth.WebAuthn;

using System.Formats.Cbor;

/// <summary>A parsed COSE public key from an authenticator, with signature verification for its algorithm.</summary>
public sealed class CoseKey
{
    private readonly object key;

    private CoseKey(string algorithm, object key, byte[] rawCbor)
    {
        Algorithm = algorithm;
        this.key = key;
        RawCbor = rawCbor;
    }

    /// <summary>JWS algorithm identifier (ES256, RS256, PS256).</summary>
    public string Algorithm { get; }

    /// <summary>The canonical CBOR encoding of the key as delivered by the authenticator; persisted verbatim.</summary>
    public byte[] RawCbor { get; }

    /// <summary>Parses one COSE_Key map (CTAP2 canonical CBOR) starting at <paramref name="offset"/>.</summary>
    public static CoseKey Parse(byte[] buffer, int offset, out int consumed)
    {
        var entries = new Dictionary<int, byte[]?>();
        int? kty = null, alg = null, crv = null;

        var memory = buffer.AsMemory(offset);
        var reader = new CborReader(memory);
        var count = reader.ReadStartMap();
        var definite = count is not null;
        var remaining = count ?? 0;
        while (!definite || remaining > 0)
        {
            var label = reader.ReadInt32();
            if (reader.PeekState() == CborReaderState.Null)
            {
                reader.ReadNull();
            }
            else
            {
                switch (label)
                {
                    case 1: kty = reader.ReadInt32(); break;
                    case 3: alg = reader.ReadInt32(); break;
                    case -1: crv = reader.ReadInt32(); break;
                    case -2:
                    case -3:
                    case -4:
                        entries[label] = reader.ReadByteString(); break;
                    default:
                        reader.SkipValue(); break;
                }
            }
            if (definite) remaining--;
            else if (reader.PeekState() == CborReaderState.EndMap) break;
        }
        reader.ReadEndMap();
        consumed = memory.Length - reader.BytesRemaining;
        var rawCbor = buffer.AsSpan(offset, consumed).ToArray();

        if (kty is null || alg is null)
            throw new FormatException("COSE key missing kty or alg");

        // EC2 (kty 2) with P-256 (crv 1) and ES256 (alg -7).
        if (kty == 2 && alg == -7 && crv == 1
            && entries.TryGetValue(-2, out var x) && entries.TryGetValue(-3, out var y)
            && x is { Length: 32 } && y is { Length: 32 })
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = (byte[])x.Clone(), Y = (byte[])y.Clone() },
            });
            return new CoseKey("ES256", ecdsa, rawCbor);
        }

        // RSA (kty 3) with RS256 (alg -257) or PS256 (alg -37).
        if (kty == 3 && (alg == -257 || alg == -37)
            && entries.TryGetValue(-1, out var n) && entries.TryGetValue(-2, out var e)
            && n is { Length: > 0 } && e is { Length: > 0 })
        {
            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = (byte[])n.Clone(), Exponent = (byte[])e.Clone() });
            return new CoseKey(alg == -257 ? "RS256" : "PS256", rsa, rawCbor);
        }

        throw new FormatException($"unsupported COSE key (kty {kty}, alg {alg}, crv {crv})");
    }

    /// <summary>Reconstructs a verifier from the stored CBOR encoding.</summary>
    public static CoseKey FromStored(byte[] rawCbor) => Parse(rawCbor, 0, out _);

    /// <summary>Encodes an EC2 P-256 ES256 key to canonical CBOR.</summary>
    public static byte[] EncodeEc2(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        var writer = new CborWriter();
        writer.WriteStartMap(5);
        writer.WriteInt32(1); writer.WriteInt32(2);
        writer.WriteInt32(3); writer.WriteInt32(-7);
        writer.WriteInt32(-1); writer.WriteInt32(1);
        writer.WriteInt32(-2); writer.WriteByteString(x);
        writer.WriteInt32(-3); writer.WriteByteString(y);
        writer.WriteEndMap();
        return writer.Encode();
    }

    /// <summary>Exports the EC point coordinates for ES256 keys.</summary>
    public (byte[] X, byte[] Y) ExportEc2Coordinates()
    {
        if (Algorithm != "ES256" || key is not ECDsa ecdsa)
            throw new InvalidOperationException("coordinates are only available for ES256 keys");
        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        return (parameters.Q.X!, parameters.Q.Y!);
    }

    /// <summary>Verifies a WebAuthn signature (ES256 raw r||s or RSA padding) over the signed data.</summary>
    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        switch (Algorithm, key)
        {
            case ("ES256", ECDsa ecdsa):
                return signature.Length == 64
                    && ecdsa.VerifyData(data.ToArray(), signature.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            case ("RS256", RSA rsa256):
                return rsa256.VerifyData(data.ToArray(), signature.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            case ("PS256", RSA rsaPss):
                return rsaPss.VerifyData(data.ToArray(), signature.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            default:
                return false;
        }
    }
}
