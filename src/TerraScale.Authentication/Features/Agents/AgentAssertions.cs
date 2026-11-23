using Dapper;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Agents;

public sealed record AgentAssertionRecord(string ProjectId, string AgentId, string Jti, DateTimeOffset KeepUntil)
{
    public string? Version { get; init; }

    public const int MaxJtiLength = 128;
}

public sealed record VerifiedAgentAssertion(Versioned<AgentKeyRecord> Key, Versioned<AgentRecord> Agent, Versioned<UserRecord> Owner, JsonWebToken Token, string Jti,
    DateTimeOffset AcceptedUntil)
{
    public Change Replay => Change.Insert(new AgentAssertionRecord(this.Agent.Value.ProjectId, this.Agent.Value.Id, this.Jti, this.AcceptedUntil + AgentAssertions.ReplayRetention));
}

public sealed class AgentAssertions(AuthData data, TimeProvider clock)
{
    public const int MaxLength = 8192;

    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan ReplayRetention = TimeSpan.FromMinutes(10);

    private const long MaxUnixSeconds = 253402300799;

    private static readonly string[] ForbiddenHeaders = ["jwk", "jku", "x5u", "x5c", "crit"];

    private static readonly JsonWebTokenHandler Handler = new();

    private static readonly JsonWebKey Decoy = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(ECDsa.Create(ECCurve.NamedCurves.nistP256)));

    public async Task<(VerifiedAgentAssertion? Verified, string? AgentId, string Reason)> VerifyAsync(string project, string assertion, string audience, CancellationToken cancellationToken)
    {
        if (assertion.Length > MaxLength || !Handler.CanReadToken(assertion) || Read(assertion) is not { IsEncrypted: false, Alg: SecurityAlgorithms.EcdsaSha256, Kid: { } kid } unverified
            || !TerraScaleIds.IsValid(kid) || ForbiddenHeaders.Any(name => unverified.TryGetHeaderValue<object>(name, out _)))
        {
            return (null, null, "malformed assertion or header");
        }

        var key = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AgentKeyRecord>(
            "SELECT * FROM agent_keys WHERE project_id = @project AND kid = @kid", new { project, kid }), cancellationToken));
        var validated = await Handler.ValidateTokenAsync(unverified, new TokenValidationParameters
        {
            IssuerSigningKey = key is null ? Decoy : new JsonWebKey(key.Value.PublicJwk) { KeyId = kid },
            TryAllIssuerSigningKeys = key is null,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidIssuer = key?.Value.AgentId ?? kid,
            ValidAudience = audience,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = ClockSkew,
        });
        if (key is null)
        {
            return (null, null, "unknown key");
        }

        var agentId = key.Value.AgentId;
        if (!validated.IsValid || unverified.Subject != agentId || unverified.Audiences.Count() != 1
            || unverified.Id is not { Length: > 0 and <= AgentAssertionRecord.MaxJtiLength } jti || jti.Any(char.IsControl))
        {
            return (null, agentId, "signature or claims");
        }

        var agent = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AgentRecord>(
            "SELECT * FROM agents WHERE project_id = @project AND id = @agentId", new { project, agentId }), cancellationToken));
        var ownerId = agent?.Value.OwnerUserId;
        var owner = agent is null ? null : Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<UserRecord>(
            "SELECT * FROM users WHERE instance_id = @project AND id = @ownerId", new { project, ownerId }), cancellationToken));
        if (agent is null)
        {
            return (null, agentId, "unknown agent");
        }

        var now = clock.GetUtcNow();
        if (Times(unverified) is not var (iat, exp) || iat is < 0 or > MaxUnixSeconds || exp is < 0 or > MaxUnixSeconds || iat >= exp || exp - iat > (long)MaxLifetime.TotalSeconds
            || DateTimeOffset.FromUnixTimeSeconds(iat) > now + ClockSkew)
        {
            return (null, agentId, "assertion times");
        }

        if (owner is null || !Active(key.Value, agent.Value, owner.Value, now))
        {
            return (null, agentId, "agent, key or owner not active");
        }

        return (new(key, agent, owner, unverified, jti, DateTimeOffset.FromUnixTimeSeconds(exp) + ClockSkew), agentId, string.Empty);
    }

    public static bool Active(AgentKeyRecord key, AgentRecord agent, UserRecord owner, DateTimeOffset now) =>
        key.RevokedAt is null && !(agent.ExpiresAt <= now)
        && AgentDirectory.Status(agent, new AgentOwner(owner.Id, owner.AuthEpoch, owner.AuthStamp, owner.Disabled)) == AgentStatus.Active;

    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return await data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
            """
            DELETE FROM agent_assertions WHERE (project_id, agent_id, jti) IN (
                SELECT project_id, agent_id, jti FROM agent_assertions WHERE keep_until <= @now ORDER BY keep_until LIMIT @batchSize)
            """, new { now, batchSize }), cancellationToken);
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
}
