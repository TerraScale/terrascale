using Dapper;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Primitives;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Agents;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;
using ZLogger;

namespace TerraScale.Authentication.Features.AgentId;

public sealed record AgentIdTransactionRecord(string Id, string RequestHash, string ClientId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? AgentId = null,
    string? OwnerUserId = null, string? Kid = null, DateTimeOffset? ProvedAt = null, DateTimeOffset? CompletedAt = null)
{
    public string? Version { get; init; }

    public const int RequestHashLength = 43;
}

public sealed record AgentIdProofRequest(string? Proof);

public sealed record AgentIdPendingBody(string Status, string TransactionId, string ProofUrl, string ClientId, string ClientName, DateTimeOffset ExpiresAt);

public sealed record AgentIdSignIn(AgentRecord Agent, UserRecord Owner, string Kid);

public readonly union ProveResult(AuthCompleted, AuthNotFound, AuthUnauthorized);

public sealed record AgentIdFence(Versioned<AgentKeyRecord> Key, Versioned<AgentRecord> Agent, Versioned<UserRecord> Owner, AgentIdRegistration? Registration = null)
{
    public const string Item = "ts-auth:agentid-fence";

    public IReadOnlyList<Change> Asserts => [Change.Assert(this.Key), Change.Assert(this.Agent), Change.Assert(this.Owner), .. this.Registration?.Asserts ?? []];

    public AgentIdSignIn SignIn => new(this.Agent.Value, this.Owner.Value, this.Key.Value.Kid);
}

public sealed class AgentIdTransactions(AuthData data, AgentAssertions assertions, AttemptLimiter limiter, AuthOptions options, TimeProvider clock, ILogger<AgentIdTransactions> logger)
{
    public const int ProofsPerAgentMinute = 30;

    public const int RequestsPerClientMinute = 600;

    public const int AuthorizationsPerAddressMinute = 120;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    public const string Expired = "This sign-in expired. Start again from the app.";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static string RequestHash(IEnumerable<KeyValuePair<string, StringValues>> parameters)
    {
        var canonical = new StringBuilder();
        foreach (var (key, values) in parameters.OrderBy(parameter => parameter.Key, StringComparer.Ordinal))
        {
            foreach (var value in values)
            {
                canonical.Append(key.Length).Append(':').Append(key).Append(value?.Length ?? -1).Append(':').Append(value);
            }
        }

        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    public async Task<TimeSpan?> LimitAsync(string purpose, string ip, string clientId, CancellationToken cancellationToken)
    {
        return (await limiter.AcquireAsync(AgentIdIssuer.Slug, purpose + "-ip", limiter.Fold(ip), AuthorizationsPerAddressMinute, Window, cancellationToken)
            ?? await limiter.AcquireAsync(AgentIdIssuer.Slug, purpose + "-client", limiter.Fold(clientId), RequestsPerClientMinute, Window, cancellationToken));
    }

    public async Task<AgentIdTransactionRecord> OpenAsync(string requestHash, string clientId, CancellationToken cancellationToken)
    {
        if (await this.FindByRequestAsync(requestHash, cancellationToken) is { } existing)
        {
            return existing;
        }

        var now = clock.GetUtcNow();
        var created = new AgentIdTransactionRecord(AuthIds.New(), requestHash, clientId, now, now + Lifetime);
        return await data.SaveAsync(Change.Insert(created), cancellationToken)
            ? created
            : await this.FindByRequestAsync(requestHash, cancellationToken) ?? throw ApiException.Unavailable();
    }

    public bool IsOpen(AgentIdTransactionRecord transaction) => transaction.CompletedAt is null && transaction.ExpiresAt > clock.GetUtcNow();

    public async Task<ProveResult> ProveAsync(string id, string? proof, CancellationToken cancellationToken)
    {
        var current = TerraScaleIds.IsValid(id) ? await this.FindAsync(id, cancellationToken) : null;
        if (current is not { Value: var transaction } || !this.IsOpen(transaction))
        {
            return new AuthNotFound(Expired, "expired_transaction");
        }

        var (verified, refusedAgent, reason) = proof is { Length: > 0 }
            ? await assertions.VerifyAsync(PlatformTenant.Id, proof, AgentIdIssuer.ProofUrl(options, id), cancellationToken)
            : (null, null, "no proof");
        if (verified is null
            || !verified.Token.TryGetPayloadValue<string>(AgentIdIssuer.ProofTransactionClaim, out var bound) || bound != id
            || !verified.Token.TryGetPayloadValue<string>(AgentIdIssuer.ProofClientClaim, out var client) || client != transaction.ClientId)
        {
            return Refused(verified?.Agent.Value.Id ?? refusedAgent, verified is null ? reason : "transaction or client_id not bound");
        }

        var agent = verified.Agent.Value;
        if (transaction.ProvedAt is not null)
        {
            return Refused(agent.Id, "transaction already proved");
        }

        if ((await limiter.AcquireAsync(AgentIdIssuer.Slug, "agentid-proof-agent", agent.Id, ProofsPerAgentMinute, Window, cancellationToken)
            ?? await limiter.AcquireAsync(AgentIdIssuer.Slug, "agentid-proof-client", limiter.Fold(transaction.ClientId), RequestsPerClientMinute, Window, cancellationToken))
            is { } wait)
        {
            throw ApiException.RateLimited("Too many proofs for this agent; retry later.", wait);
        }

        var now = clock.GetUtcNow();
        if (transaction.ExpiresAt <= now || verified.AcceptedUntil <= now)
        {
            return Refused(agent.Id, "transaction or proof expired before the proof was recorded");
        }

        var proved = transaction with { AgentId = agent.Id, OwnerUserId = agent.OwnerUserId, Kid = verified.Key.Value.Kid, ProvedAt = now };
        if (!await data.SaveAsync([verified.Replay, Change.Update(current, proved), Change.Assert(verified.Key), Change.Assert(verified.Owner)], cancellationToken))
        {
            return Refused(agent.Id, "proof replayed, or the transaction, key or owner changed");
        }

        return new AuthCompleted();

        AuthUnauthorized Refused(string? agentId, string why)
        {
            logger.ZLogInformation($"Agent sign-in proof refused for transaction {id} and agent {agentId ?? "unknown"}: {why}");
            return new("Sign an ES256 JWT with an active key of an active agent: iss and sub the agent id, aud this URL, txn this transaction, client_id the app's client_id, a jti, iat, and exp at most 300 seconds after iat.",
                "invalid_proof");
        }
    }

    public async Task<AgentIdSignIn?> CompleteAsync(AgentIdTransactionRecord transaction, CancellationToken cancellationToken)
    {
        if (transaction is not { AgentId: { } agentId, OwnerUserId: { } ownerId, Kid: { } kid, ProvedAt: not null } || !this.IsOpen(transaction))
        {
            return null;
        }

        var current = await this.FindAsync(transaction.Id, cancellationToken);
        var live = await this.ReadLiveAsync(agentId, ownerId, kid, cancellationToken);
        if (current is null || current.Value != transaction || !this.IsOpen(current.Value) || live is not ({ } key, { } agent, { } owner))
        {
            logger.ZLogInformation($"Agent sign-in refused at completion for transaction {transaction.Id} and agent {agentId}: transaction changed or expired, or agent, key or owner not active");
            return null;
        }

        return await data.SaveAsync([Change.Update(current, transaction with { CompletedAt = clock.GetUtcNow() }), Change.Assert(key), Change.Assert(owner)], cancellationToken)

            ? new(agent.Value, owner.Value, kid)
            : null;
    }

    public async Task<AgentIdFence?> LiveAsync(string agentId, string ownerId, string kid, CancellationToken cancellationToken)
    {
        return await this.ReadLiveAsync(agentId, ownerId, kid, cancellationToken) is ({ } key, { } agent, { } owner)
            ? new(key, agent, owner)
            : null;
    }

    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - Retention;
        return await data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
            "DELETE FROM agentid_transactions WHERE id IN (SELECT id FROM agentid_transactions WHERE expires_at <= @cutoff ORDER BY expires_at LIMIT @batchSize)",
            new { cutoff, batchSize }), cancellationToken);
    }

    private async Task<(Versioned<AgentKeyRecord> Key, Versioned<AgentRecord> Agent, Versioned<UserRecord> Owner)?> ReadLiveAsync(string agentId, string ownerId, string kid,
        CancellationToken cancellationToken)
    {
        const string tenant = PlatformTenant.Id;
        if (!TerraScaleIds.IsValid(agentId) || !TerraScaleIds.IsValid(ownerId) || !TerraScaleIds.IsValid(kid))
        {
            return null;
        }

        var key = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AgentKeyRecord>(
            "SELECT * FROM agent_keys WHERE project_id = @tenant AND kid = @kid AND agent_id = @agentId", new { tenant, kid, agentId }), cancellationToken));
        var agent = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AgentRecord>(
            "SELECT * FROM agents WHERE project_id = @tenant AND id = @agentId AND owner_user_id = @ownerId", new { tenant, agentId, ownerId }), cancellationToken));
        var owner = Versioned.Of(await data.ReadUserAsync(connection => connection.QuerySingleOrDefaultAsync<UserRecord>(
            "SELECT * FROM users WHERE instance_id = @tenant AND id = @ownerId", new { tenant, ownerId }), cancellationToken));
        return key is not null && agent is not null && owner is not null && AgentAssertions.Active(key.Value, agent.Value, owner.Value, clock.GetUtcNow())
            ? (key, agent, owner)
            : null;
    }

    private async Task<Versioned<AgentIdTransactionRecord>?> FindAsync(string id, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AgentIdTransactionRecord>(
            "SELECT * FROM agentid_transactions WHERE id = @id", new { id }), cancellationToken));

    private Task<AgentIdTransactionRecord?> FindByRequestAsync(string requestHash, CancellationToken cancellationToken) =>
        data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AgentIdTransactionRecord>(
            "SELECT * FROM agentid_transactions WHERE request_hash = @requestHash", new { requestHash }), cancellationToken);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(AgentIdProofRequest))]
[JsonSerializable(typeof(AgentIdPendingBody))]
[JsonSerializable(typeof(AgentIdUserInfoBody))]
internal sealed partial class AgentIdJson : JsonSerializerContext;
