using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Storage;
using ZLogger;

namespace TerraScale.Authentication.Features.Agents;

public static class AgentStatus
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}

public sealed record AgentRecord(string ProjectId, string Id, string OwnerUserId, string Name, string Status, IReadOnlyList<string> AllowedScopes,
    bool AllowSelfSubject, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt, long OwnerAuthEpoch, string OwnerAuthStamp, bool ShareOwnerEmail = false)
{
    public string? Version { get; init; }
}

public sealed record AgentKeyRecord(string ProjectId, string Kid, string AgentId, string PublicJwk, string Thumbprint, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt)
{
    public string? Version { get; init; }
}

public sealed record AgentPolicyRecord(string ProjectId, IReadOnlyList<string> AllowedScopes)
{
    public string? Version { get; init; }
}

public sealed record AgentOwner(string Id, long AuthEpoch, string? AuthStamp, bool Disabled);

public sealed record AgentPolicyBody(IReadOnlyList<string> AllowedScopes);
public sealed record CreateAgentRequest(string? Name, IReadOnlyList<string>? AllowedScopes, JsonElement? PublicJwk, bool AllowSelfSubject = false, DateTimeOffset? ExpiresAt = null);
public sealed record AddAgentKeyRequest(JsonElement? PublicJwk);
public sealed record AgentKeyBody(string Kid, string Thumbprint, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
public sealed record AgentBody(string Id, string ProjectId, string OwnerUserId, string Name, string Status, IReadOnlyList<string> AllowedScopes, bool AllowSelfSubject,
    IReadOnlyList<AgentKeyBody> Keys, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt, bool ShareOwnerEmail);
public sealed record AgentSharingRequest(bool? ShareOwnerEmail);
public sealed record AgentPageBody(IReadOnlyList<AgentBody> Items, string? Next);

public readonly union GetAgentResult(AgentBody, AuthNotFound);

public readonly union ShareOwnerEmailResult(AgentBody, AuthNotFound);

public sealed class AgentDirectory(AuthData data, PlatformDirectory platform, AgentLifecycleEvents events, AttemptLimiter limiter, TimeProvider clock, ILogger<AgentDirectory> logger)
{
    public static readonly IReadOnlyList<string> PlatformScopes = ["mcp:read", "mcp:write"];

    public const int MaxLiveAgentsPerUser = 25;

    public const int MaxActiveKeysPerAgent = 2;

    public async Task<AgentPolicyBody> PolicyAsync(string project, CancellationToken cancellationToken) =>
        !Tenancy.IsProject(project) ? new(PlatformScopes) : new((await this.PolicyRowAsync(project, cancellationToken))?.Value.AllowedScopes ?? []);

    private async Task<Versioned<AgentPolicyRecord>?> PolicyRowAsync(string project, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AgentPolicyRecord>(
            "SELECT * FROM agent_policies WHERE project_id = @project", new { project }), cancellationToken));

    private async Task<Versioned<UserRecord>?> UserAsync(string project, string userId, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadUserAsync(connection => connection.QuerySingleOrDefaultAsync<UserRecord>(
            "SELECT * FROM users WHERE instance_id = @project AND id = @userId", new { project, userId }), cancellationToken));

    private async Task<Versioned<AgentRecord>?> AgentAsync(string project, string agentId, string? owner, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AgentRecord>(
            "SELECT * FROM agents WHERE project_id = @project AND id = @agentId AND (@owner IS NULL OR owner_user_id = @owner)", new { project, agentId, owner }), cancellationToken));

    public async Task<AgentPolicyBody> SetPolicyAsync(string project, IReadOnlyList<string> scopes, string actor, CancellationToken cancellationToken)
    {
        if (scopes is null || scopes.Count != 0 && !MachineClients.ValidScopes(scopes))
        {
            throw new ApiException(400, "invalid_request", "allowed_scopes must be empty or 1 to 64 unique scopes.");
        }

        var current = await this.PolicyRowAsync(project, cancellationToken);
        var policy = new AgentPolicyRecord(project, scopes);
        await this.SaveAsync(project, actor, "agent.policy_updated", project, null,
            [current is null ? Change.Insert(policy) : Change.Update(current, policy)], cancellationToken);
        return new(scopes);
    }

    public async Task<AgentBody> CreateAsync(string project, string userId, string actor, CreateAgentRequest request, Func<AgentKeyRecord, IReadOnlyList<Change>> bound,
        DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(bound);
        var now = clock.GetUtcNow();
        var name = PlatformDirectory.Name(request.Name);
        var policy = Tenancy.IsProject(project)
            ? await this.PolicyRowAsync(project, cancellationToken)
            : null;
        var allowed = Tenancy.IsProject(project) ? policy?.Value.AllowedScopes : PlatformScopes;
        if (name is not { Length: <= 64 } || request.AllowedScopes is not { } scopes || !MachineClients.ValidScopes(scopes) || allowed is null
            || scopes.Except(allowed, StringComparer.Ordinal).Any() || request.ExpiresAt <= now)
        {
            throw new ApiException(400, "invalid_request", "Supply a name of 1 to 64 characters, 1 to 64 unique scopes from the project's agent policy, a public_jwk and an optional future expiry.");
        }

        var key = AgentJwk.Parse(request.PublicJwk);
        var user = await this.UserAsync(project, userId, cancellationToken);
        if (user is null || user.Value.Disabled)
        {
            throw PlatformDirectory.NotFound();
        }

        var stamp = user.Value.AuthStamp ?? throw new InvalidDataException("The user has no authentication stamp.");
        var (active, epoch) = (AgentStatus.Active, user.Value.AuthEpoch);
        var live = await data.ReadAsync(connection => connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM agents WHERE project_id = @project AND owner_user_id = @userId AND status = @active
                AND (expires_at IS NULL OR expires_at > @now) AND owner_auth_epoch = @epoch AND owner_auth_stamp = @stamp
            """, new { project, userId, active, now, epoch, stamp }), cancellationToken);
        if (live >= MaxLiveAgentsPerUser)
        {
            throw new ApiException(409, "agent_limit", $"A user can have at most {MaxLiveAgentsPerUser} live agents.");
        }

        if (await limiter.AcquireAsync(project, "agent-create", userId, 100, TimeSpan.FromHours(1), cancellationToken) is { } wait)
        {
            throw ApiException.RateLimited("Too many agents created.", wait);
        }

        var agent = new AgentRecord(project, AuthIds.New(), userId, name, AgentStatus.Active, scopes, request.AllowSelfSubject, now, request.ExpiresAt, null, null, user.Value.AuthEpoch, stamp);
        var stored = new AgentKeyRecord(project, AuthIds.New(), agent.Id, key.Jwk, key.Thumbprint, now, null);
        await this.SaveAsync(project, actor, "agent.created", agent.Id, key.Thumbprint,
            [Change.Insert(agent), Change.Insert(stored), .. policy is null ? [] : new[] { Change.Assert(policy) }, Change.Update(user, user.Value),
                .. Event(project, () => events.Created(project, userId, agent.Id, stored.Kid)), .. bound(stored)], cancellationToken, deadline);
        return Body(agent, [stored], new(user.Value.Id, user.Value.AuthEpoch, stamp, false));
    }

    public async Task<GetAgentResult> GetAsync(string project, string agentId, CancellationToken cancellationToken)
    {
        var agent = (await this.AgentAsync(project, agentId, null, cancellationToken))?.Value;
        return agent is null
            ? new AuthNotFound("No such resource.")
            : (await this.BodiesAsync(project, [agent], cancellationToken))[0];
    }

    public async Task<AgentPageBody> ListAsync(string project, string? owner, int limit, string? after, CancellationToken cancellationToken)
    {
        var from = PlatformDirectory.Cursor(after);
        var take = limit + 1;
        var rows = (await data.ReadAsync(connection => connection.QueryAsync<AgentRecord>(
            "SELECT * FROM agents WHERE project_id = @project AND (@owner IS NULL OR owner_user_id = @owner) AND id > @from ORDER BY id LIMIT @take",
            new { project, owner, from, take }), cancellationToken)).AsList();
        var page = PlatformDirectory.Paged(rows, limit, row => row.Id);
        return new(await this.BodiesAsync(project, page.Items, cancellationToken), page.Next);
    }

    public async Task<AgentKeyBody> AddKeyAsync(string project, string agentId, string actor, AddAgentKeyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = AgentJwk.Parse(request.PublicJwk);
        var agent = await this.AgentAsync(project, agentId, null, cancellationToken) ?? throw PlatformDirectory.NotFound();
        var owner = await this.UserAsync(project, agent.Value.OwnerUserId, cancellationToken);
        var now = clock.GetUtcNow();
        if (owner is null || Status(agent.Value, Owner(owner.Value)) != AgentStatus.Active || agent.Value.ExpiresAt <= now)
        {
            throw new ApiException(409, "agent_inactive", "The agent is revoked or expired, or its owner signed out everywhere or was disabled.");
        }

        var active = await data.ReadAsync(connection => connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM agent_keys WHERE project_id = @project AND agent_id = @agentId AND revoked_at IS NULL", new { project, agentId }), cancellationToken);
        if (active >= MaxActiveKeysPerAgent)
        {
            throw new ApiException(409, "agent_key_limit", $"An agent can have at most {MaxActiveKeysPerAgent} active keys; revoke one first.");
        }

        var stored = new AgentKeyRecord(project, AuthIds.New(), agentId, key.Jwk, key.Thumbprint, now, null);
        await this.SaveAsync(project, actor, "agent.key_added", stored.Kid, key.Thumbprint,
            [Change.Insert(stored), Change.Update(agent, agent.Value), Change.Assert(owner),
                .. Event(project, () => events.KeyAdded(project, agent.Value.OwnerUserId, agentId, stored.Kid))], cancellationToken);
        return KeyBody(stored);
    }

    public async Task RevokeKeyAsync(string project, string agentId, string kid, string actor, CancellationToken cancellationToken)
    {
        var agent = await this.AgentAsync(project, agentId, null, cancellationToken);
        var key = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AgentKeyRecord>(
            "SELECT * FROM agent_keys WHERE project_id = @project AND agent_id = @agentId AND kid = @kid", new { project, agentId, kid }), cancellationToken));
        if (agent is null || key is null)
        {
            throw PlatformDirectory.NotFound();
        }

        if (key.Value.RevokedAt is not null)
        {
            return;
        }

        await this.SaveAsync(project, actor, "agent.key_revoked", kid, null,
            [Change.Update(key, key.Value with { RevokedAt = clock.GetUtcNow() }), Change.Update(agent, agent.Value),
                .. Event(project, () => events.KeyRevoked(project, agent.Value.OwnerUserId, agentId, kid))], cancellationToken);
    }

    public async Task RevokeAsync(string project, string agentId, string? owner, string actor, CancellationToken cancellationToken)
    {
        var agent = await this.AgentAsync(project, agentId, owner, cancellationToken);
        var keys = Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<AgentKeyRecord>(
            "SELECT * FROM agent_keys WHERE project_id = @project AND agent_id = @agentId AND revoked_at IS NULL", new { project, agentId }), cancellationToken));
        if (agent is null)
        {
            throw PlatformDirectory.NotFound();
        }

        if (agent.Value.Status == AgentStatus.Revoked)
        {
            return;
        }

        var now = clock.GetUtcNow();
        await this.SaveAsync(project, actor, "agent.revoked", agentId, null,
            [Change.Update(agent, agent.Value with { Status = AgentStatus.Revoked, RevokedAt = now }),
                .. keys.Select(key => Change.Update(key, key.Value with { RevokedAt = now })),
                .. Event(project, () => events.Revoked(project, agent.Value.OwnerUserId, agentId))], cancellationToken);
    }

    public async Task<ShareOwnerEmailResult> ShareOwnerEmailAsync(string project, string agentId, string owner, bool share, CancellationToken cancellationToken)
    {
        var agent = await this.AgentAsync(project, agentId, owner, cancellationToken);
        if (agent is null)
        {
            return new AuthNotFound("No such resource.");
        }

        var shared = agent.Value with { ShareOwnerEmail = share };
        if (agent.Value.ShareOwnerEmail != share)
        {
            await this.SaveAsync(project, owner, share ? "agent.owner_email_shared" : "agent.owner_email_unshared", agentId, null, [Change.Update(agent, shared)], cancellationToken)
                ;
        }

        return (await this.BodiesAsync(project, [shared], cancellationToken))[0];
    }

    public static string Status(AgentRecord agent, AgentOwner? owner)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return agent.Status == AgentStatus.Active && owner is { Disabled: false } && owner.AuthEpoch == agent.OwnerAuthEpoch && owner.AuthStamp == agent.OwnerAuthStamp
            ? AgentStatus.Active : AgentStatus.Revoked;
    }

    private static IEnumerable<Change> Event(string project, Func<Change> lifecycle) => Tenancy.IsProject(project) ? [lifecycle()] : [];

    private static AgentOwner Owner(UserRecord user) => new(user.Id, user.AuthEpoch, user.AuthStamp, user.Disabled);

    private async Task<IReadOnlyList<AgentBody>> BodiesAsync(string project, IReadOnlyList<AgentRecord> agents, CancellationToken cancellationToken)
    {
        if (agents.Count == 0)
        {
            return [];
        }

        string[] ids = [.. agents.Select(agent => agent.Id)];
        string[] owners = [.. agents.Select(agent => agent.OwnerUserId).Distinct(StringComparer.Ordinal)];
        var keys = await data.ReadAsync(connection => connection.QueryAsync<AgentKeyRecord>(
            "SELECT * FROM agent_keys WHERE project_id = @project AND agent_id IN @ids ORDER BY kid", new { project, ids }), cancellationToken);
        var users = await data.ReadAsync(connection => connection.QueryAsync<AgentOwner>(
            "SELECT id, auth_epoch, auth_stamp, disabled FROM users WHERE instance_id = @project AND id IN @owners", new { project, owners }), cancellationToken);
        var byAgent = keys.ToLookup(key => key.AgentId, StringComparer.Ordinal);
        var byId = users.ToDictionary(user => user.Id, StringComparer.Ordinal);
        return [.. agents.Select(agent => Body(agent, byAgent[agent.Id], byId.GetValueOrDefault(agent.OwnerUserId)))];
    }

    private static AgentBody Body(AgentRecord agent, IEnumerable<AgentKeyRecord> keys, AgentOwner? owner) =>
        new(agent.Id, agent.ProjectId, agent.OwnerUserId, agent.Name, Status(agent, owner), agent.AllowedScopes, agent.AllowSelfSubject,
            [.. keys.Select(KeyBody)], agent.CreatedAt, agent.ExpiresAt, agent.RevokedAt, agent.LastUsedAt, agent.ShareOwnerEmail);

    private static AgentKeyBody KeyBody(AgentKeyRecord key) => new(key.Kid, key.Thumbprint, key.CreatedAt, key.RevokedAt);

    private async Task SaveAsync(string project, string actor, string action, string target, string? thumbprint, IReadOnlyList<Change> changes, CancellationToken cancellationToken,
        DateTimeOffset? deadline = null)
    {
        var organization = Tenancy.IsProject(project)
            ? (await platform.ProjectAsync(project, cancellationToken) ?? throw PlatformDirectory.NotFound()).Value.OrganizationId
            : null;
        if (deadline <= clock.GetUtcNow())
        {
            throw new ApiException(409, "approval_expired", "The approval deadline passed before the agent could be stored.");
        }

        if (await data.SaveAsync([.. changes, .. organization is null ? [] : new[] { platform.Audit(organization, actor, action, target) }], cancellationToken))
        {
            if (organization is null)
            {
                logger.ZLogInformation($"Platform agent audit: {action} {target} by {actor}");
            }

            return;
        }

        if (thumbprint is not null && await data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM agent_keys WHERE project_id = @project AND thumbprint = @thumbprint)", new { project, thumbprint }), cancellationToken))
        {
            throw new ApiException(409, "agent_key_in_use", "This public key is already registered in the project; generate a new key pair.");
        }

        throw new ApiException(409, "version_conflict", "The agent, its owner or the agent policy changed concurrently.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(AgentPolicyBody))]
[JsonSerializable(typeof(CreateAgentRequest))]
[JsonSerializable(typeof(AddAgentKeyRequest))]
[JsonSerializable(typeof(AgentKeyBody))]
[JsonSerializable(typeof(AgentBody))]
[JsonSerializable(typeof(AgentPageBody))]
[JsonSerializable(typeof(AgentSharingRequest))]
[JsonSerializable(typeof(AgentTokenBody))]
internal sealed partial class AgentJson : JsonSerializerContext;
