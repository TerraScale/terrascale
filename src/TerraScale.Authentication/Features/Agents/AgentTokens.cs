using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;
using ZLogger;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Agents;

public sealed record AgentAssertionRecord(string ProjectId, string AgentId, string Jti, DateTimeOffset KeepUntil);

public sealed record AgentTokenBody(string AccessToken, string TokenType, long ExpiresIn, string Scope);

public sealed class AgentTokens(AuthData data, KeyRing ring, AttemptLimiter limiter, AuthOptions options, TimeProvider clock, ILogger<AgentTokens> logger)
{
    public const string GrantType = "urn:ietf:params:oauth:grant-type:jwt-bearer";

    public const string TokenUse = "agent";

    public const int AgentMinuteBudget = 60;

    public const int OwnerMinuteBudget = 600;

    public const int MaxAssertionLength = 8192;

    public const int MaxJtiLength = 128;

    public static readonly TimeSpan Lifetime = OidcIssuer.TokenLifetime;

    public static readonly TimeSpan MaxAssertionLifetime = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan LastUsedInterval = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan ReplayRetention = TimeSpan.FromMinutes(10);

    private const long MaxUnixSeconds = 253402300799;

    private const string RequestedSubject = "requested_subject";

    private const string Refusal = "The assertion is not valid, or its agent cannot sign in.";

    private static readonly FrozenSet<string> Accepted = new[] { Parameters.GrantType, Parameters.Assertion, Parameters.Scope, RequestedSubject }
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] ForbiddenHeaders = ["jwk", "jku", "x5u", "x5c", "crit"];

    private static readonly JsonWebTokenHandler Handler = new();

    public static string TokenEndpoint(AuthOptions options, string project) => OidcIssuer.Issuer(options, project).AbsoluteUri + "/oauth/token";

    public async Task<IResult> GrantAsync(string project, OpenIddictRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var subject = request.GetParameter(RequestedSubject)?.GetRawValue();
        if (request.GetParameters().Any(parameter => !Accepted.Contains(parameter.Key))
            || request.GetParameter(Parameters.Assertion)?.GetRawValue() is not string { Length: > 0 and <= MaxAssertionLength } assertion
            || !TryScopes(request.GetParameter(Parameters.Scope)?.GetRawValue(), out var scopes)
            || subject is not (null or "owner" or "agent"))
        {
            return Error(Errors.InvalidRequest, "Send grant_type, one assertion, and optionally scope and requested_subject (owner or agent); nothing else.");
        }

        return await this.IssueAsync(project, assertion, scopes, subject is "agent", cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return await data.InBatchesAsync(batchSize, (db, token) => db.AgentAssertions.Where(row => row.KeepUntil <= now)
            .OrderBy(row => row.KeepUntil).Take(batchSize).ExecuteDeleteAsync(token), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IResult> IssueAsync(string project, string assertion, IReadOnlyList<string>? requested, bool self, CancellationToken cancellationToken)
    {
        if (!Handler.CanReadToken(assertion) || Read(assertion) is not { IsEncrypted: false, Alg: SecurityAlgorithms.EcdsaSha256, Kid: { } kid } unverified
            || !TerraScaleIds.IsValid(kid) || ForbiddenHeaders.Any(name => unverified.TryGetHeaderValue<object>(name, out _)))
        {
            return this.Refuse(project, null, "malformed assertion or header");
        }

        var (key, agent, owner) = await data.ReadAsync(async (db, token) =>
        {
            var key = await db.AgentKeys.Where(row => row.ProjectId == project && row.Kid == kid).WithVersion().SingleOrDefaultAsync(token).ConfigureAwait(false);
            var agent = key is null ? null
                : await db.Agents.Where(row => row.ProjectId == project && row.Id == key.Value.AgentId).WithVersion().SingleOrDefaultAsync(token).ConfigureAwait(false);
            var owner = agent is null ? null
                : await db.Users.Where(row => row.InstanceId == project && row.Id == agent.Value.OwnerUserId).WithVersion().SingleOrDefaultAsync(token).ConfigureAwait(false);
            return (key, agent, owner);
        }, cancellationToken).ConfigureAwait(false);
        if (key is null || agent is null)
        {
            return this.Refuse(project, null, "unknown key");
        }

        var agentId = agent.Value.Id;
        var validated = await Handler.ValidateTokenAsync(unverified, new TokenValidationParameters
        {
            IssuerSigningKey = new JsonWebKey(key.Value.PublicJwk) { KeyId = kid },
            TryAllIssuerSigningKeys = false,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidIssuer = agentId,
            ValidAudience = TokenEndpoint(options, project),
            IgnoreTrailingSlashWhenValidatingAudience = false,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = ClockSkew,
        }).ConfigureAwait(false);
        if (!validated.IsValid || unverified.Subject != agentId || unverified.Audiences.Count() != 1
            || unverified.Id is not { Length: > 0 and <= MaxJtiLength } jti || jti.Any(char.IsControl))
        {
            return this.Refuse(project, agentId, "signature or claims");
        }

        var now = clock.GetUtcNow();
        if (Times(unverified) is not var (iat, exp) || iat is < 0 or > MaxUnixSeconds || exp is < 0 or > MaxUnixSeconds || iat >= exp || exp - iat > (long)MaxAssertionLifetime.TotalSeconds
            || DateTimeOffset.FromUnixTimeSeconds(iat) > now + ClockSkew)
        {
            return this.Refuse(project, agentId, "assertion times");
        }

        if (owner is null || key.Value.RevokedAt is not null || agent.Value.ExpiresAt <= now
            || AgentDirectory.Status(agent.Value, new AgentOwner(owner.Value.Id, owner.Value.AuthEpoch, owner.Value.AuthStamp, owner.Value.Disabled)) != AgentStatus.Active)
        {
            return this.Refuse(project, agentId, "agent, key or owner not active");
        }

        var scopes = requested ?? agent.Value.AllowedScopes;
        if (scopes.Except(agent.Value.AllowedScopes, StringComparer.Ordinal).Any() || self && !agent.Value.AllowSelfSubject)
        {
            return this.Refuse(project, agentId, self && !agent.Value.AllowSelfSubject ? "self subject not allowed" : "scope outside the agent's allowed scopes");
        }

        if ((await limiter.AcquireAsync(project, "agent-token-agent", agentId, AgentMinuteBudget, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false)
            ?? await limiter.AcquireAsync(project, "agent-token-owner", agent.Value.OwnerUserId, OwnerMinuteBudget, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false))
            is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        var scope = string.Join(' ', scopes);
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(Math.Min((issuedAt + Lifetime).ToUnixTimeSeconds(), agent.Value.ExpiresAt?.ToUnixTimeSeconds() ?? long.MaxValue));
        var body = new AgentTokenBody(await this.SignAsync(project, agent.Value, scope, self, issuedAt, expiresAt, cancellationToken).ConfigureAwait(false), "Bearer",
            (long)(expiresAt - issuedAt).TotalSeconds, scope);
        var acceptedUntil = DateTimeOffset.FromUnixTimeSeconds(exp) + ClockSkew;
        var gate = clock.GetUtcNow();
        if (gate > acceptedUntil || expiresAt <= gate)
        {
            return this.Refuse(project, agentId, "assertion or agent expired during issuance");
        }

        List<Change> changes = [Change.Insert(new AgentAssertionRecord(project, agentId, jti, acceptedUntil + ReplayRetention)), Change.Assert(key), Change.Assert(owner)];
        if (agent.Value.LastUsedAt is not { } used || used <= now - LastUsedInterval)
        {
            changes.Add(Change.Update(agent, agent.Value with { LastUsedAt = now }));
        }

        return await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false)
            ? Results.Json(body, AgentJson.Default.AgentTokenBody)
            : this.Refuse(project, agentId, "assertion replayed, or the key, owner or agent changed");
    }

    private async Task<string> SignAsync(string project, AgentRecord agent, string scope, bool self, DateTimeOffset issuedAt, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var signing = await ring.GetActiveAsync(project, cancellationToken).ConfigureAwait(false);
        try
        {
            var claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtRegisteredClaimNames.Sub] = self ? agent.Id : agent.OwnerUserId,
                [JwtRegisteredClaimNames.Jti] = AuthIds.New(),
                ["token_use"] = TokenUse,
                ["actor_type"] = "agent",
                ["agent_id"] = agent.Id,
                [Claims.Scope] = scope,
            };
            if (self)
            {
                claims["owner_sub"] = agent.OwnerUserId;
            }
            else
            {
                claims["act"] = new Dictionary<string, object>(StringComparer.Ordinal) { [JwtRegisteredClaimNames.Sub] = agent.Id };
            }

            return Handler.CreateToken(new SecurityTokenDescriptor
            {
                TokenType = "at+jwt",
                Issuer = OidcIssuer.Issuer(options, project).AbsoluteUri,
                IssuedAt = issuedAt.UtcDateTime,
                NotBefore = issuedAt.UtcDateTime,
                Expires = expiresAt.UtcDateTime,
                SigningCredentials = new SigningCredentials(signing[0].SecurityKey, SecurityAlgorithms.EcdsaSha256),
                Claims = claims,
            });
        }
        finally
        {
            foreach (var signer in signing)
            {
                signer.Dispose();
            }
        }
    }

    private static (long Iat, long Exp)? Times(JsonWebToken assertion)
    {
        try
        {
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(assertion.EncodedPayload), new JsonDocumentOptions { AllowDuplicateProperties = false });
            return payload.RootElement.TryGetProperty(JwtRegisteredClaimNames.Iat, out var iat) && iat.ValueKind == JsonValueKind.Number && iat.TryGetInt64(out var issued)
                && payload.RootElement.TryGetProperty(JwtRegisteredClaimNames.Exp, out var exp) && exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out var expires)
                ? (issued, expires) : null;
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return null;
        }
    }

    private static JsonWebToken? Read(string assertion)
    {
        try
        {
            return Handler.ReadJsonWebToken(assertion);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool TryScopes(object? value, out IReadOnlyList<string>? scopes)
    {
        scopes = value switch
        {
            null => null,
            string text when text.Split(' ') is var requested && MachineClients.ValidScopes(requested) => requested,
            _ => [],
        };
        return value is null || scopes is { Count: > 0 };
    }

    private IResult Refuse(string project, string? agentId, string reason)
    {
        logger.ZLogInformation($"Agent token refused in project {project} for agent {agentId ?? "unknown"}: {reason}");
        return Error(Errors.InvalidGrant, Refusal);
    }

    private static IResult Error(string error, string description) =>
        Results.Json(new OAuthErrorBody(error, description), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status400BadRequest);
}

internal sealed class RouteAgentGrant : IOpenIddictServerHandler<ExtractTokenRequestContext>
{
    public ValueTask HandleAsync(ExtractTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request?.GrantType == AgentTokens.GrantType && Tenancy.IsProject(Tenancy.Current(context.Transaction.GetHttpRequest()?.HttpContext)))
        {
            context.SkipRequest();
        }

        return ValueTask.CompletedTask;
    }
}
