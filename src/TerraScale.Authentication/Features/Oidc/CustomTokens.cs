using System.Net;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Oidc;

public sealed class CustomTokens(ApiKeys keys, TenantDirectory tenants, KeyRing ring, AuthOptions options, TimeProvider clock)
{
    public const string TokenUse = "custom";

    public static readonly TimeSpan Lifetime = OidcIssuer.TokenLifetime;

    internal Task CheckAsync(ApiKeyRecord key, string project, IPAddress? address, CancellationToken cancellationToken) =>
        tenants.CheckKeyAccessAsync(key, project, TerraBaseScopes.AuthTokensMint, address, cancellationToken);

    internal async Task<CustomTokenBody> IssueAsync(ApiKeyRecord key, string project, CustomTokenRequest request, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(request.UserId) || request.Database is not null && !TerraScaleIds.IsValid(request.Database))
        {
            throw Invalid();
        }

        var claims = TrustedClaims.Minted(request.Claims);
        await keys.RecordUseAsync(key, cancellationToken).ConfigureAwait(false);
        var signing = await ring.GetActiveAsync(project, cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            var descriptor = new SecurityTokenDescriptor
            {
                TokenType = "at+jwt",
                Issuer = OidcIssuer.Issuer(options, project).AbsoluteUri,
                IssuedAt = now.UtcDateTime,
                NotBefore = now.UtcDateTime,
                Expires = (now + Lifetime).UtcDateTime,
                SigningCredentials = new SigningCredentials(signing[0].SecurityKey, SecurityAlgorithms.EcdsaSha256),
                Claims = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [JwtRegisteredClaimNames.Sub] = request.UserId!,
                    ["token_use"] = TokenUse,
                },
            };
            foreach (var audience in options.ProjectDataAudiences(project))
            {
                descriptor.Audiences.Add(audience);
            }

            if (request.Database is { } database)
            {
                descriptor.Claims["database"] = database;
            }

            if (claims is { } trusted)
            {
                descriptor.Claims[TrustedClaims.Claim] = trusted;
            }

            return new CustomTokenBody(new JsonWebTokenHandler().CreateToken(descriptor), "Bearer", (long)Lifetime.TotalSeconds);
        }
        finally
        {
            foreach (var signer in signing)
            {
                signer.Dispose();
            }
        }
    }

    internal static ApiException Invalid() => new(StatusCodes.Status400BadRequest, "invalid_request",
        "Supply a UTF-8 JSON object of at most 16384 bytes with user_id (canonical ULID), optional database (canonical ULID) and optional claims. No other members are accepted.");
}
