using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TerraScale.Authentication.Features.Agents;

public static class AgentProof
{
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(300);

    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private static readonly string[] Algorithms = [SecurityAlgorithms.EcdsaSha256];

    public static async Task<long?> IssuedAtAsync(string? proof, string publicJwk, string audience, DateTimeOffset now)
    {
        if (proof is not { Length: > 0 and <= 4096 })
        {
            return null;
        }

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(proof, new TokenValidationParameters
        {
            IssuerSigningKey = new JsonWebKey(publicJwk),
            ValidAlgorithms = Algorithms,
            ValidAudience = audience,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidateIssuer = false,
            ValidateLifetime = false,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
        });
        var unixNow = now.ToUnixTimeSeconds();
        return result.IsValid && result.SecurityToken is JsonWebToken { Alg: SecurityAlgorithms.EcdsaSha256 } token
            && token.Id is { Length: > 0 and <= 128 }
            && token.TryGetPayloadValue<long>(JwtRegisteredClaimNames.Exp, out var expires)
            && token.TryGetPayloadValue<long>(JwtRegisteredClaimNames.Iat, out var issued)
            && expires > unixNow && expires <= unixNow + (long)MaxLifetime.TotalSeconds
            && issued <= unixNow + (long)ClockSkew.TotalSeconds && issued >= unixNow - (long)MaxLifetime.TotalSeconds
                ? issued : null;
    }
}
