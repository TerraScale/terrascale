using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    internal static class AuthJwtClaims
    {
        internal const string OrganizationPickerClaim = "ts_org_picker";

        public static string? KeyId(string? token) => UnverifiedText(token, 0, "kid");

        public static string? UnverifiedIssuer(string? token) => UnverifiedText(token, 1, "iss");

        private static string? UnverifiedText(string? token, int part, string name)
        {
            if (token is not { Length: <= 65536 } || token.Split('.') is not { Length: 3 } parts || !Base64Url.IsValid(parts[part]))
            {
                return null;
            }

            var reader = new Utf8JsonReader(Base64Url.DecodeFromChars(parts[part]));
            try
            {
                if (!JsonDocument.TryParseValue(ref reader, out var parsed))
                {
                    return null;
                }

                using var document = parsed;
                return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static JsonElement Read(string? token, string issuer, string? audience, TsAuthJwkSet keys, DateTimeOffset now,
            string? requiredType = null, int maxLength = 65536)
        {
            try
            {
                if (token is not { Length: > 0 } || token.Length > maxLength)
                {
                    throw new SignInRejectedException();
                }

                var parts = token.Split('.');
                if (parts.Length != 3)
                {
                    throw new SignInRejectedException();
                }

                using var header = JsonDocument.Parse(Decode(parts[0]));
                if (header.RootElement.GetProperty("alg").GetString() != "ES256" ||
                    requiredType is not null && (!header.RootElement.TryGetProperty("typ", out var type) || type.GetString() != requiredType))
                {
                    throw new SignInRejectedException();
                }

                var kid = header.RootElement.GetProperty("kid").GetString();
                var key = keys.Keys.SingleOrDefault(key => key.KeyId == kid && key.KeyType == "EC" && key.Curve == "P-256" &&
                    (key.Algorithm is null or "ES256") && (key.Use is null or "sig")) ?? throw new SignInRejectedException();
                var x = Decode(key.X); var y = Decode(key.Y);
                if (x.Length != 32 || y.Length != 32)
                {
                    throw new SignInRejectedException();
                }

                using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
                if (!verifier.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    throw new SignInRejectedException();
                }

                using var json = JsonDocument.Parse(Decode(parts[1]));
                var claims = json.RootElement;
                if (claims.GetProperty("iss").GetString() != issuer || !Audience(claims, audience) ||
                    claims.GetProperty("exp").GetInt64() <= now.ToUnixTimeSeconds() ||
                    (claims.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > now.ToUnixTimeSeconds()) ||
                    (claims.TryGetProperty("iat", out var iat) && iat.GetInt64() > now.AddMinutes(1).ToUnixTimeSeconds()))
                {
                    throw new SignInRejectedException();
                }

                return claims.Clone();
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or CryptographicException or InvalidOperationException or ArgumentException)
            { throw new SignInRejectedException(); }
        }

        private static bool Audience(JsonElement claims, string? audience)
        {
            if (audience is null)
            {
                return !claims.TryGetProperty("aud", out _);
            }

            var claim = claims.GetProperty("aud");
            return claim.ValueKind switch
            {
                JsonValueKind.String => claim.GetString() == audience,
                JsonValueKind.Array => claim.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == audience),
                _ => false
            };
        }

        private static byte[] Decode(string value)
        {
            if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            {
                throw new FormatException();
            }

            return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        }
    }
}
