using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TerraScale.Auth.Security;

/// <summary>
/// ES256 compact JWTs through Microsoft.IdentityModel. Validation accepts only ES256, a key whose
/// <c>kid</c> matches the header, the expected issuer, audience and <c>typ</c>, and an unexpired
/// token; there is no clock skew.
/// </summary>
public static class Jwt
{
    /// <summary>
    /// The longest lifetime a token may have. The key ring keeps a superseded key published
    /// for longer than this after its successor starts signing.
    /// </summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);

    private static readonly string[] ReservedClaims =
    [
        JwtRegisteredClaimNames.Iss, JwtRegisteredClaimNames.Aud, JwtRegisteredClaimNames.Exp,
        JwtRegisteredClaimNames.Nbf, JwtRegisteredClaimNames.Iat,
    ];

    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>
    /// The unverified <c>kid</c> header of a compact JWT, or <see langword="null"/>: only for choosing which keys
    /// to verify with, never as evidence of anything.
    /// </summary>
    public static string? KeyId(string? token)
    {
        if (string.IsNullOrEmpty(token) || !Handler.CanReadToken(token))
        {
            return null;
        }

        try
        {
            return new JsonWebToken(token).Kid is { Length: > 0 } kid ? kid : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Signs <paramref name="claims"/> with <paramref name="key"/>. The issuer, audience and
    /// lifetime (positive, at most <see cref="MaxLifetime"/>) come only from the arguments, so
    /// claims cannot name their own.
    /// </summary>
    public static string Sign(
        Es256Key key,
        string type,
        string issuer,
        string audience,
        IReadOnlyDictionary<string, object> claims,
        DateTimeOffset issuedAt,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(claims);
        if (lifetime <= TimeSpan.Zero || lifetime > MaxLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "A token lifetime is positive and at most Jwt.MaxLifetime.");
        }

        if (ReservedClaims.FirstOrDefault(claims.ContainsKey) is { } reserved)
        {
            throw new ArgumentException($"The '{reserved}' claim is set from the signing arguments.", nameof(claims));
        }

        return Handler.CreateToken(new SecurityTokenDescriptor
        {
            TokenType = type,
            Issuer = issuer,
            Audience = audience,
            Claims = claims.ToDictionary(),
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = (issuedAt + lifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(key.SecurityKey, SecurityAlgorithms.EcdsaSha256),
        });
    }

    /// <summary>The token's claims, or <see langword="null"/> when any check fails.</summary>
    public static async Task<IDictionary<string, object>?> ValidateAsync(
        string token,
        IEnumerable<Es256Key> keys,
        string type,
        string issuer,
        string audience)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidTypes = [type],
            ValidIssuer = issuer,
            ValidAudience = audience,
            // Audiences are compared exactly: "aud/" is a different audience.
            IgnoreTrailingSlashWhenValidatingAudience = false,
            IssuerSigningKeys = [.. keys.Select(key => key.SecurityKey)],
            TryAllIssuerSigningKeys = false,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero,
        }).ConfigureAwait(false);
        return result.IsValid ? result.Claims : null;
    }
}
