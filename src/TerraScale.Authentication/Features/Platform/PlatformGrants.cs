using Microsoft.AspNetCore.Http.HttpResults;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OidcErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.AgentId;
using TerraScale.Authentication.Features.Agents;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Storage;
using ZLogger;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Platform;

public sealed class PlatformGrants(ApiKeys keys, PlatformDirectory directory, TotpCredentials mfa, AttemptLimiter limiter, AuthOptions options, TimeProvider clock,
    AuthData data, GitHubActionsKeys gitHubActions, LiveAgents agents, ILogger<PlatformGrants> logger)
{
    public const string OrganizationClaim = "org_id";

    public const string ProjectClaim = "project_id";

    public const string DatabaseClaim = "database";

    public const string KeyCidrsClaim = "key_cidrs";

    public const string ProjectCidrsClaim = "project_cidrs";

    public const string KeyIdClaim = "key_id";

    public const string KeyGenerationClaim = "key_gen";

    public const string KeyExpiryClaim = "key_exp";

    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan AuthenticationTimeClockSkew = TimeSpan.FromSeconds(30);

    public const int KeyRequestsPerMinute = 1000;

    internal const string NameNarrowedKeyNotice = "This API key is narrowed to a database by name. Create a new key narrowed by the database ID and revoke this one.";

    internal const string InvalidApiKeyNotice = "The API key is invalid, revoked, expired, or superseded by a concurrent change in another region. Use the latest credential.";

    public async Task<IResult> ApiKeyAsync(HttpContext context, OpenIddictRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (await keys.FindAsync(request.ClientId!, cancellationToken) is not { } key
            || request.ClientSecret is not { } secret || !Matches(secret, keys.Digests(key))
            || await directory.OrganizationAsync(key.OrganizationId, cancellationToken) is null)
        {
            return OidcResults.Forbid(OidcErrors.InvalidClient, InvalidApiKeyNotice);
        }

        if (key.Database is { } narrowed && !ApiKeys.IsDatabaseId(narrowed))
        {
            return OidcResults.Forbid(OidcErrors.InvalidClient, NameNarrowedKeyNotice);
        }

        if (!await limiter.TryAcquireAsync(PlatformDirectory.Instance, "api-key-token", key.Id, KeyRequestsPerMinute, cancellationToken))
        {
            context.Response.Headers.RetryAfter = "60";
            return TypedResults.Json(new OAuthErrorBody("rate_limited", "Too many token requests for this API key; retry later."), statusCode: StatusCodes.Status429TooManyRequests);
        }

        var project = Parameter(request, "project") ?? key.ProjectId;
        var found = project is null || key.ProjectId is not null && project != key.ProjectId ? null : await directory.ProjectAsync(project, cancellationToken);
        if (project is not null && (found is null || found.Value.OrganizationId != key.OrganizationId))
        {
            return OidcResults.Forbid(OidcErrors.InvalidTarget, "The project is not one this key reaches.");
        }

        if (await directory.ClosureAsync("organization", key.OrganizationId, cancellationToken) is not null
            || project is not null && await directory.ClosureAsync("project", project, cancellationToken) is not null)
        {
            return OidcResults.Forbid(OidcErrors.InvalidClient, InvalidApiKeyNotice);
        }

        var allowed = key.Scopes.Where(scope => !TerraBaseScopes.IsAuth(scope)).ToArray();
        var requested = request.GetScopes();
        if (requested.Except(allowed).Any())
        {
            return OidcResults.Forbid(OidcErrors.InvalidScope, "A requested scope exceeds the key's.");
        }

        var now = clock.GetUtcNow();
        if (key.ExpiresAt is { } expired && expired <= now)
        {
            return TypedResults.Json(new OAuthErrorBody("key_expired", "The API key has expired."), statusCode: StatusCodes.Status401Unauthorized);
        }

        var lifetime = key.ExpiresAt is { } end && end - now < Lifetime ? end - now : Lifetime;
        var identity = Identity(key.Id, key.OrganizationId, project, requested.IsEmpty ? allowed : requested, lifetime)
            .SetClaim(KeyIdClaim, key.Id)
            .SetClaim(KeyGenerationClaim, ApiKeys.Generation(secret))
            .SetExpirationDate(now + lifetime);
        if (key.ExpiresAt is { } keyExpiry)
        {
            identity.SetClaim(KeyExpiryClaim, keyExpiry.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }
        if (key.Database is { } database)
        {
            identity.SetClaim(DatabaseClaim, database);
        }

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

    public async Task<IResult> ExchangeAsync(HttpContext context, OpenIddictRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var subject = (await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        var issuer = OidcIssuer.Issuer(options).AbsoluteUri;
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
                subject.GetClaim(OidcIssuer.MfaClaim), cancellationToken) is not { } user
            || subject.GetClaim(LiveAgents.Claim) is { } agentId && await agents.ActiveAsync(agentId, userId, cancellationToken) is null)
        {
            return OidcResults.Forbid(OidcErrors.InvalidGrant, "The subject token is not a live sign-in token of this client.");
        }

        if (Parameter(request, "organization") is not { } organization)
        {
            return OidcResults.Forbid(OidcErrors.InvalidRequest, "The organization parameter is required.");
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
            return OidcResults.Forbid(OidcErrors.AccessDenied, "mfa_required: the organization requires two-factor authentication; enrol at /ui/terrascale/security.");
        }

        if (allowed.Count == 0)
        {
            return OidcResults.Forbid(OidcErrors.InvalidTarget, "The organization or project is not one this user reaches.");
        }

        allowed = [.. allowed.Where(scope => !TerraBaseScopes.IsAuth(scope))];
        var requested = request.GetScopes();
        if (requested.Except(allowed).Any())
        {
            return OidcResults.Forbid(OidcErrors.InvalidScope, "A requested scope exceeds the user's role.");
        }

        if (!user.MultiFactor && !await mfa.AllowsPrimaryAuthenticationAsync(user, cancellationToken) || expires <= clock.GetUtcNow())
        {
            return OidcResults.Forbid(OidcErrors.InvalidGrant, "The subject token is not a live sign-in token of this client.");
        }

        var lifetime = expires - clock.GetUtcNow();
        var identity = Identity(user.Id, organization, project, requested.IsEmpty ? allowed : requested, lifetime < Lifetime ? lifetime : Lifetime)
            .SetClaim("token_use", "user")
            .SetClaim(Claims.Email, user.Email)
            .SetClaim(Claims.EmailVerified, true)
            .SetClaim(Claims.AuthenticationTime, authenticatedAt);
        return SignIn(identity);
    }

    public async Task<IResult> GitHubActionsAsync(OpenIddictRequest request, OidcApplication? binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ClientId != GitHubActionsGrant.ClientId || binding is null || binding.ClientType != ClientTypes.Public
            || !GitHubActionsGrant.HasExactPermissions(binding.Permissions)
            || !GitHubActionsGrant.TryReadBinding(binding.Properties, clock.GetUtcNow(), out var organization, out var project, out var database, out var deadline)
            || await directory.OrganizationAsync(organization, cancellationToken) is null
            || await directory.ProjectAsync(project, cancellationToken) is not { } found
            || found.Value.OrganizationId != organization)
        {
            return OidcResults.Forbid(OidcErrors.InvalidClient, "The GitHub Actions workload is not bound to a live approved target.");
        }

        foreach (var (name, expected) in new[] { ("organization", organization), ("project", project), ("database", database) })
        {
            if (request.GetParameter(name) is { } target && (string?)target != expected)
            {
                return OidcResults.Forbid(OidcErrors.InvalidTarget, "The requested target differs from the binding.");
            }
        }

        if (request.SubjectToken is not { Length: > 0 } encoded)
        {
            return OidcResults.Forbid(OidcErrors.InvalidRequest, "The subject_token parameter is required.");
        }

        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(encoded, new TokenValidationParameters
        {
            ConfigurationManager = gitHubActions.Configuration,
            ValidIssuer = GitHubActionsGrant.Issuer,
            ValidAudience = GitHubActionsGrant.Audience,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = AuthenticationTimeClockSkew,
        });
        var token = validated.IsValid ? validated.SecurityToken as JsonWebToken : null;
        using var payload = token is null ? null : JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload));
        var claims = payload?.RootElement ?? default;
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
            return OidcResults.Forbid(OidcErrors.InvalidGrant, "The GitHub Actions token does not match the bound workload.");
        }

        var requested = request.GetScopes();
        if (requested.Except(GitHubActionsGrant.Scopes).Any())
        {
            return OidcResults.Forbid(OidcErrors.InvalidScope, "A requested scope exceeds the workload's.");
        }

        var runId = Claim(claims, "run_id")!;
        var tokenId = Claim(claims, "jti")!;
        if (!await data.SaveAsync(Credentials.Claim(PlatformDirectory.Instance, GitHubActionsGrant.TokenIdKind, tokenId, runId), cancellationToken))
        {
            logger.ZLogWarning($"GitHub Actions token refused: jti already used");
            return OidcResults.Forbid(OidcErrors.InvalidGrant, "The GitHub Actions token was already used.");
        }

        var now = clock.GetUtcNow();
        if (deadline <= now)
        {
            return OidcResults.Forbid(OidcErrors.InvalidClient, "The workload binding has expired.");
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
            .SetClaim("token_use", "machine")
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

    private static SignInHttpResult SignIn(ClaimsIdentity identity)
    {
        identity.SetDestinations(_ => [Destinations.AccessToken]);
        return TypedResults.SignIn(new ClaimsPrincipal(identity), null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}

public sealed class SkipShortLivedTokenEntries : OpenIddict.Server.IOpenIddictServerHandler<OpenIddict.Server.OpenIddictServerEvents.GenerateTokenContext>
{
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

public sealed class ApiKeyFailureNotice(ApiKeys keys) : OpenIddict.Server.IOpenIddictServerHandler<OpenIddict.Server.OpenIddictServerEvents.ApplyTokenResponseContext>
{
    public async ValueTask HandleAsync(OpenIddict.Server.OpenIddictServerEvents.ApplyTokenResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Response.Error == OidcErrors.InvalidClient && context.Request?.IsClientCredentialsGrantType() == true
            && context.Request.ClientId is { } clientId
            && await keys.FindAsync(clientId, context.CancellationToken) is not null)
        {
            context.Response.ErrorDescription = PlatformGrants.InvalidApiKeyNotice;
        }
    }
}

public sealed class ApiKeyApplicationManager(
    OpenIddict.Abstractions.IOpenIddictApplicationCache<OidcApplication> cache,
    ILogger<OpenIddict.Core.OpenIddictApplicationManager<OidcApplication>> logger,
    Microsoft.Extensions.Options.IOptionsMonitor<OpenIddict.Core.OpenIddictCoreOptions> options,
    OpenIddict.Abstractions.IOpenIddictApplicationStore<OidcApplication> store,
    TimeProvider clock,
    AttemptLimiter limiter)
    : OpenIddict.Core.OpenIddictApplicationManager<OidcApplication>(cache, logger, options, store)
{
    public const string DigestPrefix = "tsk-sha256:";

    public override async ValueTask<OidcApplication?> FindByClientIdAsync(string identifier, CancellationToken cancellationToken = default)
    {
        return await this.Store.FindByClientIdAsync(identifier, cancellationToken) is { } application
            && string.Equals(await this.Store.GetClientIdAsync(application, cancellationToken), identifier, StringComparison.Ordinal)
                ? application
                : null;
    }

    public override async ValueTask<bool> HasPermissionAsync(OidcApplication application, string permission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        return permission == Permissions.Prefixes.GrantType + AgentExchange.GrantType
            ? application.InstanceId == OidcIssuer.InstanceId && application.ClientId == OidcIssuer.DashboardClientId
            : await base.HasPermissionAsync(application, permission, cancellationToken);
    }

    public override async ValueTask<bool> ValidateRedirectUriAsync(OidcApplication application, [StringSyntax(StringSyntaxAttribute.Uri)] string uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        return application.InstanceId == AgentIdIssuer.Slug && application.ClientType == ClientTypes.Public
            ? AgentIdIssuer.AllowsRedirect(application.ClientId!, uri)
            : await base.ValidateRedirectUriAsync(application, uri, cancellationToken);
    }

    public override async ValueTask<bool> ValidateClientSecretAsync(OidcApplication application, string secret, CancellationToken cancellationToken = default)
    {
        var machine = MachineClients.Policy(application) is not null;
        if (machine && await limiter.CheckAsync(application.InstanceId!, "machine-client-failure", application.ClientId!,
            10, TimeSpan.FromMinutes(15), cancellationToken) is not null)
        {
            return false;
        }

        var valid = await base.ValidateClientSecretAsync(application, secret, cancellationToken)
            || application.PreviousSecretHash is not null && application.PreviousSecretExpiresAt > clock.GetUtcNow()
            && await ValidateClientSecretAsync(secret, application.PreviousSecretHash, cancellationToken);
        if (!valid && machine)
        {
            await limiter.AcquireAsync(application.InstanceId!, "machine-client-failure", application.ClientId!,
                10, TimeSpan.FromMinutes(15), cancellationToken);
        }

        return valid;
    }

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

internal static class ApiKeyClients
{
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
