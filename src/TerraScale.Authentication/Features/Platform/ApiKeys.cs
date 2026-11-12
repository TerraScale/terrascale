using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Security;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;
using ZLogger;

namespace TerraScale.Authentication.Features.Platform;

public static class TerraBaseScopes
{
    public const string DatabasesRead = "databases:read";

    public const string DatabasesWrite = "databases:write";

    public const string SqlRead = "sql:read";

    public const string SqlWrite = "sql:write";

    public const string AuthRead = "auth:read";

    public const string AuthWrite = "auth:write";

    public static IReadOnlyList<string> All { get; } = [DatabasesRead, DatabasesWrite, SqlRead, SqlWrite, AuthRead, AuthWrite];

    public static IReadOnlyList<string> For(string? projectRole) => projectRole switch
    {
        Roles.Viewer => [DatabasesRead, SqlRead],
        Roles.Member => [DatabasesRead, DatabasesWrite, SqlRead, SqlWrite, AuthRead],
        Roles.Admin => All,
        _ => [],
    };
}

public sealed partial class ApiKeys(AuthData data, Credentials credentials, StepUpProofs proofs, PlatformDirectory directory, AttemptLimiter limiter, TimeProvider clock, ILogger<ApiKeys> logger)
{
    public static readonly TimeSpan MaxPreviousSecretTtl = TimeSpan.FromDays(1);

    public static readonly TimeSpan UsageGranularity = TimeSpan.FromMinutes(5);

    public const int MaxAllowedCidrs = 32;

    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private const string Instance = PlatformDirectory.Instance;

    public static bool IsKeyId(string? value) =>
        TerraScaleIds.IsValid(value);

    public static IReadOnlyList<string>? NormalizeCidrs(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        List<string> cidrs = [];
        foreach (var value in values)
        {
            var parts = (value ?? "").Trim().Split('/');
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address) || parts[0].Contains('%', StringComparison.Ordinal))
            {
                return null;
            }

            address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var prefix = bits;
            if (parts.Length == 2 && (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out prefix) || prefix > bits))
            {
                return null;
            }

            var bytes = address.GetAddressBytes();
            for (var bit = prefix; bit < bits; bit++)
            {
                bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
            }

            var cidr = $"{new IPAddress(bytes)}/{prefix}";
            if (!cidrs.Contains(cidr))
            {
                cidrs.Add(cidr);
            }
        }

        return cidrs.Count <= MaxAllowedCidrs ? cidrs : null;
    }

    public static bool IsDatabaseId(string? value) => PlatformDirectory.IsId(value);

    public const int CreationsPerUserPerHour = 20;

    public async Task<(ApiKeyRecord Key, string Secret)> CreateAsync(UserRecord caller, string organizationId, string? projectId, string name, IReadOnlyList<string> scopes, string? database, DateTimeOffset? expiresAt, IReadOnlyList<string> allowedCidrs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await limiter.AcquireAsync(Instance, "api-key-create", caller.Id, CreationsPerUserPerHour, TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false) is { } wait)
        {
            throw ApiException.RateLimited("Too many API keys created; retry after the Retry-After delay.", wait);
        }

        return await directory.CommitAsync(async () =>
        {
            var authority = await this.AuthorizeAsync(caller, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            await directory.FenceAsync(organizationId, projectId, null, cancellationToken).ConfigureAwait(false);
            if (projectId is not null && scopes.Except(TerraBaseScopes.For(authority.Role)).Any())
            {
                throw PlatformDirectory.Forbidden();
            }

            var (id, secret) = Generate();
            var key = new ApiKeyRecord(id, Instance, organizationId, projectId, database, name, scopes, Tokens.Sha256Hex(secret), null, null, caller.Id, clock.GetUtcNow(), expiresAt, allowedCidrs)
            {
                SecretBindings = [Credentials.UserState(caller.Id, caller.AuthStamp!)],
            };
            List<Change> operations = [.. await this.RequirementsAsync(caller, cancellationToken).ConfigureAwait(false), .. authority.Assertions, Change.Insert(key)];

            operations.Add(directory.Audit(organizationId, caller.Id, "api_key.created", id, string.Join(' ', scopes)));
            return new PlatformDirectory.Plan<(ApiKeyRecord, string)>(operations, (key, secret));
        }, cancellationToken).ConfigureAwait(false);
    }

    public const int ScopedKeysPerMinute = 600;

    public Task<(ApiKeyRecord Key, string Secret)> CreateScopedAsync(ApiKeyRecord parent, string generation, string projectId, string database, IReadOnlyList<string> scopes,
        DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return directory.CommitAsync(async () =>
        {
            await directory.FenceAsync(parent.OrganizationId, projectId, null, cancellationToken).ConfigureAwait(false);
            var project = await directory.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
            var (id, secret) = Generate();
            var key = new ApiKeyRecord(id, Instance, parent.OrganizationId, projectId, database, "scoped " + database, scopes, Tokens.Sha256Hex(secret), null, null,
                parent.CreatedBy, clock.GetUtcNow(), expiresAt, parent.AllowedCidrs ?? [])
            {
                SecretBindings = parent.SecretBindings,
                ParentKeyId = parent.Id,
                ParentGeneration = generation,
            };
            List<Change> operations =
            [
                Change.Insert(key),
                Change.Assert(project),
                directory.Audit(parent.OrganizationId, parent.Id, "api_key.scoped_created", id, database + " " + string.Join(' ', scopes)),
            ];
            return new PlatformDirectory.Plan<(ApiKeyRecord, string)>(operations, (key, secret));
        }, cancellationToken);
    }

    public async Task<Page<ApiKeyRecord>> ListAsync(string userId, string organizationId, string? projectId, int limit, string? after, CancellationToken cancellationToken)
    {
        _ = await this.AuthorizeAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        var from = PlatformDirectory.Cursor(after);
        var rows = await data.ReadAsync((db, token) => db.ApiKeys
            .Where(key => key.OrganizationId == organizationId && key.ProjectId == projectId && string.Compare(key.Id, from) > 0)
            .OrderBy(key => key.Id).Take(limit + 1).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return PlatformDirectory.Paged(rows, limit, key => key.Id);
    }

    public async Task<ApiKeyRecord> GetAsync(string userId, string organizationId, string? projectId, string keyId, CancellationToken cancellationToken)
    {
        _ = await this.AuthorizeAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        return (await this.OwnedAsync(organizationId, projectId, keyId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound()).Value;
    }

    public Task RevokeAsync(string userId, string organizationId, string? projectId, string keyId, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var authority = await this.AuthorizeAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            var key = await this.OwnedAsync(organizationId, projectId, keyId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
            return new PlatformDirectory.Plan<bool>(
                [.. authority.Assertions, Change.Delete(key), directory.Audit(organizationId, userId, "api_key.revoked", keyId)], true);
        }, cancellationToken);

    public Task<ApiKeyRecord> SetAllowedCidrsAsync(string userId, string organizationId, string? projectId, string keyId, IReadOnlyList<string> allowedCidrs, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var authority = await this.AuthorizeAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            await directory.FenceAsync(organizationId, projectId, null, cancellationToken).ConfigureAwait(false);
            var key = await this.OwnedAsync(organizationId, projectId, keyId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
            if (projectId is not null && key.Value.Scopes.Except(TerraBaseScopes.For(authority.Role)).Any())
            {
                throw PlatformDirectory.Forbidden();
            }

            var changed = key.Value with { AllowedCidrs = allowedCidrs };
            return new PlatformDirectory.Plan<ApiKeyRecord>(
                [.. authority.Assertions, Change.Update(key, changed),
                    directory.Audit(organizationId, userId, "api_key.allowed_cidrs_changed", keyId, string.Join(' ', allowedCidrs))], changed);
        }, cancellationToken);

    public Task<(ApiKeyRecord Key, string Secret)> RotateAsync(UserRecord caller, string organizationId, string? projectId, string keyId, TimeSpan previousTtl, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            if (caller.StepUpBindings.Count == 0)
            {
                throw StepUpProofs.Required();
            }

            var authority = await this.AuthorizeAsync(caller, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            await directory.FenceAsync(organizationId, projectId, null, cancellationToken).ConfigureAwait(false);
            var key = await this.OwnedAsync(organizationId, projectId, keyId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
            if (projectId is not null && key.Value.Scopes.Except(TerraBaseScopes.For(authority.Role)).Any())
            {
                throw PlatformDirectory.Forbidden();
            }

            var now = clock.GetUtcNow();
            var secret = $"{keyId}_{RandomNumberGenerator.GetString(Alphabet, 52)}";
            var rotated = key.Value with
            {
                SecretHash = Tokens.Sha256Hex(secret),
                PreviousSecretHash = previousTtl > TimeSpan.Zero ? key.Value.SecretHash : null,
                PreviousSecretExpiresAt = previousTtl > TimeSpan.Zero ? now + previousTtl : null,
                SecretBindings = caller.StepUpBindings,
                PreviousSecretBindings = previousTtl > TimeSpan.Zero ? key.Value.SecretBindings : [],
            };
            return new PlatformDirectory.Plan<(ApiKeyRecord, string)>(
                [.. await this.RequirementsAsync(caller, cancellationToken).ConfigureAwait(false), .. authority.Assertions, Change.Update(key, rotated),
                    directory.Audit(organizationId, caller.Id, "api_key.rotated", keyId)], (rotated, secret));
        }, cancellationToken);

    public async Task<ApiKeyRecord?> FindAsync(string keyId, CancellationToken cancellationToken)
    {
        if (!IsKeyId(keyId))
        {
            return null;
        }

        if (await this.ReadAsync(keyId, cancellationToken).ConfigureAwait(false) is not { } key
            || key.ExpiresAt <= clock.GetUtcNow() || !await this.ParentActsAsync(key, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var current = key.SecretBindings.Count == 0 || await credentials.AreCurrentAsync(Instance, key.SecretBindings, cancellationToken).ConfigureAwait(false);
        var previous = key.PreviousSecretHash is not null && key.PreviousSecretExpiresAt > clock.GetUtcNow()
            && (key.PreviousSecretBindings.Count == 0 || await credentials.AreCurrentAsync(Instance, key.PreviousSecretBindings, cancellationToken).ConfigureAwait(false));
        // These digests are a transient authentication view; the stored rotation record keeps both original lineages.
        return current || previous ? key with { SecretHash = current ? key.SecretHash : string.Empty, PreviousSecretHash = previous ? key.PreviousSecretHash : null } : null;
    }

    public static string Generation(string secret) => Tokens.Sha256Hex(secret)[..16];

    public async Task<string?> RefusalAsync(string keyId, string generation, string? organizationId, string? projectId, CancellationToken cancellationToken)
    {
        if (!IsKeyId(keyId) || organizationId is null)
        {
            return "key_revoked";
        }

        // The key, and the organization and project its token names, together: one storage round trip, where reading the key
        // first made two, and TerraBase's two-second answer window is spent on them (#853). The token is this issuer's own
        // signed claim, and the key must still belong to that organization: the same answer as the key's own reads.
        var row = await data.ReadAsync((db, token) => db.ApiKeys.Where(key => key.Id == keyId && key.InstanceId == Instance)
            .Select(key => new
            {
                Key = key,
                Organization = db.Organizations.Any(organization => organization.Id == organizationId),
                ProjectOrganization = db.Projects.Where(project => project.Id == projectId).Select(project => project.OrganizationId).FirstOrDefault(),
            }).SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        if (row?.Key is not { } key)
        {
            return "key_revoked";
        }

        if (key.ExpiresAt <= clock.GetUtcNow())
        {
            return "key_expired";
        }

        if (!await this.ParentActsAsync(key, cancellationToken).ConfigureAwait(false))
        {
            return "key_revoked";
        }

        if (generation.Length != 16 || !this.Digests(key).Any(digest => digest.StartsWith(generation, StringComparison.Ordinal))
            || projectId is not null && key.ProjectId is not null && key.ProjectId != projectId)
        {
            return "key_revoked";
        }

        return key.OrganizationId != organizationId || !row.Organization
            || projectId is not null && row.ProjectOrganization != key.OrganizationId
            ? "key_revoked"
            : null;
    }

    public IReadOnlyList<string> Digests(ApiKeyRecord key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return [.. new[] { key.SecretHash, key.PreviousSecretExpiresAt > clock.GetUtcNow() ? key.PreviousSecretHash : null }.Where(hash => !string.IsNullOrEmpty(hash)).Select(hash => hash!)];
    }

    public async Task RecordUseAsync(ApiKeyRecord key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var now = clock.GetUtcNow();
        try
        {
            var usage = await data.ReadAsync((db, token) => db.ApiKeyUsage.Where(row => row.KeyId == key.Id).WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
            if (usage is not null && usage.Value.LastUsedAt > now - UsageGranularity)
            {
                return;
            }

            var record = new ApiKeyUsageRecord(key.Id, Instance, key.OrganizationId, key.ProjectId, now);
            await data.SaveAsync(usage is null ? Change.Insert(record) : Change.Update(usage, record), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or DbUpdateException)
        {
            logger.ZLogWarning($"Recording an API key's use failed ({error.GetType().Name:@Error}); the token is issued either way");
        }
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> LastUsedAsync(IReadOnlyList<ApiKeyRecord> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var ids = keys.Select(key => key.Id).ToArray();
        return await data.ReadAsync((db, token) => db.ApiKeyUsage.Where(usage => ids.Contains(usage.KeyId))
            .ToDictionaryAsync(usage => usage.KeyId, usage => usage.LastUsedAt, StringComparer.Ordinal, token), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiKeyRecord?> AuthenticateAsync(string? secret, CancellationToken cancellationToken)
    {
        if (secret is not { Length: 79 } || secret[26] != '_'
            || await this.FindAsync(secret[..26], cancellationToken).ConfigureAwait(false) is not { } key)
        {
            return null;
        }

        var digest = Tokens.Sha256Hex(secret);
        var matched = false;
        foreach (var candidate in this.Digests(key))
        {
            matched |= Tokens.FixedTimeEquals(digest, candidate);
        }

        return matched ? key : null;
    }

    private async Task<bool> ParentActsAsync(ApiKeyRecord key, CancellationToken cancellationToken) =>
        key.ParentKeyId is not { } parentId
        || key.ParentGeneration is { Length: 16 } generation
            && await this.FindAsync(parentId, cancellationToken).ConfigureAwait(false) is { ParentKeyId: null } parent
            && parent.OrganizationId == key.OrganizationId
            && this.Digests(parent).Any(digest => digest.StartsWith(generation, StringComparison.Ordinal));

    private static (string Id, string Secret) Generate()
    {
        var id = AuthIds.New();
        return (id, $"{id}_{RandomNumberGenerator.GetString(Alphabet, 52)}");
    }

    private async Task<IReadOnlyList<Change>> RequirementsAsync(UserRecord caller, CancellationToken cancellationToken) =>
        await proofs.RequirementsAsync(caller, cancellationToken).ConfigureAwait(false)
            ?? throw PlatformDirectory.Conflict("authentication_conflict", "This sign-in lost a concurrent change in another region. Sign in again before issuing an API key secret.");

    private Task<ApiKeyRecord?> ReadAsync(string keyId, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.ApiKeys.SingleOrDefaultAsync(key => key.Id == keyId && key.InstanceId == Instance, token), cancellationToken);

    private async Task<(string Role, Change[] Assertions)> AuthorizeAsync(UserRecord caller, string organizationId, string? projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.InstanceId != Instance)
        {
            throw PlatformDirectory.Forbidden();
        }

        if (!await credentials.AreCurrentAsync(Instance, caller.AuthenticationBindings, cancellationToken).ConfigureAwait(false))
        {
            throw PlatformDirectory.Conflict("authentication_conflict", "This sign-in lost a concurrent change in another region. Sign in again before issuing an API key secret.");
        }

        return await this.AuthorizeAsync(caller.Id, organizationId, projectId, cancellationToken).ConfigureAwait(false);
    }

    // Organization keys need the admin organization role; project keys the project's member role.
    private async Task<(string Role, Change[] Assertions)> AuthorizeAsync(string userId, string organizationId, string? projectId, CancellationToken cancellationToken)
    {
        if (projectId is null)
        {
            var organization = await directory.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            PlatformDirectory.RequireRole(organization, Roles.Admin);
            return (organization.Role!, [PlatformDirectory.Authority(organization), Change.Assert(organization.Organization)]);
        }

        var project = await directory.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        PlatformDirectory.RequireRole(project, Roles.Member);
        return (project.Role, [PlatformDirectory.Authority(project, Roles.Member), Change.Assert(project.Organization), Change.Assert(project.Project)]);
    }

    private async Task<Versioned<ApiKeyRecord>?> OwnedAsync(string organizationId, string? projectId, string keyId, CancellationToken cancellationToken) =>
        IsKeyId(keyId)
            ? await data.ReadAsync((db, token) => db.ApiKeys.Where(key => key.Id == keyId && key.InstanceId == Instance && key.OrganizationId == organizationId && key.ProjectId == projectId)
                .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false)
            : null;
}
