using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>
/// Issues rule-bound tokens for identities asserted by an authenticated project backend, without provisioning users.
/// </summary>
public sealed class EndUserAccessTokens(ApiKeys apiKeys, PlatformDirectory projects, KeyRing ring, AuthOptions options, TimeProvider clock)
{
    /// <summary>The most a scoped end-user token remains valid offline after key or identity revocation.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>Maximum compact JWT size in ASCII bytes, including signature.</summary>
    public const int MaxTokenBytes = 8192;

    /// <summary>
    /// Authenticates a live project key with no database restriction and both document scopes.
    /// An organization key, platform JWT or narrowed key cannot assert project-wide end-user authority.
    /// </summary>
    internal async Task<ApiKeyRecord?> AuthorizeAsync(string project, string? secret, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(project) || await apiKeys.AuthenticateAsync(secret, cancellationToken).ConfigureAwait(false) is not { } key
            || key.ProjectId != project || key.Database is not null
            || !key.Scopes.Contains(TerraBaseScopes.DocumentsRead, StringComparer.Ordinal)
            || !key.Scopes.Contains(TerraBaseScopes.DocumentsWrite, StringComparer.Ordinal)
            || await projects.ProjectAsync(project, cancellationToken).ConfigureAwait(false) is not { } linked
            || linked.Value.OrganizationId != key.OrganizationId
            || await projects.OrganizationAsync(key.OrganizationId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        return key;
    }

    /// <summary>A project-scoped token, capped at the freshly authenticated key's expiry; no key authorization is cached.</summary>
    internal async Task<EndUserTokenBody?> IssueAsync(string project, string secret, string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(userId) || !ValidRoles(roles) || options.AccessTokenAudiences.Count == 0)
        {
            return null;
        }

        // The platform ring vouches for the tenant linkage; tenant keys never sign platform project grants.
        var keys = await ring.GetActiveAsync(OidcIssuer.InstanceId, cancellationToken).ConfigureAwait(false);
        try
        {
            // Signing-key loading or body reading may outlive revocation; reread authority immediately before signing.
            if (await this.AuthorizeAsync(project, secret, cancellationToken).ConfigureAwait(false) is not { } key)
            {
                return null;
            }

            var now = clock.GetUtcNow();
            var expires = key.ExpiresAt is { } keyExpiry && keyExpiry < now + Lifetime ? keyExpiry : now + Lifetime;
            var seconds = (long)(expires - now).TotalSeconds;
            if (seconds <= 0)
            {
                return null;
            }

            var descriptor = new SecurityTokenDescriptor
            {
                TokenType = "at+jwt",
                Issuer = OidcIssuer.Issuer(options).AbsoluteUri,
                IssuedAt = now.UtcDateTime,
                NotBefore = now.UtcDateTime,
                Expires = expires.UtcDateTime,
                SigningCredentials = new SigningCredentials(keys[0].SecurityKey, SecurityAlgorithms.EcdsaSha256),
                Claims = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["sub"] = userId,
                    ["token_kind"] = "end_user",
                    ["instance_id"] = project,
                    ["project_id"] = project,
                    ["roles"] = roles.Distinct(StringComparer.Ordinal).ToArray(),
                },
            };
            foreach (var audience in options.AccessTokenAudiences)
            {
                descriptor.Audiences.Add(audience);
            }

            var encoded = new JsonWebTokenHandler().CreateToken(descriptor);
            // Compact JWTs are ASCII; bound the final encoding because Unicode claims may expand during serialization.
            if (encoded.Length > MaxTokenBytes)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_request", "The signed access token would exceed 8192 ASCII bytes; reduce the roles.");
            }

            return new EndUserTokenBody(encoded, "Bearer", seconds);
        }
        finally
        {
            foreach (var key in keys)
            {
                key.Dispose();
            }
        }
    }

    internal static bool ValidRoles(IReadOnlyList<string> roles) => roles.Count <= 64
        && roles.All(role => !string.IsNullOrWhiteSpace(role) && role.Length <= 128 && !role.Any(char.IsControl));
}
