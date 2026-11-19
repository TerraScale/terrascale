using System.Buffers.Text;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Features.Agents;

public sealed record AgentPublicKey(string Jwk, string Thumbprint);

public static class AgentJwk
{
    private static readonly FrozenSet<string> PrivateMembers = new[] { "d", "p", "q", "dp", "dq", "qi", "oth", "k" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> IgnoredMembers = new[] { "kid", "key_ops", "ext" }.ToFrozenSet(StringComparer.Ordinal);

    public static AgentPublicKey Parse(JsonElement? jwk)
    {
        if (jwk is not { ValueKind: JsonValueKind.Object } value)
        {
            throw Invalid();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? kty = null, crv = null, x = null, y = null;
        foreach (var member in value.EnumerateObject())
        {
            if (PrivateMembers.Contains(member.Name))
            {
                throw new ApiException(400, "private_key_material", "Send only the public key: a JWK with private members is refused and never stored.");
            }

            if (!seen.Add(member.Name))
            {
                throw Invalid();
            }

            switch (member.Name)
            {
                case "kty":
                    kty = Text(member.Value);
                    break;
                case "crv":
                    crv = Text(member.Value);
                    break;
                case "x":
                    x = Text(member.Value);
                    break;
                case "y":
                    y = Text(member.Value);
                    break;
                case "alg" when Text(member.Value) == "ES256":
                case "use" when Text(member.Value) == "sig":
                    break;
                default:
                    if (!IgnoredMembers.Contains(member.Name))
                    {
                        throw Invalid();
                    }

                    break;
            }
        }

        if (kty != "EC" || crv != "P-256" || Coordinate(x) is not { } pointX || Coordinate(y) is not { } pointY || !OnCurve(pointX, pointY))
        {
            throw Invalid();
        }

        var canonical = $$"""{"crv":"P-256","kty":"EC","x":"{{x}}","y":"{{y}}"}""";
        return new(canonical, Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static byte[]? Coordinate(string? text)
    {
        if (text is not { Length: 43 })
        {
            return null;
        }

        var bytes = new byte[32];
        return Base64Url.TryDecodeFromChars(text, bytes, out var written) && written == 32 && Base64Url.EncodeToString(bytes) == text ? bytes : null;
    }

    private static bool OnCurve(byte[] x, byte[] y)
    {
        try
        {
            using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static ApiException Invalid() =>
        new(400, "invalid_jwk", "public_jwk must be a public P-256 EC JWK (kty EC, crv P-256, unpadded base64url x and y of 32 bytes on the curve).");
}
