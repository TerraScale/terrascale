using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Mfa;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.RateLimiting;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Auth.Features.Platform;

/// <summary>
/// The token endpoint grants of the platform contract (TS-598): an API key's client credentials, and a user's sign-in
/// access token exchanged (RFC 8693), for a token that acts in one organization or project. The tokens are ES256
/// <c>at+jwt</c> for the configured audiences, live at most 15 minutes and carry <c>org_id</c>, <c>project_id</c>,
/// <c>database</c> and <c>scope</c>; no refresh or identity token comes with them, and no token entry is stored.
/// </summary>
public sealed class PlatformGrants(ApiKeys keys, PlatformDirectory directory, Users users, TotpCredentials mfa, AttemptLimiter limiter, AuthOptions options, TimeProvider clock)
{
    /// <summary>The organization a token acts in.</summary>
    public const string OrganizationClaim = "org_id";

    /// <summary>The project a token acts in.</summary>
    public const string ProjectClaim = "project_id";

    /// <summary>The database an API key's token is narrowed to.</summary>
    public const string DatabaseClaim = "database";

    /// <summary>The lifetime of organization and project tokens.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan AuthenticationTimeClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>Token requests per API key per minute.</summary>
    public const int KeyRequestsPerMinute = 1000;

    /// <summary>The <c>client_credentials</c> grant of an API key OpenIddict has authenticated.</summary>
    public async Task<IResult> ApiKeyAsync(HttpContext context, OpenIddictRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        // Read the key again and check the presented string against it: whatever OpenIddict saw, a key revoked or rotated
        // since then, on any replica, issues nothing.
        if (await keys.FindAsync(request.ClientId!, cancellationToken) is not { } key
            || request.ClientSecret is not { } secret || !Matches(secret, keys.Digests(key))
            || await directory.OrganizationAsync(key.OrganizationId, cancellationToken) is null)
        {
            return OidcResults.Forbid(Errors.InvalidClient, "The API key is unknown, revoked or expired.");
        }

        if (!await limiter.TryAcquireAsync(PlatformDirectory.Instance, "api-key-token", key.Id, KeyRequestsPerMinute, cancellationToken))
        {
            context.Response.Headers.RetryAfter = "60";
            return Results.Json(new OAuthErrorBody("rate_limited", "Too many token requests for this API key; retry later."), statusCode: StatusCodes.Status429TooManyRequests);
        }

        var project = Parameter(request, "project") ?? key.ProjectId;
        if (project is not null && (key.ProjectId is not null && project != key.ProjectId
            || await directory.ProjectAsync(project, cancellationToken) is not { } found || found.Value.OrganizationId != key.OrganizationId))
        {
            return OidcResults.Forbid(Errors.InvalidTarget, "The project is not one this key reaches.");
        }

        var requested = request.GetScopes();
        if (requested.Except(key.Scopes).Any())
        {
            return OidcResults.Forbid(Errors.InvalidScope, "A requested scope exceeds the key's.");
        }

        var identity = Identity(key.Id, key.OrganizationId, project, requested.IsEmpty ? key.Scopes : requested, Lifetime);
        if (key.Database is { } database)
        {
            identity.SetClaim(DatabaseClaim, database);
        }

        return SignIn(identity);
    }

    /// <summary>The token-exchange grant: a sign-in access token of the same client for an organization or project token.</summary>
    public async Task<IResult> ExchangeAsync(HttpContext context, OpenIddictRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var subject = (await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        var issuer = OidcIssuer.Issuer(options).AbsoluteUri;
        // Only a sign-in token (its audiences include the issuer) issued to this very client, for a user that still exists
        // and has not signed out everywhere or reset its password since (the token's epoch is still the user's).
        // Claims flatten arrays, so inspect the scalar payload of the subject JWT OpenIddict already validated.
        if (subject is null || !subject.GetAudiences().Contains(issuer, StringComparer.Ordinal)
            || request.ClientId is not { } clientId || !subject.GetPresenters().Contains(clientId, StringComparer.Ordinal)
            || subject.GetClaim(Claims.Subject) is not { } userId
            || await users.GetAsync(PlatformDirectory.Instance, userId, cancellationToken) is not { } user
            || subject.GetClaim(OidcIssuer.EpochClaim) != user.AuthEpoch.ToString(CultureInfo.InvariantCulture)
            || subject.GetExpirationDate() is not { } expires || expires <= clock.GetUtcNow()
            || request.SubjectToken is not { } encoded
            || !new JsonWebToken(encoded).TryGetPayloadValue<object>(Claims.AuthenticationTime, out var rawAuthenticationTime)
            || rawAuthenticationTime is not (int or long)
            || !long.TryParse(subject.GetClaim(Claims.AuthenticationTime), NumberStyles.None, CultureInfo.InvariantCulture, out var authenticatedAt)
            || authenticatedAt > clock.GetUtcNow().Add(AuthenticationTimeClockSkew).ToUnixTimeSeconds()
            || !await mfa.AllowsPrimaryAuthenticationAsync(user, cancellationToken))
        {
            return OidcResults.Forbid(Errors.InvalidGrant, "The subject token is not a live sign-in token of this client.");
        }

        if (Parameter(request, "organization") is not { } organization)
        {
            return OidcResults.Forbid(Errors.InvalidRequest, "The organization parameter is required.");
        }

        var project = Parameter(request, "project");
        IReadOnlyList<string> allowed;
        try
        {
            allowed = project is null
                ? TerraBaseScopes.For(Roles.Implied((await directory.OrganizationAccessAsync(user.Id, organization, cancellationToken)).Role))
                : TerraBaseScopes.For((await directory.ProjectAccessAsync(user.Id, organization, project, cancellationToken)).Role);
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status404NotFound)
        {
            allowed = [];
        }

        if (allowed.Count == 0)
        {
            // Unknown, unreachable and role-less (a collaborator's organization token) answer alike.
            return OidcResults.Forbid(Errors.InvalidTarget, "The organization or project is not one this user reaches.");
        }

        var requested = request.GetScopes();
        if (requested.Except(allowed).Any())
        {
            return OidcResults.Forbid(Errors.InvalidScope, "A requested scope exceeds the user's role.");
        }

        if (!await mfa.AllowsPrimaryAuthenticationAsync(user, cancellationToken) || expires <= clock.GetUtcNow())
        {
            return OidcResults.Forbid(Errors.InvalidGrant, "The subject token is not a live sign-in token of this client.");
        }

        var lifetime = expires - clock.GetUtcNow();
        var identity = Identity(user.Id, organization, project, requested.IsEmpty ? allowed : requested, lifetime < Lifetime ? lifetime : Lifetime)
            .SetClaim(Claims.Email, user.Email)
            .SetClaim(Claims.EmailVerified, true)
            .SetClaim(Claims.AuthenticationTime, authenticatedAt);
        return SignIn(identity);
    }

    private static bool Matches(string secret, IReadOnlyList<string> digests)
    {
        var digest = Security.Tokens.Sha256Hex(secret);
        var matched = false;
        foreach (var candidate in digests)
        {
            matched |= Security.Tokens.FixedTimeEquals(digest, candidate);
        }

        return matched;
    }

    private static string? Parameter(OpenIddictRequest request, string name) =>
        request.GetParameter(name) is { } value && (string?)value is { Length: > 0 } text ? text : null;

    private ClaimsIdentity Identity(string subject, string organization, string? project, IEnumerable<string> scopes, TimeSpan lifetime)
    {
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role)
            .SetClaim(Claims.Subject, subject)
            .SetClaim(OrganizationClaim, organization)
            .SetScopes(scopes)
            .SetResources(options.AccessTokenAudiences);
        if (project is not null)
        {
            identity.SetClaim(ProjectClaim, project);
        }

        identity.SetAccessTokenLifetime(lifetime);
        return identity;
    }

    private static IResult SignIn(ClaimsIdentity identity)
    {
        identity.SetDestinations(_ => [Destinations.AccessToken]);
        return Results.SignIn(new ClaimsPrincipal(identity), null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}

/// <summary>
/// Stores no token entry for the 15-minute organization and project tokens and for service clients' tokens: they are
/// validated offline and never revoked one by one (revoking the key or the role is the control), so an entry would only
/// grow storage. Every other token keeps its entry.
/// </summary>
public sealed class SkipShortLivedTokenEntries : OpenIddict.Server.IOpenIddictServerHandler<OpenIddict.Server.OpenIddictServerEvents.GenerateTokenContext>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddict.Server.OpenIddictServerEvents.GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TokenType == TokenTypeIdentifiers.AccessToken
            && (context.Principal.HasClaim(claim => claim.Type == PlatformGrants.OrganizationClaim) || context.Request?.IsClientCredentialsGrantType() == true))
        {
            context.CreateTokenEntry = false;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// API keys authenticate as OpenIddict clients whose stored secret is <c>tsk-sha256:</c> and the hex SHA-256 digests the
/// key string may match. They are compared here in constant time; every other client keeps OpenIddict's PBKDF2.
/// </summary>
public sealed class ApiKeyApplicationManager(
    OpenIddict.Abstractions.IOpenIddictApplicationCache<OidcApplication> cache,
    ILogger<OpenIddict.Core.OpenIddictApplicationManager<OidcApplication>> logger,
    Microsoft.Extensions.Options.IOptionsMonitor<OpenIddict.Core.OpenIddictCoreOptions> options,
    OpenIddict.Abstractions.IOpenIddictApplicationStore<OidcApplication> store)
    : OpenIddict.Core.OpenIddictApplicationManager<OidcApplication>(cache, logger, options, store)
{
    /// <summary>The prefix of an API key client's stored secret.</summary>
    public const string DigestPrefix = "tsk-sha256:";

    /// <summary>
    /// An API key's client comes from the store on every lookup, never from OpenIddict's entity cache: rotation and
    /// revocation change its digests, which must hold at once. Other clients keep the base lookup.
    /// </summary>
    public override async ValueTask<OidcApplication?> FindByClientIdAsync(string identifier, CancellationToken cancellationToken = default)
    {
        if (!ApiKeys.IsKeyId(identifier))
        {
            return await base.FindByClientIdAsync(identifier, cancellationToken).ConfigureAwait(false);
        }

        return await this.Store.FindByClientIdAsync(identifier, cancellationToken).ConfigureAwait(false) is { } application
            && string.Equals(await this.Store.GetClientIdAsync(application, cancellationToken).ConfigureAwait(false), identifier, StringComparison.Ordinal)
                ? application
                : null;
    }

    /// <inheritdoc />
    protected override ValueTask<bool> ValidateClientSecretAsync(string secret, string comparand, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparand);
        if (!comparand.StartsWith(DigestPrefix, StringComparison.Ordinal))
        {
            return base.ValidateClientSecretAsync(secret, comparand, cancellationToken);
        }

        var digest = Security.Tokens.Sha256Hex(secret);
        var matched = false;
        foreach (var candidate in comparand[DigestPrefix.Length..].Split(','))
        {
            matched |= Security.Tokens.FixedTimeEquals(digest, candidate);
        }

        return ValueTask.FromResult(matched);
    }
}

/// <summary>The OpenIddict client an API key authenticates as.</summary>
internal static class ApiKeyClients
{
    /// <summary>
    /// A confidential client allowed only the token endpoint and client credentials. It holds every TerraBase scope
    /// permission, so a scope beyond the key's reaches <see cref="PlatformGrants.ApiKeyAsync"/> and answers invalid_scope.
    /// </summary>
    public static OidcApplication Describe(ApiKeyRecord key, IReadOnlyList<string> digests) => new()
    {
        Id = key.Id,
        ClientId = key.Id,
        InstanceId = PlatformDirectory.Instance,
        ClientType = ClientTypes.Confidential,
        ApplicationType = ApplicationTypes.Web,
        ConsentType = ConsentTypes.Implicit,
        DisplayName = key.Name,
        ClientSecret = ApiKeyApplicationManager.DigestPrefix + string.Join(',', digests),
        Permissions = [Permissions.Endpoints.Token, Permissions.GrantTypes.ClientCredentials, .. TerraBaseScopes.All.Select(scope => Permissions.Prefixes.Scope + scope)],
        Version = string.Create(CultureInfo.InvariantCulture, $"api-key:{key.Id}"),
    };
}
