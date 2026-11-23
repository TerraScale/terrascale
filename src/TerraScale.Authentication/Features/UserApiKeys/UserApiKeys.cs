using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.UserApiKeys;

public sealed record UserApiKeyRecord(string ProjectId, string Id, string UserId, string Name, string Prefix, string SecretHash,
    IReadOnlyList<string> Scopes, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, long AuthEpoch, string AuthStamp)
{
    public string? Version { get; init; }
}

public sealed record UserApiKeyAuthorization(Change Session, long AuthEpoch, string AuthStamp, DateTimeOffset Until);
public sealed record UserApiKeyPolicyRecord(string ProjectId, IReadOnlyList<string> AllowedScopes)
{
    public string? Version { get; init; }
}

public sealed record UserApiKeyRequest(string? Name, IReadOnlyList<string>? Scopes, DateTimeOffset? ExpiresAt = null);
public sealed record UserApiKeyPolicyBody(IReadOnlyList<string> AllowedScopes);
public sealed record VerifyUserApiKeyRequest(string? Key)
{
    public override string ToString() => nameof(VerifyUserApiKeyRequest);
}

public sealed record UserApiKeyBody(string Id, string UserId, string Name, string Prefix, IReadOnlyList<string> Scopes, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null)
{
    public override string ToString() => nameof(UserApiKeyBody);
}

public sealed record UserApiKeyPageBody(IReadOnlyList<UserApiKeyBody> Items);
public sealed record VerifyUserApiKeyBody(bool Valid, string? UserId = null, IReadOnlyList<string>? Scopes = null, string? KeyId = null, DateTimeOffset? ExpiresAt = null);

public sealed class UserApiKeys(AuthData data, PlatformDirectory platform, WebhookOutbox outbox, AttemptLimiter limiter, TimeProvider clock)
{
    public static UserApiKeyBody Body(UserApiKeyRecord key, string? secret = null) =>
        new(key.Id, key.UserId, key.Name, key.Prefix, key.Scopes, key.CreatedAt, key.ExpiresAt, secret);

    public async Task<UserApiKeyPolicyBody> PolicyAsync(string project, CancellationToken cancellationToken) =>
        new((await this.PolicyRowAsync(project, cancellationToken))?.Value.AllowedScopes ?? []);

    private async Task<Versioned<UserApiKeyPolicyRecord>?> PolicyRowAsync(string project, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<UserApiKeyPolicyRecord>(
            "SELECT * FROM user_api_key_policies WHERE project_id = @project", new { project }), cancellationToken));

    private Task<bool> OwnerLiveAsync(string project, UserApiKeyRecord key, CancellationToken cancellationToken)
    {
        var (userId, epoch, stamp) = (key.UserId, key.AuthEpoch, key.AuthStamp);
        return data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM users WHERE instance_id = @project AND id = @userId AND disabled = 0 AND auth_epoch = @epoch AND auth_stamp IS @stamp)",
            new { project, userId, epoch, stamp }), cancellationToken);
    }

    public async Task<UserApiKeyPolicyBody> SetPolicyAsync(string project, IReadOnlyList<string> scopes, string actor, CancellationToken cancellationToken)
    {
        if (scopes is null || scopes.Count != 0 && !MachineClients.ValidScopes(scopes))
        {
            throw Invalid();
        }

        var current = await this.PolicyRowAsync(project, cancellationToken);
        var policy = new UserApiKeyPolicyRecord(project, scopes);
        await this.SaveAsync(project, actor, "apiKey.policy_updated", project,
            [current is null ? Change.Insert(policy) : Change.Update(current, policy)], cancellationToken);
        return new(scopes);
    }

    public async Task<UserApiKeyBody> CreateAsync(string project, string userId, string actor, UserApiKeyRequest request, UserApiKeyAuthorization? authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = PlatformDirectory.Name(request.Name);
        var policy = await this.PolicyRowAsync(project, cancellationToken);
        if (name is null || request.Scopes is not { } scopes || !MachineClients.ValidScopes(scopes) || policy is null
            || scopes.Except(policy.Value.AllowedScopes, StringComparer.Ordinal).Any() || request.ExpiresAt <= clock.GetUtcNow())
        {
            throw Invalid();
        }

        var user = Versioned.Of(await data.ReadUserAsync(connection => connection.QuerySingleOrDefaultAsync<UserRecord>(
            "SELECT * FROM users WHERE instance_id = @project AND id = @userId", new { project, userId }), cancellationToken));
        if (user is null || user.Value.Disabled)
        {
            throw PlatformDirectory.NotFound();
        }

        if (authorization is not null && (authorization.AuthEpoch != user.Value.AuthEpoch || authorization.AuthStamp != user.Value.AuthStamp))
        {
            throw new ApiException(403, "reauthentication_required", "Sign in again to create an API key.");
        }

        var now = clock.GetUtcNow();
        var (epoch, stamp) = (user.Value.AuthEpoch, user.Value.AuthStamp);
        var live = await data.ReadAsync(connection => connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM user_api_keys WHERE project_id = @project AND user_id = @userId AND (expires_at IS NULL OR expires_at > @now)
                AND auth_epoch = @epoch AND auth_stamp IS @stamp
            """, new { project, userId, now, epoch, stamp }), cancellationToken);
        if (live >= 25)
        {
            throw new ApiException(409, "user_key_limit", "A user can have at most 25 live API keys.");
        }

        if (await limiter.AcquireAsync(project, "user-api-key-create", userId, 100, TimeSpan.FromHours(1), cancellationToken) is { } wait)
        {
            throw ApiException.RateLimited("Too many user keys created.", wait);
        }

        return await this.InsertAsync(project, actor, user, policy, name, scopes, request.ExpiresAt, authorization, false, cancellationToken);
    }

    private async Task<UserApiKeyBody> InsertAsync(string project, string actor, Versioned<UserRecord> user, Versioned<UserApiKeyPolicyRecord> policy,
        string name, IReadOnlyList<string> scopes, DateTimeOffset? expiresAt, UserApiKeyAuthorization? authorization, bool retried, CancellationToken cancellationToken)
    {
        var prefix = "tsk_" + Tokens.RandomToken(8);
        var secret = prefix + "_" + Tokens.RandomToken(43);
        var key = new UserApiKeyRecord(project, AuthIds.New(), user.Value.Id, name, prefix, Tokens.Sha256Hex(secret), scopes,
            clock.GetUtcNow(), expiresAt, user.Value.AuthEpoch, user.Value.AuthStamp ?? throw new InvalidDataException("The user has no authentication stamp."));
        List<Change> changes = [Change.Insert(key), Change.Assert(policy), Change.Update(user, user.Value),
            Change.Insert(outbox.NewEvent(project, "apiKey.created", user.Value.Id))];
        if (authorization is not null)
        {
            if (authorization.Until <= clock.GetUtcNow())
            {
                throw new ApiException(403, "reauthentication_required", "Sign in again to create an API key.");
            }

            changes.Add(authorization.Session);
        }

        if (await this.TrySaveAsync(project, actor, "apiKey.created", key.Id, changes, cancellationToken))
        {
            return Body(key, secret);
        }

        if (!retried && await data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM user_api_keys WHERE project_id = @project AND prefix = @prefix)", new { project, prefix }), cancellationToken))
        {
            return await this.InsertAsync(project, actor, user, policy, name, scopes, expiresAt, authorization, true, cancellationToken);
        }

        throw Conflict();
    }

    public async Task<UserApiKeyPageBody> ListAsync(string project, string userId, CancellationToken cancellationToken)
    {
        var rows = await data.ReadAsync(connection => connection.QueryAsync<UserApiKeyRecord>(
            "SELECT * FROM user_api_keys WHERE project_id = @project AND user_id = @userId ORDER BY id", new { project, userId }), cancellationToken);
        return new([.. rows.Select(row => Body(row))]);
    }

    public async Task RevokeAsync(string project, string userId, string id, string actor, CancellationToken cancellationToken)
    {
        var key = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<UserApiKeyRecord>(
            "SELECT * FROM user_api_keys WHERE project_id = @project AND user_id = @userId AND id = @id", new { project, userId, id }), cancellationToken))
            ?? throw PlatformDirectory.NotFound();
        await this.SaveAsync(project, actor, "apiKey.revoked", id,
            [Change.Delete(key), Change.Insert(outbox.NewEvent(project, "apiKey.revoked", userId))], cancellationToken);
    }

    public async Task<VerifyUserApiKeyBody> VerifyAsync(string project, string? secret, CancellationToken cancellationToken)
    {
        if (await limiter.AcquireAsync(project, "user-api-key-verify", project, 1000, TimeSpan.FromMinutes(1), cancellationToken) is { } wait)
        {
            throw ApiException.RateLimited("Too many user key verifications.", wait);
        }

        if (secret is not { Length: 56 } || !secret.StartsWith("tsk_", StringComparison.Ordinal) || secret[12] != '_')
        {
            return new(false);
        }

        var prefix = secret[..12];
        var key = await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<UserApiKeyRecord>(
            "SELECT * FROM user_api_keys WHERE project_id = @project AND prefix = @prefix", new { project, prefix }), cancellationToken);
        var equal = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Tokens.Sha256Hex(secret)),
            Encoding.ASCII.GetBytes(key?.SecretHash ?? new string('0', 64)));
        if (!equal || key is null || key.ExpiresAt <= clock.GetUtcNow()
            || !await this.OwnerLiveAsync(project, key, cancellationToken))
        {
            return new(false);
        }

        return new(true, key.UserId, key.Scopes, key.Id, key.ExpiresAt);
    }

    private async Task SaveAsync(string project, string actor, string action, string target, IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        if (!await this.TrySaveAsync(project, actor, action, target, changes, cancellationToken))
        {
            throw Conflict();
        }
    }

    private async Task<bool> TrySaveAsync(string project, string actor, string action, string target, IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        var owner = await platform.ProjectAsync(project, cancellationToken) ?? throw PlatformDirectory.NotFound();
        return await data.SaveAsync([.. changes, platform.Audit(owner.Value.OrganizationId, actor, action, target)], cancellationToken);
    }

    private static ApiException Conflict() => new(409, "version_conflict", "The user or key policy changed concurrently.");

    private static ApiException Invalid() => new(400, "invalid_request", "Supply a name, 1 to 64 unique scopes from the project's user key policy, and an optional future expiry.");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(UserApiKeyRequest))]
[JsonSerializable(typeof(UserApiKeyBody))]
[JsonSerializable(typeof(UserApiKeyPageBody))]
[JsonSerializable(typeof(UserApiKeyPolicyBody))]
[JsonSerializable(typeof(VerifyUserApiKeyRequest))]
[JsonSerializable(typeof(VerifyUserApiKeyBody))]
internal sealed partial class UserApiKeyJson : JsonSerializerContext;
