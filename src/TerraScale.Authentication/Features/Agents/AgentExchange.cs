using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;
using ZLogger;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Agents;

public sealed record LiveAgent(Versioned<AgentRecord> Agent, Versioned<UserRecord> Owner);

public sealed class LiveAgents(AuthData data, TimeProvider clock)
{
    public const string Claim = "agent_id";

    public async Task<LiveAgent?> ActiveAsync(string agentId, string ownerId, CancellationToken cancellationToken)
    {
        const string tenant = OidcIssuer.InstanceId;
        if (!TerraScaleIds.IsValid(agentId) || !TerraScaleIds.IsValid(ownerId))
        {
            return null;
        }

        using var home = HomeReads.Begin();
        var (agent, owner, keyed) = await data.ReadAsync(async (db, token) =>
        {
            var agent = await db.Agents.Where(row => row.ProjectId == tenant && row.Id == agentId && row.OwnerUserId == ownerId).WithVersion()
                .SingleOrDefaultAsync(token).ConfigureAwait(false);
            var owner = agent is null ? null : await db.Users.Where(row => row.InstanceId == tenant && row.Id == ownerId).WithVersion().SingleOrDefaultAsync(token).ConfigureAwait(false);
            var keyed = agent is not null && await db.AgentKeys.AnyAsync(row => row.ProjectId == tenant && row.AgentId == agentId && row.RevokedAt == null, token).ConfigureAwait(false);
            return (agent, owner, keyed);
        }, cancellationToken).ConfigureAwait(false);
        return agent is not null && owner is not null && keyed && (agent.Value.ExpiresAt is not { } expires || expires > clock.GetUtcNow())
            && AgentDirectory.Status(agent.Value, new AgentOwner(owner.Value.Id, owner.Value.AuthEpoch, owner.Value.AuthStamp, owner.Value.Disabled)) == AgentStatus.Active
                ? new(agent, owner)
                : null;
    }
}

public sealed class AgentExchange(KeyRing ring, LiveAgents live, AuthData data, AuthOptions options, TimeProvider clock, ILogger<AgentExchange> logger)
{
    public const string GrantType = "urn:terrascale:params:oauth:grant-type:agent-exchange";

    public const string SubjectTokenType = "urn:ietf:params:oauth:token-type:access_token";

    internal const string SubjectExpiry = "ts-auth:agent-subject-expiry";

    private const int MaxTokenLength = 8 * 1024;

    private const string Refusal = "The subject token is not a live delegated agent token of the TerraScale platform.";

    private static readonly FrozenSet<string> Accepted = new[]
    {
        Parameters.GrantType, Parameters.ClientId, Parameters.ClientSecret, Parameters.SubjectToken, Parameters.SubjectTokenType,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<IResult> ExchangeAsync(HttpContext context, OpenIddictRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ClientId != OidcIssuer.DashboardClientId)
        {
            return OidcResults.Forbid(Errors.UnauthorizedClient, "Only the TerraScale dashboard exchanges agent tokens.");
        }

        if (request.GetParameters().Any(parameter => !Accepted.Contains(parameter.Key)) || request.SubjectTokenType != SubjectTokenType
            || request.SubjectToken is not { Length: > 0 and <= MaxTokenLength } subject)
        {
            return OidcResults.Forbid(Errors.InvalidRequest, $"Send grant_type, subject_token and subject_token_type {SubjectTokenType}; nothing else.");
        }

        if (await this.ValidateAsync(subject).ConfigureAwait(false) is not var (ownerId, agentId, expires))
        {
            return this.Refuse(null, "subject token signature, issuer, type or claims");
        }

        if (await live.ActiveAsync(agentId, ownerId, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return this.Refuse(agentId, "agent, key or owner not active");
        }

        if (!await data.SaveAsync([Change.Assert(current.Agent), Change.Assert(current.Owner)], cancellationToken).ConfigureAwait(false))
        {
            return this.Refuse(agentId, "agent or owner changed during the exchange");
        }

        var lifetime = TimeSpan.FromSeconds(Math.Floor(Math.Min(OidcIssuer.TokenLifetime.TotalSeconds, (expires - clock.GetUtcNow()).TotalSeconds)));
        if (lifetime <= TimeSpan.Zero)
        {
            return this.Refuse(agentId, "subject token expires now");
        }

        context.Items[SubjectExpiry] = expires;
        logger.ZLogInformation($"Agent token exchanged for agent {agentId} of owner {ownerId}");
        return OidcResults.SignIn(options, current.Owner.Value with { MultiFactor = false, MfaMethod = null, SessionId = null }, current.Agent.Value.CreatedAt, [],
            OidcIssuer.SignInAudiences(options),
            agentId: agentId, lifetime: lifetime);
    }

    private async Task<(string Owner, string Agent, DateTimeOffset Expires)?> ValidateAsync(string subject)
    {
        if (!Handler.CanReadToken(subject))
        {
            return null;
        }

        var keys = await ring.GetPublicAsync(OidcIssuer.InstanceId, Jwt.KeyId(subject), CancellationToken.None).ConfigureAwait(false);
        try
        {
            var result = await Handler.ValidateTokenAsync(subject, new TokenValidationParameters
            {
                IssuerSigningKeys = [.. keys.Select(key => key.SecurityKey)],
                ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                ValidTypes = ["at+jwt"],
                ValidIssuer = OidcIssuer.Issuer(options).AbsoluteUri,
                ValidateAudience = false,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
            }).ConfigureAwait(false);
            if (!result.IsValid || result.SecurityToken is not JsonWebToken token || token.Audiences.Any())
            {
                return null;
            }

            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload), new JsonDocumentOptions { AllowDuplicateProperties = false });
            var claims = payload.RootElement;
            return Text(claims, "token_use") == AgentTokens.TokenUse && Text(claims, "actor_type") == "agent" && !claims.TryGetProperty("owner_sub", out _)
                && Text(claims, LiveAgents.Claim) is { } agentId && Text(claims, JwtRegisteredClaimNames.Sub) is { } ownerId && ownerId != agentId
                && claims.TryGetProperty("act", out var act) && act.ValueKind == JsonValueKind.Object && Text(act, JwtRegisteredClaimNames.Sub) == agentId
                    ? (ownerId, agentId, new DateTimeOffset(token.ValidTo, TimeSpan.Zero))
                    : null;
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
        finally
        {
            foreach (var key in keys)
            {
                key.Dispose();
            }
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private IResult Refuse(string? agentId, string reason)
    {
        logger.ZLogInformation($"Agent token exchange refused for agent {agentId ?? "unknown"}: {reason}");
        return OidcResults.Forbid(Errors.InvalidGrant, Refusal);
    }
}
