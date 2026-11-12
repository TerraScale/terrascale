using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.UserApiKeys;

public sealed record UserApiKeyRecord(string ProjectId, string Id, string UserId, string Name, string Prefix, string SecretHash,
    IReadOnlyList<string> Scopes, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);
public sealed record UserApiKeyPolicyRecord(string ProjectId, IReadOnlyList<string> AllowedScopes);
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
        new(await data.ReadAsync((db, token) => db.UserApiKeyPolicies.Where(row => row.ProjectId == project)
            .Select(row => row.AllowedScopes).SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false) ?? []);

    public async Task<UserApiKeyPolicyBody> SetPolicyAsync(string project, IReadOnlyList<string> scopes, string actor, CancellationToken cancellationToken)
    {
        if (scopes is null || scopes.Count != 0 && !MachineClients.ValidScopes(scopes))
        {
            throw Invalid();
        }

        var current = await data.ReadAsync((db, token) => db.UserApiKeyPolicies.Where(row => row.ProjectId == project).WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        var policy = new UserApiKeyPolicyRecord(project, scopes);
        await this.SaveAsync(project, actor, "apiKey.policy_updated", project,
            [current is null ? Change.Insert(policy) : Change.Update(current, policy)], cancellationToken).ConfigureAwait(false);
        return new(scopes);
    }

    public async Task<UserApiKeyBody> CreateAsync(string project, string userId, string actor, UserApiKeyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = PlatformDirectory.Name(request.Name);
        var policy = await data.ReadAsync((db, token) => db.UserApiKeyPolicies.Where(row => row.ProjectId == project).WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        if (name is null || request.Scopes is not { } scopes || !MachineClients.ValidScopes(scopes) || policy is null
            || scopes.Except(policy.Value.AllowedScopes, StringComparer.Ordinal).Any() || request.ExpiresAt <= clock.GetUtcNow())
        {
            throw Invalid();
        }

        var user = await data.ReadAsync((db, token) => db.Users.SingleOrDefaultAsync(row => row.InstanceId == project && row.Id == userId, token), cancellationToken).ConfigureAwait(false);
        if (user is null || user.Disabled)
        {
            throw PlatformDirectory.NotFound();
        }

        if (await limiter.AcquireAsync(project, "user-api-key-create", userId, 20, TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false) is { } wait)
        {
            throw ApiException.RateLimited("Too many user keys created.", wait);
        }

        var prefix = "tsk_" + Tokens.RandomToken(8);
        var secret = prefix + "_" + Tokens.RandomToken(43);
        var key = new UserApiKeyRecord(project, AuthIds.New(), userId, name, prefix, Tokens.Sha256Hex(secret), scopes, clock.GetUtcNow(), request.ExpiresAt);
        await this.SaveAsync(project, actor, "apiKey.created", key.Id,
            [Change.Insert(key), Change.Assert(policy), Change.Assert(user), Change.Insert(outbox.NewEvent(project, "apiKey.created", userId))], cancellationToken).ConfigureAwait(false);
        return Body(key, secret);
    }

    public async Task<UserApiKeyPageBody> ListAsync(string project, string userId, CancellationToken cancellationToken)
    {
        var rows = await data.ReadAsync((db, token) => db.UserApiKeys.Where(row => row.ProjectId == project && row.UserId == userId)
            .OrderBy(row => row.Id).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return new([.. rows.Select(row => Body(row))]);
    }

    public async Task RevokeAsync(string project, string userId, string id, string actor, CancellationToken cancellationToken)
    {
        var key = await data.ReadAsync((db, token) => db.UserApiKeys.Where(row => row.ProjectId == project && row.UserId == userId && row.Id == id)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        await this.SaveAsync(project, actor, "apiKey.revoked", id,
            [Change.Delete(key), Change.Insert(outbox.NewEvent(project, "apiKey.revoked", userId))], cancellationToken).ConfigureAwait(false);
    }

    public async Task<VerifyUserApiKeyBody> VerifyAsync(string project, string? secret, CancellationToken cancellationToken)
    {
        if (await limiter.AcquireAsync(project, "user-api-key-verify", project, 1000, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false) is { } wait)
        {
            throw ApiException.RateLimited("Too many user key verifications.", wait);
        }

        if (secret is not { Length: 56 } || !secret.StartsWith("tsk_", StringComparison.Ordinal) || secret[12] != '_')
        {
            return new(false);
        }

        var prefix = secret[..12];
        var key = await data.ReadAsync((db, token) => db.UserApiKeys.SingleOrDefaultAsync(row => row.ProjectId == project && row.Prefix == prefix, token), cancellationToken).ConfigureAwait(false);
        var equal = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Tokens.Sha256Hex(secret)),
            Encoding.ASCII.GetBytes(key?.SecretHash ?? new string('0', 64)));
        if (!equal || key is null || key.ExpiresAt <= clock.GetUtcNow()
            || !await data.ReadAsync((db, token) => db.Users.AnyAsync(row => row.InstanceId == project && row.Id == key.UserId && !row.Disabled, token), cancellationToken).ConfigureAwait(false))
        {
            return new(false);
        }

        return new(true, key.UserId, key.Scopes, key.Id, key.ExpiresAt);
    }

    private async Task SaveAsync(string project, string actor, string action, string target, IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        var owner = await platform.ProjectAsync(project, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        if (!await data.SaveAsync([.. changes, platform.Audit(owner.Value.OrganizationId, actor, action, target)], cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(409, "version_conflict", "The user or key policy changed concurrently.");
        }
    }

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
