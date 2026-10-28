using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Store;
using ZLogger;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>
/// The token endpoint grants of the platform contract (TS-598): an API key's client credentials, and a user's sign-in
/// access token exchanged (RFC 8693), for a token that acts in one organization or project. The tokens are ES256
/// <c>at+jwt</c> for the configured audiences, live at most 60 seconds (<see cref="Lifetime"/>) and carry <c>org_id</c>, <c>project_id</c>,
/// <c>database</c> and <c>scope</c>; no refresh or identity token comes with them, and no token entry is stored.
/// </summary>
public sealed class PlatformGrants(ApiKeys keys, PlatformDirectory directory, TotpCredentials mfa, AttemptLimiter limiter, AuthOptions options, TimeProvider clock,
    AuthStore store, GitHubActionsKeys gitHubActions, ILogger<PlatformGrants> logger)
{
    /// <summary>The organization a token acts in.</summary>
    public const string OrganizationClaim = "org_id";

    /// <summary>The project a token acts in.</summary>
    public const string ProjectClaim = "project_id";

    /// <summary>The database an API key's token is narrowed to.</summary>
    public const string DatabaseClaim = "database";

    /// <summary>The space-separated CIDRs an API key's token must be used from (absent: any address).</summary>
    public const string KeyCidrsClaim = "key_cidrs";

    /// <summary>The space-separated CIDRs every API key token of the project must be used from (absent: any address).</summary>
    public const string ProjectCidrsClaim = "project_cidrs";

    /// <summary>The API key an API key's token was issued for (equal to its <c>sub</c>).</summary>
    public const string KeyIdClaim = "key_id";

    /// <summary>Which of the key's strings the token was issued for (<see cref="ApiKeys.Generation"/>), so a rotation retires it.</summary>
    public const string KeyGenerationClaim = "key_gen";

    /// <summary>
    /// The lifetime of organization and project tokens. TerraBase validates them offline; an API key's token is also checked
    /// against the key's live status within seconds (docs/terrabase/key-revocation.md), others only expire.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan AuthenticationTimeClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>Token requests per API key per minute.</summary>
    public const int KeyRequestsPerMinute = 1000;

    internal const string NameNarrowedKeyNotice = "This API key is narrowed to a database by name. Create a new key narrowed by the database ID and revoke this one.";

    internal const string InvalidApiKeyNotice = "The API key is invalid, revoked, expired, or superseded by a concurrent change in another region. Use the latest credential.";

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
            return OidcResults.Forbid(Errors.InvalidClient, InvalidApiKeyNotice);
        }

        // A key stored narrowed by database name (before narrowing by ULID) would follow the name to another database;
        // it mints nothing, with a notice that says how to replace it, rather than a token TerraBase later refuses.
        if (key.Database is { } narrowed && !ApiKeys.IsDatabaseId(narrowed))
        {
            return OidcResults.Forbid(Errors.InvalidClient, NameNarrowedKeyNotice);
        }

        if (!await limiter.TryAcquireAsync(PlatformDirectory.Instance, "api-key-token", key.Id, KeyRequestsPerMinute, cancellationToken))
        {
            context.Response.Headers.RetryAfter = "60";
            return Results.Json(new OAuthErrorBody("rate_limited", "Too many token requests for this API key; retry later."), statusCode: StatusCodes.Status429TooManyRequests);
        }

        var project = Parameter(request, "project") ?? key.ProjectId;
        var found = project is null || key.ProjectId is not null && project != key.ProjectId ? null : await directory.ProjectAsync(project, cancellationToken);
        if (project is not null && (found is null || found.Value.OrganizationId != key.OrganizationId))
        {
            return OidcResults.Forbid(Errors.InvalidTarget, "The project is not one this key reaches.");
        }

        var requested = request.GetScopes();
        if (requested.Except(key.Scopes).Any())
        {
            return OidcResults.Forbid(Errors.InvalidScope, "A requested scope exceeds the key's.");
        }

        // Never past the key's own expiry; key_id and key_gen let TerraBase ask GET /v1/terrascale/api-keys/self whether the
        // key, and the key string the token came from, still act (docs/terrabase/key-revocation.md).
        var now = clock.GetUtcNow();
        var lifetime = key.ExpiresAt is { } end && end - now < Lifetime ? end - now : Lifetime;
        if (lifetime <= TimeSpan.Zero)
        {
            return OidcResults.Forbid(Errors.InvalidClient, InvalidApiKeyNotice);
        }

        var identity = Identity(key.Id, key.OrganizationId, project, requested.IsEmpty ? key.Scopes : requested, lifetime)
            .SetClaim(KeyIdClaim, key.Id)
            .SetClaim(KeyGenerationClaim, ApiKeys.Generation(secret))
            .SetExpirationDate(now + lifetime);
        if (key.Database is { } database)
        {
            identity.SetClaim(DatabaseClaim, database);
        }

        // TerraBase enforces both lists offline (docs/auth/api-key-ip-allowlists.md); a token outlives a change by 60 seconds at most.
        if (key.AllowedCidrs is { Count: > 0 } keyCidrs)
        {
            identity.SetClaim(KeyCidrsClaim, string.Join(' ', keyCidrs));
        }

        if (found?.Value.AllowedCidrs is { Count: > 0 } projectCidrs)
        {
            identity.SetClaim(ProjectCidrsClaim, string.Join(' ', projectCidrs));
        }

        await keys.RecordUseAsync(key, cancellationToken);
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
            || subject.GetExpirationDate() is not { } expires || expires <= clock.GetUtcNow()
            || request.SubjectToken is not { } encoded
            || !new JsonWebToken(encoded).TryGetPayloadValue<object>(Claims.AuthenticationTime, out var rawAuthenticationTime)
            || rawAuthenticationTime is not (int or long)
            || !long.TryParse(subject.GetClaim(Claims.AuthenticationTime), NumberStyles.None, CultureInfo.InvariantCulture, out var authenticatedAt)
            || authenticatedAt > clock.GetUtcNow().Add(AuthenticationTimeClockSkew).ToUnixTimeSeconds()
            || await mfa.PrimaryUserAsync(PlatformDirectory.Instance, userId, subject.GetClaim(OidcIssuer.EpochClaim), subject.GetClaim(OidcIssuer.StateClaim),
                subject.GetClaim(OidcIssuer.MfaClaim) is not null, cancellationToken) is not { } user)
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
        catch (ApiException error) when (error.Code == PlatformDirectory.MfaRequiredCode)
        {
            return OidcResults.Forbid(Errors.AccessDenied, "mfa_required: the organization requires two-factor authentication; enrol at /ui/terrascale/security.");
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

        if (!user.MultiFactor && !await mfa.AllowsPrimaryAuthenticationAsync(user, cancellationToken) || expires <= clock.GetUtcNow())
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

    /// <summary>
    /// The GitHub Actions grant (<see cref="GitHubActionsGrant"/>): the bound workload's GitHub OIDC token, in
    /// <c>subject_token</c>, for a project token of the bound e2e project. The token must be signed by GitHub's current
    /// keys, name the platform issuer as its audience, be live, match every claim of the trust policy and carry a
    /// <c>jti</c> never presented before. <paramref name="binding"/> is the client OpenIddict authenticated;
    /// without its exact live binding and permissions nothing is issued.
    /// </summary>
    public async Task<IResult> GitHubActionsAsync(OpenIddictRequest request, OidcApplication? binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ClientId != GitHubActionsGrant.ClientId || binding is null || binding.ClientType != ClientTypes.Public
            || !GitHubActionsGrant.HasExactPermissions(binding.Permissions)
            || !GitHubActionsGrant.TryReadBinding(binding.Properties, clock.GetUtcNow(), out var organization, out var project, out var database, out var deadline)
            || await directory.OrganizationAsync(organization, cancellationToken).ConfigureAwait(false) is null
            || await directory.ProjectAsync(project, cancellationToken).ConfigureAwait(false) is not { } found
            || found.Value.OrganizationId != organization)
        {
            return OidcResults.Forbid(Errors.InvalidClient, "The GitHub Actions workload is not bound to a live approved target.");
        }

        foreach (var (name, expected) in new[] { ("organization", organization), ("project", project), ("database", database) })
        {
            if (request.GetParameter(name) is { } target && (string?)target != expected)
            {
                return OidcResults.Forbid(Errors.InvalidTarget, "The requested target differs from the binding.");
            }
        }

        if (request.SubjectToken is not { Length: > 0 } encoded)
        {
            return OidcResults.Forbid(Errors.InvalidRequest, "The subject_token parameter is required.");
        }

        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(encoded, new TokenValidationParameters
        {
            ConfigurationManager = gitHubActions.Configuration,
            ValidIssuer = GitHubActionsGrant.Issuer,
            ValidAudience = GitHubActionsGrant.Audience,
            // Exact audience, as every token ts-auth validates: no trailing-slash folding.
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = AuthenticationTimeClockSkew,
        }).ConfigureAwait(false);
        var token = validated.IsValid ? validated.SecurityToken as JsonWebToken : null;
        using var payload = token is null ? null : JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload));
        var claims = payload?.RootElement ?? default;
        // Only the name of the first claim that failed is logged, never the token.
        var refused = token is null ? "signature, issuer, audience or lifetime"
            : new (string Claim, string Expected)[]
            {
                ("iss", GitHubActionsGrant.Issuer),
                ("aud", GitHubActionsGrant.Audience),
                ("sub", GitHubActionsGrant.Subject),
                ("environment", GitHubActionsGrant.Environment),
                ("repository_id", GitHubActionsGrant.RepositoryId),
                ("repository_owner_id", GitHubActionsGrant.RepositoryOwnerId),
                ("repository", GitHubActionsGrant.Repository),
                ("ref", GitHubActionsGrant.Ref),
                ("event_name", GitHubActionsGrant.EventName),
                ("workflow_ref", GitHubActionsGrant.WorkflowRef),
            }.FirstOrDefault(pair => Claim(claims, pair.Claim) != pair.Expected).Claim
            ?? (!claims.TryGetProperty("job_workflow_ref", out _) ? null : "job_workflow_ref")
            ?? (Claim(claims, "jti") is null ? "jti" : Claim(claims, "run_id") is null ? "run_id" : null);
        if (token is null || refused is not null)
        {
            logger.ZLogWarning($"GitHub Actions token refused: {refused}");
            return OidcResults.Forbid(Errors.InvalidGrant, "The GitHub Actions token does not match the bound workload.");
        }

        var requested = request.GetScopes();
        if (requested.Except(GitHubActionsGrant.Scopes).Any())
        {
            return OidcResults.Forbid(Errors.InvalidScope, "A requested scope exceeds the workload's.");
        }

        var runId = Claim(claims, "run_id")!;
        var tokenId = Claim(claims, "jti")!;
        // Single use on every replica: the claim is created only if absent.
        if (!await store.ClaimUniqueAsync(PlatformDirectory.Instance, GitHubActionsGrant.TokenIdKind, tokenId, runId, cancellationToken).ConfigureAwait(false))
        {
            logger.ZLogWarning($"GitHub Actions token refused: jti already used");
            return OidcResults.Forbid(Errors.InvalidGrant, "The GitHub Actions token was already used.");
        }

        var now = clock.GetUtcNow();
        if (deadline <= now)
        {
            return OidcResults.Forbid(Errors.InvalidClient, "The workload binding has expired.");
        }

        var scopes = requested.IsEmpty ? GitHubActionsGrant.Scopes : [.. requested];
        logger.ZLogWarning($"Audit: GitHub Actions project token for organization {organization}, project {project}, database {database}, deadline {deadline}: run {runId} attempt {Claim(claims, "run_attempt")}, sha {Claim(claims, "sha")}, scopes {string.Join(' ', scopes)}");
        var expires = now + Lifetime < deadline ? now + Lifetime : deadline;
        var identity = Identity(GitHubActionsGrant.ClientId, organization, project, scopes, expires - now)
            .SetClaim(DatabaseClaim, database)
            .SetResources(GitHubActionsGrant.ApiAudience)
            .SetExpirationDate(expires);
        return SignIn(identity);
    }

    // Numeric ids and other policy claims must remain scalar strings; typed JWT lookups can coerce other JSON forms.
    private static string? Claim(JsonElement claims, string name) =>
        claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text ? text : null;

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
/// Stores no token entry for the 5-minute organization and project tokens and for service clients' tokens: they are
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

/// <summary>Preserves API-key recovery guidance when OpenIddict rejects a filtered secret before grant handling.</summary>
public sealed class ApiKeyFailureNotice(ApiKeys keys) : OpenIddict.Server.IOpenIddictServerHandler<OpenIddict.Server.OpenIddictServerEvents.ApplyTokenResponseContext>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(OpenIddict.Server.OpenIddictServerEvents.ApplyTokenResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Response.Error == Errors.InvalidClient && context.Request?.IsClientCredentialsGrantType() == true
            && context.Request.ClientId is { } clientId
            && await keys.FindAsync(clientId, context.CancellationToken).ConfigureAwait(false) is not null)
        {
            context.Response.ErrorDescription = PlatformGrants.InvalidApiKeyNotice;
        }
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
    /// revocation change its digests, which must hold at once. Canonical ULIDs do not identify the client kind.
    /// </summary>
    public override async ValueTask<OidcApplication?> FindByClientIdAsync(string identifier, CancellationToken cancellationToken = default)
    {
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
