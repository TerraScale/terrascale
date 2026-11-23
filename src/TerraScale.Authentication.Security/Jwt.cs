using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TerraScale.Authentication.Security;

public static class Jwt
{
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);

    private static readonly string[] ReservedClaims =
    [
        JwtRegisteredClaimNames.Iss, JwtRegisteredClaimNames.Aud, JwtRegisteredClaimNames.Exp,
        JwtRegisteredClaimNames.Nbf, JwtRegisteredClaimNames.Iat,
    ];

    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    public static string? KeyId(string? token)
    {
        if (string.IsNullOrEmpty(token) || !Handler.CanReadToken(token))
        {
            return null;
        }

        try
        {
            return new JsonWebToken(token) is { IsEncrypted: false, Kid: { Length: > 0 } kid } ? kid : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

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
            IgnoreTrailingSlashWhenValidatingAudience = false,
            IssuerSigningKeys = [.. keys.Select(key => key.SecurityKey)],
            TryAllIssuerSigningKeys = false,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero,
        });
        return result.IsValid ? result.Claims : null;
    }
}
