using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    // ts-auth publishes ES256/P-256 keys. Reject every other algorithm rather
    // than selecting a verifier from untrusted JWT headers.
    public static class AuthIdToken
    {
        public static AuthIdentity Validate(string? token, string issuer, string audience, string? nonce, TsAuthJwkSet keys, DateTimeOffset now)
        {
            try
            {
                var claims = AuthJwtClaims.Read(token, issuer, audience, keys, now);
                if (claims.TryGetProperty("azp", out var azp) && azp.GetString() != audience)
                {
                    throw new SignInRejectedException();
                }

                if (claims.GetProperty("aud").ValueKind == JsonValueKind.Array && claims.GetProperty("aud").GetArrayLength() > 1 && !claims.TryGetProperty("azp", out _))
                {
                    throw new SignInRejectedException();
                }

                if (nonce is not null && (!claims.TryGetProperty("nonce", out var actualNonce) || !Fixed(nonce, actualNonce.GetString())))
                {
                    throw new SignInRejectedException();
                }

                var subject = claims.GetProperty("sub").GetString();
                var email = claims.GetProperty("email").GetString()?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(subject) || subject.Length > 512 || subject.Any(char.IsControl) || email is not { Length: > 0 and <= 254 } ||
                    claims.GetProperty("email_verified").ValueKind != JsonValueKind.True || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email ||
                    !email[(email.IndexOf('@') + 1)..].Contains('.'))
                {
                    throw new SignInRejectedException();
                }

                var name = claims.TryGetProperty("name", out var claimName) ? claimName.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || name.EnumerateRunes().Count() > 128)
                {
                    name = email;
                }

                return new(subject, email, name);
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or CryptographicException or InvalidOperationException or ArgumentException)
            { throw new SignInRejectedException(); }
        }

        private static bool Fixed(string expected, string? actual)
        {
            return actual is not null &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
        }
    }

    public sealed record AuthIdentity(string Subject, string Email, string Name);
}
