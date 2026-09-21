namespace TerraScale.Auth.Connections;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>Verify-only ES256 key built from public JWK coordinates (the Crypto EcdsaSigningKey import path requires private material).</summary>
internal sealed class EcdsaPublicSigningKey : SigningKey
{
    private readonly ECDsa ecdsa = ECDsa.Create();

    public override string Algorithm => "ES256";

    public static EcdsaPublicSigningKey FromCoordinates(byte[] x, byte[] y, string id)
    {
        var key = new EcdsaPublicSigningKey { Id = id };
        key.ecdsa.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
        });
        return key;
    }

    public override byte[] Sign(ReadOnlySpan<byte> input) => throw new NotSupportedException("verify-only key");

    public override bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> signature) =>
        signature.Length == 64
        && ecdsa.VerifyData(input.ToArray(), signature.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public override Dictionary<string, object> ToPublicJwk() => new()
    {
        ["kty"] = "EC",
        ["crv"] = "P-256",
        ["use"] = "sig",
        ["alg"] = "ES256",
        ["kid"] = Id,
        ["x"] = Encodings.Base64UrlEncode(ecdsa.ExportParameters(false).Q.X!),
        ["y"] = Encodings.Base64UrlEncode(ecdsa.ExportParameters(false).Q.Y!),
    };
}
