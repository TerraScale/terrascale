using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OidcErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;
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

public sealed record AgentTokenBody(string AccessToken, string TokenType, long ExpiresIn, string Scope);

public sealed class AgentTokens(AuthData data, AgentAssertions assertions, KeyRing ring, AttemptLimiter limiter, AuthOptions options, TimeProvider clock, ILogger<AgentTokens> logger)
{
    public const string GrantType = "urn:ietf:params:oauth:grant-type:jwt-bearer";

    public const string TokenUse = "agent";

    public const int AgentMinuteBudget = 60;

    public const int OwnerMinuteBudget = 600;

    public static readonly TimeSpan Lifetime = OidcIssuer.TokenLifetime;

    public static readonly TimeSpan LastUsedInterval = TimeSpan.FromMinutes(1);

    private const string RequestedSubject = "requested_subject";

    private const string Refusal = "The assertion is not valid, or its agent cannot sign in.";

    private static readonly FrozenSet<string> Accepted = new[] { Parameters.GrantType, Parameters.Assertion, Parameters.Scope, RequestedSubject }
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly JsonWebTokenHandler Handler = new();

    public static string TokenEndpoint(AuthOptions options, string project) => OidcIssuer.Issuer(options, project).AbsoluteUri + "/oauth/token";

    public async Task<IResult> GrantAsync(string project, OpenIddictRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var subject = request.GetParameter(RequestedSubject)?.GetRawValue();
        if (request.GetParameters().Any(parameter => !Accepted.Contains(parameter.Key))
            || request.GetParameter(Parameters.Assertion)?.GetRawValue() is not string { Length: > 0 and <= AgentAssertions.MaxLength } assertion
            || !TryScopes(request.GetParameter(Parameters.Scope)?.GetRawValue(), out var scopes)
            || subject is not (null or "owner" or "agent"))
        {
            return Error(OidcErrors.InvalidRequest, "Send grant_type, one assertion, and optionally scope and requested_subject (owner or agent); nothing else.");
        }

        return await this.IssueAsync(project, assertion, scopes, subject is "agent", cancellationToken).ConfigureAwait(false);
    }

    private async Task<IResult> IssueAsync(string project, string assertion, IReadOnlyList<string>? requested, bool self, CancellationToken cancellationToken)
    {
        var (verified, refusedAgent, reason) = await assertions.VerifyAsync(project, assertion, TokenEndpoint(options, project), cancellationToken).ConfigureAwait(false);
        if (verified is null)
        {
            return this.Refuse(project, refusedAgent, reason);
        }

        var (key, agent, owner) = (verified.Key, verified.Agent, verified.Owner);
        var agentId = agent.Value.Id;
        var now = clock.GetUtcNow();
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
        var gate = clock.GetUtcNow();
        if (gate > verified.AcceptedUntil || expiresAt <= gate)
        {
            return this.Refuse(project, agentId, "assertion or agent expired during issuance");
        }

        List<Change> changes = [verified.Replay, Change.Assert(key), Change.Assert(owner)];
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
        return Error(OidcErrors.InvalidGrant, Refusal);
    }

    private static IResult Error(string error, string description) =>
        Results.Json(new OAuthErrorBody(error, description), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status400BadRequest);
}

internal sealed class RouteAgentGrant : IOpenIddictServerHandler<ExtractTokenRequestContext>
{
    public ValueTask HandleAsync(ExtractTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request?.GrantType == AgentTokens.GrantType)
        {
            context.SkipRequest();
        }

        return ValueTask.CompletedTask;
    }
}
