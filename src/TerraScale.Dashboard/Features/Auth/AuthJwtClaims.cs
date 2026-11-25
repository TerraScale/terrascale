using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    internal sealed record UnverifiedJwt(string KeyId, string? Type, string? Issuer, string SigningInput, byte[] Signature, JsonElement Payload);

    internal static class AuthJwtClaims
    {
        internal const string OrganizationPickerClaim = "ts_org_picker";

        public static UnverifiedJwt? Parse(string? token, int maxLength = 65536)
        {
            if (token is not { Length: > 0 } || token.Length > maxLength || token.Split('.') is not [var head, var body, var seal] ||
                Bytes(head) is not { } headerBytes || Bytes(body) is not { } payloadBytes || Bytes(seal) is not { Length: 64 } signature ||
                JsonObject(headerBytes) is not { } header || JsonObject(payloadBytes) is not { } payload ||
                Text(header, "alg") != "ES256" || Text(header, "kid") is not { Length: > 0 } kid)
            {
                return null;
            }

            return new UnverifiedJwt(kid, Text(header, "typ"), Text(payload, "iss"), head + "." + body, signature, payload);
        }

        public static string? KeyId(string? token) => Parse(token)?.KeyId;

        public static TsAuthJwkSet? KeySet(JsonElement published)
        {
            try
            {
                if (published.ValueKind != JsonValueKind.Object || !Canonical(published) || !published.TryGetProperty("keys", out var listed) ||
                    listed.ValueKind != JsonValueKind.Array || listed.GetArrayLength() == 0)
                {
                    return null;
                }

                List<TsAuthJwk> keys = [];
                foreach (var entry in listed.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object || Required(entry, "kty") is not { } type || Required(entry, "crv") is not { } curve ||
                        Required(entry, "kid") is not { } kid || Required(entry, "x") is not { } x || Required(entry, "y") is not { } y ||
                        !Optional(entry, "alg", "ES256") || !Optional(entry, "use", "sig"))
                    {
                        return null;
                    }

                    var key = new TsAuthJwk(type, curve, entry.TryGetProperty("alg", out _) ? "ES256" : null, entry.TryGetProperty("use", out _) ? "sig" : null, kid, x, y);
                    using var imported = Import(key);
                    if (imported is null || keys.Any(known => known.KeyId == kid))
                    {
                        return null;
                    }

                    keys.Add(key);
                }

                return new TsAuthJwkSet(keys);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static string? Required(JsonElement entry, string name) =>
            entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static bool Optional(JsonElement entry, string name, string expected) =>
            !entry.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.String && value.ValueEquals(expected);

        public static ECDsa? Import(TsAuthJwk key)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (key is not { KeyType: "EC", Curve: "P-256", KeyId.Length: > 0 } || key.Algorithm is not (null or "ES256") || key.Use is not (null or "sig") ||
                Bytes(key.X) is not { Length: 32 } x || Bytes(key.Y) is not { Length: 32 } y)
            {
                return null;
            }

            var parameters = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } };
            try
            {
                parameters.Validate();
                return ECDsa.Create(parameters);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        public static JsonElement Read(string? token, string issuer, string? audience, TsAuthJwkSet keys, DateTimeOffset now,
            string? requiredType = null, int maxLength = 65536) =>
            TryRead(token, issuer, audience, keys, now, requiredType, maxLength) ?? throw new SignInRejectedException();

        public static JsonElement? TryRead(string? token, string issuer, string? audience, TsAuthJwkSet keys, DateTimeOffset now,
            string? requiredType = null, int maxLength = 65536)
        {
            ArgumentNullException.ThrowIfNull(keys);
            if (Parse(token, maxLength) is not { } jwt || requiredType is not null && jwt.Type != requiredType ||
                keys.Keys.Where(key => key.KeyId == jwt.KeyId).ToArray() is not [var key])
            {
                return null;
            }

            using var verifier = Import(key);
            if (verifier is null ||
                !verifier.VerifyData(Encoding.ASCII.GetBytes(jwt.SigningInput), jwt.Signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return null;
            }

            var claims = jwt.Payload;
            var seconds = now.ToUnixTimeSeconds();
            return jwt.Issuer == issuer && Audience(claims, audience) &&
                Number(claims, "exp") is { } exp && exp > seconds &&
                (!claims.TryGetProperty("nbf", out _) || Number(claims, "nbf") is { } nbf && nbf <= seconds) &&
                (!claims.TryGetProperty("iat", out _) || Number(claims, "iat") is { } iat && iat <= now.AddMinutes(1).ToUnixTimeSeconds())
                ? claims : null;
        }

        private static byte[]? Bytes(string? segment)
        {
            if (segment is not { Length: > 0 } || segment.Length % 4 == 1 || segment.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) ||
                !Base64Url.IsValid(segment))
            {
                return null;
            }

            return Base64Url.DecodeFromChars(segment);
        }

        private static JsonElement? JsonObject(byte[] bytes)
        {
            if (!Utf8.IsValid(bytes))
            {
                return null;
            }

            try
            {
                using var json = JsonDocument.Parse(bytes);
                return json.RootElement.ValueKind == JsonValueKind.Object && Canonical(json.RootElement) ? json.RootElement.Clone() : null;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static bool Canonical(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => UniqueMembers(element),
            JsonValueKind.Array => element.EnumerateArray().All(Canonical),
            JsonValueKind.String => element.GetString() is not null,
            _ => true,
        };

        private static bool UniqueMembers(JsonElement element)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name) || !Canonical(property.Value))
                {
                    return false;
                }
            }

            return true;
        }

        private static string? Text(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            try
            {
                return value.GetString();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static long? Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

        private static bool Audience(JsonElement claims, string? audience)
        {
            if (audience is null)
            {
                return !claims.TryGetProperty("aud", out _);
            }

            if (!claims.TryGetProperty("aud", out var claim))
            {
                return false;
            }

            return claim.ValueKind switch
            {
                JsonValueKind.String => claim.ValueEquals(audience),
                JsonValueKind.Array => claim.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.ValueEquals(audience)),
                _ => false
            };
        }
    }
}
