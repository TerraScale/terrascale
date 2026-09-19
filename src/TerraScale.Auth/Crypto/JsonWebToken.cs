namespace TerraScale.Auth.Crypto;

/// <summary>
/// JWT encode/sign/verify (JWS compact serialization), written from scratch.
/// HS256, RS256, and ES256. ES256 signatures use raw JWS (r||s) form.
/// </summary>
public static class JsonWebToken
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Signs a token with the given key material.</summary>
    public static string Sign<TClaims>(TClaims claims, DateTimeOffset? expires, SigningKey key)
    {
        var header = new Dictionary<string, object> { ["alg"] = key.Algorithm, ["typ"] = "JWT", ["kid"] = key.Id };
        var payload = claims is null
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(claims, Json), Json) ?? [];

        if (expires is not null)
            payload["exp"] = expires.Value.ToUnixTimeSeconds();
        if (!payload.ContainsKey("iat"))
            payload["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var headerSegment = Encodings.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header, Json));
        var payloadSegment = Encodings.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, Json));
        var signingInput = System.Text.Encoding.ASCII.GetBytes($"{headerSegment}.{payloadSegment}");
        var signature = key.Sign(signingInput);
        return $"{headerSegment}.{payloadSegment}.{Encodings.Base64UrlEncode(signature)}";
    }

    /// <summary>
    /// Verifies signature and expiry; returns the claims or null. The caller is
    /// responsible for issuer/audience checks on the returned claims.
    /// </summary>
    public static Dictionary<string, object>? Verify(string token, Func<string, SigningKey?> resolveKey)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            return null;
        byte[] headerBytes, payloadBytes;
        try
        {
            headerBytes = Encodings.Base64UrlDecode(parts[0]);
            payloadBytes = Encodings.Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        Dictionary<string, object>? header;
        Dictionary<string, object>? payload;
        try
        {
            header = JsonSerializer.Deserialize<Dictionary<string, object>>(headerBytes, Json);
            payload = JsonSerializer.Deserialize<Dictionary<string, object>>(payloadBytes, Json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (header is null || payload is null)
            return null;

        var algorithm = GetString(header, "alg");
        if (string.IsNullOrEmpty(algorithm))
            return null;
        // The algorithm comes from attacker-controlled input; resolveKey must
        // return a key whose own algorithm matches before any signature check runs.
        var key = resolveKey(GetString(header, "kid") ?? string.Empty);
        if (key is null || key.Algorithm != algorithm)
            return null;

        var signature = Encodings.Base64UrlDecode(parts[2]);
        var signed = System.Text.Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        if (!key.Verify(signed, signature))
            return null;

        if (payload.TryGetValue("exp", out var exp) && exp is JsonElement { ValueKind: JsonValueKind.Number } expElement
            && expElement.TryGetInt64(out var expires) && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expires)
            return null;

        return payload;
    }

    private static string? GetString(Dictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value)
            ? value as string ?? (value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : null)
            : null;

    /// <summary>Decodes payload claims without verification — for debugging and display only.</summary>
    public static Dictionary<string, object>? DecodeUnverified(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(Encodings.Base64UrlDecode(parts[1]), Json);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>A signing key with an id and algorithm, exposing sign/verify and a JWK representation.</summary>
public abstract class SigningKey
{
    /// <summary>Key id used in JWT headers and JWKS.</summary>
    public required string Id { get; init; }

    /// <summary>JWS algorithm (HS256, RS256, ES256).</summary>
    public abstract string Algorithm { get; }

    /// <summary>Signs the ASCII signing input.</summary>
    public abstract byte[] Sign(ReadOnlySpan<byte> input);

    /// <summary>Verifies a signature over the ASCII signing input.</summary>
    public abstract bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> signature);

    /// <summary>Public JWK (no private material).</summary>
    public abstract Dictionary<string, object> ToPublicJwk();
}

/// <summary>HMAC-SHA256 signing key.</summary>
public sealed class HmacSigningKey(string secretBytesBase64) : SigningKey
{
    private readonly byte[] secret = Convert.FromBase64String(secretBytesBase64);

    public override string Algorithm => "HS256";

    public override byte[] Sign(ReadOnlySpan<byte> input) => HMACSHA256.HashData(secret, input);

    public override bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> signature) =>
        CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(secret, input), signature.ToArray());

    public override Dictionary<string, object> ToPublicJwk() => new()
    {
        ["kty"] = "oct",
        ["use"] = "sig",
        ["alg"] = "HS256",
        ["kid"] = Id,
    };
}

/// <summary>ES256 (P-256) signing key over raw coordinates.</summary>
public sealed class EcdsaSigningKey : SigningKey
{
    private readonly ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public override string Algorithm => "ES256";

    /// <summary>Generates a fresh key.</summary>
    public static EcdsaSigningKey Generate(string id) => new() { Id = id };

    /// <summary>Restores a key from d || X || Y (32 bytes each, big-endian).</summary>
    public static EcdsaSigningKey FromSecret(ReadOnlySpan<byte> secret, string id)
    {
        if (secret.Length != 96)
            throw new ArgumentException("ES256 secret must be 96 bytes (d || X || Y)", nameof(secret));
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = secret[..32].ToArray(),
            Q = new ECPoint { X = secret[32..64].ToArray(), Y = secret[64..96].ToArray() },
        };
        var key = new EcdsaSigningKey { Id = id };
        key.ecdsa.ImportParameters(parameters);
        return key;
    }

    /// <summary>d || X || Y (96 bytes).</summary>
    public byte[] ExportSecret()
    {
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
        var secret = new byte[96];
        parameters.D.AsSpan().CopyTo(secret.AsSpan(0, 32));
        parameters.Q.X.AsSpan().CopyTo(secret.AsSpan(32, 32));
        parameters.Q.Y.AsSpan().CopyTo(secret.AsSpan(64, 32));
        return secret;
    }

    public override byte[] Sign(ReadOnlySpan<byte> input) =>
        ecdsa.SignData(input.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

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

/// <summary>RS256 signing key over modulus/exponent (JWK-compatible).</summary>
public sealed class RsaSigningKey : SigningKey
{
    private readonly RSA rsa = RSA.Create();
    public override string Algorithm => "RS256";

    /// <summary>Restores a key from n || e (big-endian, no leading zeros).</summary>
    public static RsaSigningKey FromModulusExponent(byte[] modulus, byte[] exponent, string id)
    {
        var key = new RsaSigningKey { Id = id };
        key.rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
        return key;
    }

    public override byte[] Sign(ReadOnlySpan<byte> input) =>
        rsa.SignData(input.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    public override bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> signature) =>
        rsa.VerifyData(input.ToArray(), signature.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    public override Dictionary<string, object> ToPublicJwk() => new()
    {
        ["kty"] = "RSA",
        ["use"] = "sig",
        ["alg"] = "RS256",
        ["kid"] = Id,
        ["n"] = Encodings.Base64UrlEncode(rsa.ExportParameters(false).Modulus!),
        ["e"] = Encodings.Base64UrlEncode(rsa.ExportParameters(false).Exponent!),
    };
}
