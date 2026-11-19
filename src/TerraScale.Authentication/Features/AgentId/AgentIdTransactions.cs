using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Agents;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Storage;
using ZLogger;

namespace TerraScale.Authentication.Features.AgentId;

public sealed record AgentIdTransactionRecord(string Id, string ClientId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? AgentId = null,
    string? OwnerUserId = null, string? Kid = null, DateTimeOffset? ProvedAt = null, DateTimeOffset? CompletedAt = null)
{
    public const int IdLength = 43;
}

public sealed record AgentIdProofRequest(string? Proof);

public sealed record AgentIdPendingBody(string Status, string TransactionId, string ProofUrl, string ClientId, string ClientName, DateTimeOffset ExpiresAt);

public sealed record AgentIdSignIn(AgentRecord Agent, UserRecord Owner);

public sealed class AgentIdTransactions(AuthData data, AgentAssertions assertions, AttemptLimiter limiter, AuthOptions options, TimeProvider clock, ILogger<AgentIdTransactions> logger)
{
    public const int ProofsPerAgentMinute = 30;

    public const int RequestsPerClientMinute = 600;

    public const int AuthorizationsPerAddressMinute = 120;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public const string Expired = "This sign-in expired. Start again from the app.";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static string Id(IEnumerable<KeyValuePair<string, StringValues>> parameters)
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

    public async Task LimitAsync(string purpose, string ip, string clientId, CancellationToken cancellationToken)
    {
        if ((await limiter.AcquireAsync(AgentIdIssuer.Slug, purpose + "-ip", limiter.Fold(ip), AuthorizationsPerAddressMinute, Window, cancellationToken).ConfigureAwait(false)
            ?? await limiter.AcquireAsync(AgentIdIssuer.Slug, purpose + "-client", limiter.Fold(clientId), RequestsPerClientMinute, Window, cancellationToken).ConfigureAwait(false))
            is { } wait)
        {
            throw ApiException.RateLimited("Too many sign-in attempts; retry later.", wait);
        }
    }

    public async Task<AgentIdTransactionRecord> OpenAsync(string id, string clientId, CancellationToken cancellationToken)
    {
        if (await this.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing.Value;
        }

        var now = clock.GetUtcNow();
        var created = new AgentIdTransactionRecord(id, clientId, now, now + Lifetime);
        return await data.SaveAsync(Change.Insert(created), cancellationToken).ConfigureAwait(false)
            ? created
            : (await this.FindAsync(id, cancellationToken).ConfigureAwait(false))?.Value ?? throw ApiException.Unavailable();
    }

    public bool IsOpen(AgentIdTransactionRecord transaction) => transaction.CompletedAt is null && transaction.ExpiresAt > clock.GetUtcNow();

    public async Task ProveAsync(string id, string? proof, CancellationToken cancellationToken)
    {
        var current = id.Length == AgentIdTransactionRecord.IdLength ? await this.FindAsync(id, cancellationToken).ConfigureAwait(false) : null;
        if (current is not { Value: var transaction } || !this.IsOpen(transaction))
        {
            throw new ApiException(StatusCodes.Status404NotFound, "expired_transaction", Expired);
        }

        var (verified, refusedAgent, reason) = proof is { Length: > 0 }
            ? await assertions.VerifyAsync(PlatformTenant.Id, proof, AgentIdIssuer.ProofUrl(options, id), cancellationToken).ConfigureAwait(false)
            : (null, null, "no proof");
        if (verified is null
            || !verified.Token.TryGetPayloadValue<string>(AgentIdIssuer.ProofTransactionClaim, out var bound) || bound != id
            || !verified.Token.TryGetPayloadValue<string>(AgentIdIssuer.ProofClientClaim, out var client) || client != transaction.ClientId)
        {
            throw Refused(verified?.Agent.Value.Id ?? refusedAgent, verified is null ? reason : "transaction or client_id not bound");
        }

        var agent = verified.Agent.Value;
        if (transaction.ProvedAt is not null)
        {
            throw Refused(agent.Id, "transaction already proved");
        }

        if ((await limiter.AcquireAsync(AgentIdIssuer.Slug, "agentid-proof-agent", agent.Id, ProofsPerAgentMinute, Window, cancellationToken).ConfigureAwait(false)
            ?? await limiter.AcquireAsync(AgentIdIssuer.Slug, "agentid-proof-client", limiter.Fold(transaction.ClientId), RequestsPerClientMinute, Window, cancellationToken).ConfigureAwait(false))
            is { } wait)
        {
            throw ApiException.RateLimited("Too many proofs for this agent; retry later.", wait);
        }

        var proved = transaction with { AgentId = agent.Id, OwnerUserId = agent.OwnerUserId, Kid = verified.Key.Value.Kid, ProvedAt = clock.GetUtcNow() };
        if (!await data.SaveAsync([verified.Replay, Change.Update(current, proved), Change.Assert(verified.Key), Change.Assert(verified.Owner)], cancellationToken).ConfigureAwait(false))
        {
            throw Refused(agent.Id, "proof replayed, or the transaction, key or owner changed");
        }

        ApiException Refused(string? agentId, string why)
        {
            logger.ZLogInformation($"Agent sign-in proof refused for transaction {id} and agent {agentId ?? "unknown"}: {why}");
            return new ApiException(StatusCodes.Status401Unauthorized, "invalid_proof",
                "Sign an ES256 JWT with an active key of an active agent: iss and sub the agent id, aud this URL, txn this transaction, client_id the app's client_id, a jti, iat, and exp at most 300 seconds after iat.");
        }
    }

    public async Task<AgentIdSignIn?> CompleteAsync(AgentIdTransactionRecord transaction, CancellationToken cancellationToken)
    {
        if (transaction is not { AgentId: { } agentId, OwnerUserId: { } ownerId, Kid: { } kid, ProvedAt: not null } || !this.IsOpen(transaction))
        {
            return null;
        }

        var tenant = PlatformTenant.Id;
        using var home = HomeReads.Begin();
        var (current, key, agent, owner) = await data.ReadAsync(async (db, token) => (
            await db.AgentIdTransactions.Where(row => row.Id == transaction.Id).WithVersion().SingleOrDefaultAsync(token).ConfigureAwait(false),
            await db.AgentKeys.Where(row => row.ProjectId == tenant && row.Kid == kid && row.AgentId == agentId).WithVersion().SingleOrDefaultAsync(token).ConfigureAwait(false),
            await db.Agents.Where(row => row.ProjectId == tenant && row.Id == agentId && row.OwnerUserId == ownerId).WithVersion().SingleOrDefaultAsync(token).ConfigureAwait(false),
            await db.Users.Where(row => row.InstanceId == tenant && row.Id == ownerId).WithVersion().SingleOrDefaultAsync(token).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (current is null || current.Value != transaction || key is null || agent is null || owner is null || !AgentAssertions.Active(key.Value, agent.Value, owner.Value, now))
        {
            logger.ZLogInformation($"Agent sign-in refused at completion for transaction {transaction.Id} and agent {agentId}: transaction changed, or agent, key or owner not active");
            return null;
        }

        return await data.SaveAsync([Change.Update(current, transaction with { CompletedAt = now }), Change.Assert(key), Change.Assert(owner)], cancellationToken).ConfigureAwait(false)
            ? new(agent.Value, owner.Value)
            : null;
    }

    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return await data.InBatchesAsync(batchSize, (db, token) => db.AgentIdTransactions.Where(row => row.ExpiresAt <= now)
            .OrderBy(row => row.ExpiresAt).Take(batchSize).ExecuteDeleteAsync(token), cancellationToken).ConfigureAwait(false);
    }

    private Task<Versioned<AgentIdTransactionRecord>?> FindAsync(string id, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.AgentIdTransactions.Where(row => row.Id == id).WithVersion().SingleOrDefaultAsync(token), cancellationToken);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(AgentIdProofRequest))]
[JsonSerializable(typeof(AgentIdPendingBody))]
[JsonSerializable(typeof(AgentIdUserInfoBody))]
internal sealed partial class AgentIdJson : JsonSerializerContext;
