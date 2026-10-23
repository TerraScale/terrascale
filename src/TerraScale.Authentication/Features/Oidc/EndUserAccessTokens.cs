using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>
/// Issues rule-bound tokens from a live end-user session. HA1 owns the hosted sign-in and mint route;
/// this helper grants no project membership, platform scopes or credential provisioning.
/// </summary>
public sealed class EndUserAccessTokens(Sessions sessions, PlatformDirectory projects, KeyRing ring, AuthOptions options, TimeProvider clock)
{
    /// <summary>The most a scoped end-user token remains valid offline after session or role revocation.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A token for exactly the stored user's tenant/project, or null for an absent project or non-live tenant session.
    /// The caller must enforce same-origin CSRF protection and rate limits before handing over the session cookie.
    /// </summary>
    public async Task<string?> IssueAsync(string project, string? session, CancellationToken cancellationToken = default)
    {
        if (!TerraScaleIds.IsValid(project) || await projects.ProjectAsync(project, cancellationToken).ConfigureAwait(false) is null
            || await sessions.ResolveAsync(project, session, cancellationToken).ConfigureAwait(false) is not { } live
            || live.User.InstanceId != project || live.Session.InstanceId != project
            || options.AccessTokenAudiences.Count == 0)
        {
            return null;
        }

        var roles = live.User.Roles ?? [];
        if (roles.Count > 64 || roles.Any(role => string.IsNullOrWhiteSpace(role) || role.Length > 128 || role.Any(char.IsControl)))
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var expires = live.Session.ExpiresAt < now + Lifetime ? live.Session.ExpiresAt : now + Lifetime;
        if (expires <= now)
        {
            return null;
        }

        // The platform ring vouches for the tenant linkage; tenant keys never sign platform project grants.
        var keys = await ring.GetActiveAsync(OidcIssuer.InstanceId, cancellationToken).ConfigureAwait(false);
        try
        {
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
                    ["sub"] = live.User.Id,
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

            return new JsonWebTokenHandler().CreateToken(descriptor);
        }
        finally
        {
            foreach (var key in keys)
            {
                key.Dispose();
            }
        }
    }
}
