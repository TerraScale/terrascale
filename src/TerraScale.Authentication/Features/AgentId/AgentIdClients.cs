using System.Globalization;
using Dapper;
using System.Text.Json.Serialization;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;
using ZLogger;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.AgentId;

public sealed record AgentIdClientRecord(string Id, string OwnerUserId, string Name, IReadOnlyList<string> RedirectUris, string SecretDigest, DateTimeOffset CreatedAt,
    string? PreviousSecretDigest = null, DateTimeOffset? PreviousSecretExpiresAt = null)
{
    public string? Version { get; init; }

    public const int DigestLength = 64;

    public const int MaxRedirectUriLength = 2048;
}

public sealed record AgentIdRegistration(Versioned<AgentIdClientRecord> Client, Versioned<UserRecord> Owner, OidcApplication Application)
{
    public IReadOnlyList<Change> Asserts => [Change.Assert(this.Client), Change.Assert(this.Owner)];

    public readonly record struct RequestCopy(string ClientId);
}

public sealed record AgentIdOwnerEmail(bool Shared, string Email, bool EmailVerified);

public sealed record CreateAgentIdClientRequest(string? Name, IReadOnlyList<string>? RedirectUris);

public sealed record RotateAgentIdClientRequest(int? GraceSeconds);

public sealed record AgentIdClientBody(string ClientId, string Name, IReadOnlyList<string> RedirectUris, DateTimeOffset CreatedAt,
    DateTimeOffset? PreviousSecretExpiresAt = null, string? ClientSecret = null);

public sealed record AgentIdClientPageBody(IReadOnlyList<AgentIdClientBody> Items);

public readonly union CreateAgentIdClientResult(AgentIdClientBody, NotFound, Conflict, Forbidden);

public readonly union RotateAgentIdClientResult(AgentIdClientSecretBody, NotFound, Conflict, Forbidden);

public readonly union DeleteAgentIdClientResult(Done, NotFound, Conflict, Forbidden);

public sealed record AgentIdClientSecretBody(string ClientId, string ClientSecret, DateTimeOffset? PreviousSecretExpiresAt);

public readonly union OwnerCheck(UserRecord, Forbidden);

public readonly union StepUpSave(Done, Forbidden, Conflict);

public sealed class AgentIdClients(AuthData data, TimeProvider clock, ILogger<AgentIdClients> logger)
{
    public const int MaxPerAccount = 10;

    public const int MaxRedirectUris = 10;

    public static readonly TimeSpan DefaultGrace = TimeSpan.FromHours(1);

    public static readonly TimeSpan MaxGrace = TimeSpan.FromDays(1);

    internal async Task<CreateAgentIdClientResult> CreateAsync(PreparedStepUp proof, string name, IReadOnlyList<string> redirectUris, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var owner = proof.Caller;
        var user = await this.UserAsync(owner.InstanceId, owner.Id, cancellationToken);
        if (user is null)
        {
            return new NotFound("No such resource.");
        }

        var ownerId = owner.Id;
        var count = await data.ReadAsync(connection => connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM agentid_clients WHERE owner_user_id = @ownerId", new { ownerId }), cancellationToken);
        if (count >= MaxPerAccount)
        {
            return new Conflict($"An account can have at most {MaxPerAccount} Sign in as agent clients; delete one first.", "client_limit");
        }

        var secret = Tokens.RandomToken(48);
        var client = new AgentIdClientRecord(AuthIds.New(), owner.Id, name, redirectUris, Tokens.Sha256Hex(secret), clock.GetUtcNow());
        return (await this.TrySaveAsync(proof, [Change.Insert(client), Change.Update(user, user.Value)], cancellationToken)) switch
        {
            Done => this.Created(client, secret, owner.Id),
            Conflict conflict => conflict,
            Forbidden forbidden => forbidden,
        };
    }

    private AgentIdClientBody Created(AgentIdClientRecord client, string secret, string owner)
    {
        logger.ZLogInformation($"Sign in as agent client {client.Id} created by {owner}");
        return Body(client) with { ClientSecret = secret };
    }

    public async Task<AgentIdClientPageBody> ListAsync(string ownerId, CancellationToken cancellationToken)
    {
        return new([.. (await data.ReadAsync(connection => connection.QueryAsync<AgentIdClientRecord>(
            "SELECT * FROM agentid_clients WHERE owner_user_id = @ownerId ORDER BY id", new { ownerId }), cancellationToken)).Select(Body)]);
    }

    internal async Task<RotateAgentIdClientResult> RotateAsync(PreparedStepUp proof, string clientId, TimeSpan grace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var owned = await this.OwnedAsync(proof.Caller.Id, clientId, cancellationToken);
        if (owned is null)
        {
            return new NotFound("No such resource.");
        }

        var client = owned;
        var secret = Tokens.RandomToken(48);
        var expires = grace > TimeSpan.Zero ? clock.GetUtcNow() + grace : (DateTimeOffset?)null;
        var rotated = client.Value with
        {
            SecretDigest = Tokens.Sha256Hex(secret),
            PreviousSecretDigest = expires is null ? null : client.Value.SecretDigest,
            PreviousSecretExpiresAt = expires,
        };
        return (await this.TrySaveAsync(proof, [Change.Update(client, rotated)], cancellationToken)) switch
        {
            Done => Rotated(clientId, secret, expires, proof.Caller.Id),
            Conflict conflict => conflict,
            Forbidden forbidden => forbidden,
        };
    }

    private AgentIdClientSecretBody Rotated(string clientId, string secret, DateTimeOffset? expires, string actor)
    {
        logger.ZLogInformation($"Sign in as agent client {clientId} secret rotated by {actor}");
        return new(clientId, secret, expires);
    }

    internal async Task<DeleteAgentIdClientResult> DeleteAsync(PreparedStepUp proof, string clientId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var owned = await this.OwnedAsync(proof.Caller.Id, clientId, cancellationToken);
        if (owned is null)
        {
            return new NotFound("No such resource.");
        }

        return (await this.TrySaveAsync(proof, [Change.Delete(owned)], cancellationToken)) switch
        {
            Done => Deleted(clientId, proof.Caller.Id),
            Conflict conflict => conflict,
            Forbidden forbidden => forbidden,
        };
    }

    private DeleteAgentIdClientResult Deleted(string clientId, string actor)
    {
        logger.ZLogInformation($"Sign in as agent client {clientId} deleted by {actor}");
        return new Done();
    }

    public async Task<AgentIdRegistration?> FindAsync(string clientId, CancellationToken cancellationToken)
    {
        var client = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AgentIdClientRecord>(
            "SELECT * FROM agentid_clients WHERE id = @clientId", new { clientId }), cancellationToken));
        var owner = client is null ? null : await this.UserAsync(PlatformTenant.Id, client.Value.OwnerUserId, cancellationToken);
        return client is not null && owner is { Value.Disabled: false } ? new(client, owner, Describe(client.Value, clock.GetUtcNow())) : null;
    }

    public async Task<AgentIdOwnerEmail?> OwnerEmailAsync(string clientId, string agentId, string ownerId, CancellationToken cancellationToken)
    {
        const string tenant = PlatformTenant.Id;
        return await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AgentIdOwnerEmail>(
            """
            SELECT a.share_owner_email AS shared, o.email, o.email_verified FROM agentid_clients c
            JOIN agents a ON a.project_id = @tenant AND a.id = @agentId AND a.owner_user_id = @ownerId
            JOIN users o ON o.instance_id = @tenant AND o.id = @ownerId
            WHERE c.id = @clientId AND EXISTS (SELECT 1 FROM users u WHERE u.instance_id = @tenant AND u.id = c.owner_user_id AND u.disabled = 0)
            """, new { tenant, agentId, ownerId, clientId }), cancellationToken);
    }

    public static OwnerCheck Owner(UserRecord caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller.ActingAgentId is null && caller.InstanceId == PlatformTenant.Id
            ? caller
            : new Forbidden("Only the account itself, not an agent acting for it, can do this.", "owner_required");
    }
    public static string? RedirectUri(string? value) =>
        value is { Length: > 0 and <= AgentIdClientRecord.MaxRedirectUriLength } && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
            && string.Equals(uri.AbsoluteUri, value, StringComparison.Ordinal)
            ? value
            : null;

    private static OidcApplication Describe(AgentIdClientRecord client, DateTimeOffset now) => new()
    {
        Id = client.Id,
        ClientId = client.Id,
        InstanceId = AgentIdIssuer.Slug,
        ClientType = ClientTypes.Confidential,
        ApplicationType = ApplicationTypes.Web,
        ConsentType = ConsentTypes.Implicit,
        DisplayName = client.Name,
        ClientSecret = ApiKeyApplicationManager.DigestPrefix + client.SecretDigest,
        PreviousSecretHash = client.PreviousSecretDigest is { } previous && client.PreviousSecretExpiresAt > now ? ApiKeyApplicationManager.DigestPrefix + previous : null,
        PreviousSecretExpiresAt = client.PreviousSecretExpiresAt,
        RedirectUris = client.RedirectUris,
        Permissions =
        [
            Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.GrantTypes.AuthorizationCode, Permissions.ResponseTypes.Code,
            Permissions.Prefixes.Scope + Scopes.Profile, Permissions.Prefixes.Scope + AgentIdIssuer.OwnerEmailScope,
        ],
        Version = string.Create(CultureInfo.InvariantCulture, $"agentid-client:{client.Id}"),
    };

    private async Task<Versioned<AgentIdClientRecord>?> OwnedAsync(string ownerId, string clientId, CancellationToken cancellationToken)
    {
        return Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AgentIdClientRecord>(
            "SELECT * FROM agentid_clients WHERE id = @clientId AND owner_user_id = @ownerId", new { clientId, ownerId }), cancellationToken));
    }

    private async Task<Versioned<UserRecord>?> UserAsync(string instance, string userId, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<UserRecord>(
            "SELECT * FROM users WHERE instance_id = @instance AND id = @userId", new { instance, userId }), cancellationToken));

    private async Task<StepUpSave> TrySaveAsync(PreparedStepUp proof, IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        if (proof.Record.ExpiresAt <= clock.GetUtcNow())
        {
            return new Forbidden("A fresh, approved, purpose-bound single-use proof is required.", "step_up_required");
        }

        return await data.SaveAsync([.. proof.Operations, .. changes], cancellationToken)
            ? new Done()
            : new Conflict("The client, the account or its step-up proof changed; try again.", "version_conflict");
    }

    private static AgentIdClientBody Body(AgentIdClientRecord client) =>
        new(client.Id, client.Name, client.RedirectUris, client.CreatedAt, client.PreviousSecretDigest is null ? null : client.PreviousSecretExpiresAt);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(CreateAgentIdClientRequest))]
[JsonSerializable(typeof(RotateAgentIdClientRequest))]
[JsonSerializable(typeof(AgentIdClientBody))]
[JsonSerializable(typeof(AgentIdClientPageBody))]
[JsonSerializable(typeof(AgentIdClientSecretBody))]
internal sealed partial class AgentIdClientJson : JsonSerializerContext;
