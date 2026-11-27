using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    public static class AuthIdToken
    {
        private static readonly string[] NameParts = ["given_name", "family_name"];

        public static AuthIdentity Validate(string? token, string issuer, string audience, string? nonce, TsAuthJwkSet keys, DateTimeOffset now) =>
            TryValidate(token, issuer, audience, nonce, keys, now) ?? throw new SignInRejectedException();

        public static AuthIdentity? TryValidate(string? token, string issuer, string audience, string? nonce, TsAuthJwkSet keys, DateTimeOffset now)
        {
            if (AuthJwtClaims.TryRead(token, issuer, audience, keys, now) is not { } claims)
            {
                return null;
            }

            if (claims.TryGetProperty("azp", out var azp) && (azp.ValueKind != JsonValueKind.String || azp.GetString() != audience))
            {
                return null;
            }

            if (claims.TryGetProperty("aud", out var aud) && aud.ValueKind == JsonValueKind.Array && aud.GetArrayLength() > 1 && !claims.TryGetProperty("azp", out _))
            {
                return null;
            }

            if (nonce is not null && (!claims.TryGetProperty("nonce", out var actualNonce) || actualNonce.ValueKind != JsonValueKind.String || !Fixed(nonce, actualNonce.GetString())))
            {
                return null;
            }

            var subject = claims.TryGetProperty("sub", out var subClaim) && subClaim.ValueKind == JsonValueKind.String ? subClaim.GetString() : null;
            var email = claims.TryGetProperty("email", out var emailClaim) && emailClaim.ValueKind == JsonValueKind.String ? emailClaim.GetString()?.Trim().ToLowerInvariant() : null;
            if (string.IsNullOrWhiteSpace(subject) || subject.Length > 512 || subject.Any(char.IsControl) || email is not { Length: > 0 and <= 254 } ||
                !claims.TryGetProperty("email_verified", out var verified) || verified.ValueKind != JsonValueKind.True ||
                !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email ||
                !email[(email.IndexOf('@') + 1)..].Contains('.'))
            {
                return null;
            }

            var name = claims.TryGetProperty("name", out var claimName) && claimName.ValueKind == JsonValueKind.String ? claimName.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = string.Join(' ', NameParts
                    .Select(claim => claims.TryGetProperty(claim, out var part) && part.ValueKind == JsonValueKind.String ? part.GetString()?.Trim() : null)
                    .OfType<string>().Where(part => part.Length > 0));
            }

            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || name.EnumerateRunes().Count() > 128)
            {
                name = email;
            }

            return new(subject, email, name, claims.TryGetProperty(AuthJwtClaims.OrganizationPickerClaim, out var picker)
                && picker.ValueKind == JsonValueKind.String && picker.GetString() == "true");
        }

        private static bool Fixed(string expected, string? actual)
        {
            return actual is not null &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
        }
    }

    public sealed record AuthIdentity(string Subject, string Email, string Name, bool OrganizationPicker = false);
}
