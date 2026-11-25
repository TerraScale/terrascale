using System.Net;
using Dapper;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Features.Agents;
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

    public const string AuthTokensMint = "auth:tokens:mint";

    public static IReadOnlyList<string> All { get; } = [DatabasesRead, DatabasesWrite, SqlRead, SqlWrite, AuthRead, AuthWrite, AuthTokensMint];

    public static bool IsAuth(string scope) => scope is AuthRead or AuthWrite or AuthTokensMint;

    public static IReadOnlyList<string> Delegated(IEnumerable<string> agentScopes) =>
        agentScopes.Contains(AgentDirectory.McpWrite, StringComparer.Ordinal) ? [DatabasesRead, DatabasesWrite, SqlRead, SqlWrite]
        : agentScopes.Contains(AgentDirectory.McpRead, StringComparer.Ordinal) ? [DatabasesRead, SqlRead]
        : [];

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
    private const string RetiredPrefix = "retired:";
    private const string Instance = PlatformDirectory.Instance;

    public static bool IsKeyId(string? value) =>
        TerraScaleIds.IsValid(value);

    public static (string Id, string Presented)? Parse(string? secret) =>
        secret is { Length: 79 } && secret[26] == '_' ? (secret[..26], secret) : null;

    public static bool Covers(ApiKeyRecord key, string? role)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.ProjectId is null
            ? Roles.Rank(role) >= Roles.Rank(Roles.Admin)
            : Roles.Rank(role) >= Roles.Rank(Roles.Member) && !key.Scopes.Except(TerraBaseScopes.For(role), StringComparer.Ordinal).Any();
    }

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
        if (await limiter.AcquireAsync(Instance, "api-key-create", caller.Id, CreationsPerUserPerHour, TimeSpan.FromHours(1), cancellationToken) is { } wait)
        {
            throw ApiException.RateLimited("Too many API keys created; retry after the Retry-After delay.", wait);
        }

        return await directory.CommitAsync(async () =>
        {
            var authority = await this.AuthorizeAsync(caller, organizationId, projectId, cancellationToken);
            await directory.FenceAsync(organizationId, projectId, null, cancellationToken);
            if (projectId is not null && scopes.Except(TerraBaseScopes.For(authority.Role)).Any())
            {
                throw PlatformDirectory.Forbidden();
            }

            var (id, secret) = Generate();
            var key = new ApiKeyRecord(id, Instance, organizationId, projectId, database, name, scopes, Tokens.Sha256Hex(secret), null, null, caller.Id, clock.GetUtcNow(), expiresAt, allowedCidrs);
            List<Change> operations = [.. await this.RequirementsAsync(caller, cancellationToken), authority.Issuing, .. authority.Assertions, Change.Insert(key)];

            operations.Add(directory.Audit(organizationId, caller.Id, "api_key.created", id, string.Join(' ', scopes)));
            return new PlatformDirectory.Plan<(ApiKeyRecord, string)>(operations, (key, secret));
        }, cancellationToken);
    }

    public const int ScopedKeysPerMinute = 600;

    public Task<(ApiKeyRecord Key, string Secret)> CreateScopedAsync(ApiKeyRecord parent, string generation, string projectId, string database, IReadOnlyList<string> scopes,
        DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return directory.CommitAsync(async () =>
        {
            await directory.FenceAsync(parent.OrganizationId, projectId, null, cancellationToken);
            var project = await directory.ProjectAsync(projectId, cancellationToken) ?? throw PlatformDirectory.NotFound();
            var (id, secret) = Generate();
            var key = new ApiKeyRecord(id, Instance, parent.OrganizationId, projectId, database, "scoped " + database, scopes, Tokens.Sha256Hex(secret), null, null,
                parent.CreatedBy, clock.GetUtcNow(), expiresAt, parent.AllowedCidrs ?? [])
            {
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
        _ = await this.AuthorizeAsync(userId, organizationId, projectId, cancellationToken);
        var from = PlatformDirectory.Cursor(after);
        var take = limit + 1;
        List<ApiKeyRecord> live = [];
        List<ApiKeyRecord> rows;
        do
        {
            var cursor = from;
            var now = clock.GetUtcNow();
            rows = (await data.ReadAsync(connection => connection.QueryAsync<ApiKeyRecord>(
                """
                SELECT * FROM api_keys WHERE organization_id = @organizationId AND project_id IS @projectId AND id > @cursor
                    AND (expires_at IS NULL OR expires_at > @now) ORDER BY id LIMIT @take
                """, new { organizationId, projectId, cursor, now, take }), cancellationToken)).AsList();
            var roles = await this.CreatorRolesAsync(organizationId, projectId, [.. rows.Select(key => key.CreatedBy).Distinct(StringComparer.Ordinal)], cancellationToken);
            foreach (var key in rows)
            {
                if (live.Count < take && await this.LiveAsync(key, roles.GetValueOrDefault(key.CreatedBy), cancellationToken) is not null)
                {
                    live.Add(key);
                }
            }

            from = rows.Count > 0 ? rows[^1].Id : from;
        }
        while (live.Count < take && rows.Count == take);

        return PlatformDirectory.Paged(live, limit, key => key.Id);
    }

    public async Task<ApiKeyRecord> GetAsync(string userId, string organizationId, string? projectId, string keyId, CancellationToken cancellationToken)
    {
        _ = await this.AuthorizeAsync(userId, organizationId, projectId, cancellationToken);
        return (await this.OwnedAsync(organizationId, projectId, keyId, cancellationToken) ?? throw PlatformDirectory.NotFound()).Value;
    }

    public Task RevokeAsync(string userId, string organizationId, string? projectId, string keyId, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var authority = await this.AuthorizeAsync(userId, organizationId, projectId, cancellationToken);
            var key = await this.OwnedAsync(organizationId, projectId, keyId, cancellationToken) ?? throw PlatformDirectory.NotFound();
            return new PlatformDirectory.Plan<bool>(
                [.. authority.Assertions, Change.Delete(key), directory.Audit(organizationId, userId, "api_key.revoked", keyId)], true);
        }, cancellationToken);

    public Task<ApiKeyRecord> SetAllowedCidrsAsync(string userId, string organizationId, string? projectId, string keyId, IReadOnlyList<string> allowedCidrs, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var authority = await this.AuthorizeAsync(userId, organizationId, projectId, cancellationToken);
            await directory.FenceAsync(organizationId, projectId, null, cancellationToken);
            var key = await this.OwnedAsync(organizationId, projectId, keyId, cancellationToken) ?? throw PlatformDirectory.NotFound();
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

            var authority = await this.AuthorizeAsync(caller, organizationId, projectId, cancellationToken);
            await directory.FenceAsync(organizationId, projectId, null, cancellationToken);
            var key = await this.OwnedAsync(organizationId, projectId, keyId, cancellationToken) ?? throw PlatformDirectory.NotFound();
            if (projectId is not null && key.Value.Scopes.Except(TerraBaseScopes.For(authority.Role)).Any())
            {
                throw PlatformDirectory.Forbidden();
            }

            var now = clock.GetUtcNow();
            var secret = $"{keyId}_{RandomNumberGenerator.GetString(Alphabet, 52)}";
            var keep = previousTtl > TimeSpan.Zero && !Retired(key.Value.SecretHash);
            var carried = previousTtl > TimeSpan.Zero && Retired(key.Value.SecretHash) && key.Value.PreviousSecretHash is not null && key.Value.PreviousSecretExpiresAt > now;
            var rotated = key.Value with
            {
                SecretHash = Tokens.Sha256Hex(secret),
                PreviousSecretHash = keep ? key.Value.SecretHash : carried ? key.Value.PreviousSecretHash : null,
                PreviousSecretExpiresAt = keep ? now + previousTtl : carried ? key.Value.PreviousSecretExpiresAt : null,
                SecretBindings = [],
                PreviousSecretBindings = keep ? key.Value.SecretBindings : carried ? key.Value.PreviousSecretBindings : [],
            };
            return new PlatformDirectory.Plan<(ApiKeyRecord, string)>(
                [.. await this.RequirementsAsync(caller, cancellationToken), .. authority.Assertions, Change.Update(key, rotated),
                    directory.Audit(organizationId, caller.Id, "api_key.rotated", keyId)], (rotated, secret));
        }, cancellationToken);

    public async Task<ApiKeyRecord?> FindAsync(string keyId, CancellationToken cancellationToken) =>
        IsKeyId(keyId) && await this.ReadAsync(keyId, cancellationToken) is { } key
            ? await this.LiveAsync(key, (await this.CreatorRolesAsync(key.OrganizationId, key.ProjectId, [key.CreatedBy], cancellationToken)).GetValueOrDefault(key.CreatedBy), cancellationToken)
            : null;

    private async Task<ApiKeyRecord?> LiveAsync(ApiKeyRecord key, string? creatorRole, CancellationToken cancellationToken)
    {
        if (key.ExpiresAt <= clock.GetUtcNow() || !Covers(key, creatorRole) || !await this.ParentActsAsync(key, cancellationToken))
        {
            return null;
        }

        var current = !Retired(key.SecretHash) && await this.HeldAsync(key.SecretBindings, cancellationToken);
        var previous = key.PreviousSecretHash is { } previousHash && !Retired(previousHash) && key.PreviousSecretExpiresAt > clock.GetUtcNow()
            && await this.HeldAsync(key.PreviousSecretBindings, cancellationToken);
        return current || previous ? key with { SecretHash = current ? key.SecretHash : string.Empty, PreviousSecretHash = previous ? key.PreviousSecretHash : null } : null;
    }

    public static string Generation(string secret) => Tokens.Sha256Hex(secret)[..16];

    public async Task<string?> RefusalAsync(string keyId, string generation, string? organizationId, string? projectId, CancellationToken cancellationToken)
    {
        if (!IsKeyId(keyId) || organizationId is null || await this.ReadAsync(keyId, cancellationToken) is not { } stored)
        {
            return "key_revoked";
        }

        if (stored.ExpiresAt <= clock.GetUtcNow())
        {
            return "key_expired";
        }

        if (await this.FindAsync(keyId, cancellationToken) is not { } key
            || generation.Length != 16 || !this.Digests(key).Any(digest => digest.StartsWith(generation, StringComparison.Ordinal))
            || projectId is not null && key.ProjectId is not null && key.ProjectId != projectId)
        {
            return "key_revoked";
        }

        var scope = await data.ReadAsync(connection => connection.QuerySingleAsync<KeyScope>(
            """
            SELECT EXISTS (SELECT 1 FROM organizations WHERE id = @organizationId) AS organization,
                (SELECT organization_id FROM projects WHERE id = @projectId) AS project_organization
            """, new { organizationId, projectId }), cancellationToken);
        return key.OrganizationId != organizationId || !scope.Organization
            || projectId is not null && scope.ProjectOrganization != key.OrganizationId
            ? "key_revoked"
            : null;
    }

    public IReadOnlyList<string> Digests(ApiKeyRecord key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return [.. new[] { key.SecretHash, key.PreviousSecretExpiresAt > clock.GetUtcNow() ? key.PreviousSecretHash : null }.OfType<string>().Where(hash => hash.Length > 0 && !Retired(hash))];
    }

    public async Task RecordUseAsync(ApiKeyRecord key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var now = clock.GetUtcNow();
        try
        {
            var keyId = key.Id;
            var usage = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ApiKeyUsageRecord>(
                "SELECT * FROM api_key_usage WHERE key_id = @keyId", new { keyId }), cancellationToken));
            if (usage is not null && usage.Value.LastUsedAt > now - UsageGranularity)
            {
                return;
            }

            var record = new ApiKeyUsageRecord(key.Id, Instance, key.OrganizationId, key.ProjectId, now);
            await data.SaveAsync(usage is null ? Change.Insert(record) : Change.Update(usage, record), cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or ApiException)
        {
            logger.ZLogWarning($"Recording an API key's use failed ({error.GetType().Name:@Error}); the token is issued either way");
        }
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> LastUsedAsync(IReadOnlyList<ApiKeyRecord> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var ids = keys.Select(key => key.Id).ToArray();
        return (await data.ReadAsync(connection => connection.QueryAsync<ApiKeyUsageRecord>(
            "SELECT * FROM api_key_usage WHERE key_id IN @ids", new { ids }), cancellationToken))
            .ToDictionary(usage => usage.KeyId, usage => usage.LastUsedAt, StringComparer.Ordinal);
    }

    public async Task<ApiKeyRecord?> AuthenticateAsync(string? secret, CancellationToken cancellationToken)
    {
        if (Parse(secret) is not { } parsed
            || await this.FindAsync(parsed.Id, cancellationToken) is not { } key)
        {
            return null;
        }

        var digest = Tokens.Sha256Hex(parsed.Presented);
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
            && await this.FindAsync(parentId, cancellationToken) is { ParentKeyId: null } parent
            && parent.OrganizationId == key.OrganizationId
            && this.Digests(parent).Any(digest => digest.StartsWith(generation, StringComparison.Ordinal));

    private static bool Retired(string hash) => hash.StartsWith(RetiredPrefix, StringComparison.Ordinal);

    private async Task<bool> HeldAsync(IReadOnlyList<CredentialBinding> bindings, CancellationToken cancellationToken)
    {
        var claimed = bindings.Where(binding => binding != Credentials.UserState(binding.Key, binding.OwnerId)).ToArray();
        return claimed.Length == 0 || await credentials.AreCurrentAsync(Instance, claimed, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, string?>> CreatorRolesAsync(string organizationId, string? projectId, string[] creators, CancellationToken cancellationToken) =>
        creators.Length == 0
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : (await data.ReadAsync(connection => connection.QueryAsync<CreatorRole>(
                """
                SELECT m.user_id, m.role AS organization_role, g.role AS project_role FROM memberships m
                LEFT JOIN project_members g ON g.project_id = @projectId AND g.organization_id = m.organization_id AND g.user_id = m.user_id
                WHERE m.organization_id = @organizationId AND m.user_id IN @creators
                """, new { organizationId, projectId, creators }), cancellationToken))
                .ToDictionary(role => role.UserId, role => projectId is null ? role.OrganizationRole : Roles.Higher(Roles.Implied(role.OrganizationRole), role.ProjectRole),
                    StringComparer.Ordinal);

    private static (string Id, string Secret) Generate()
    {
        var id = AuthIds.New();
        return (id, $"{id}_{RandomNumberGenerator.GetString(Alphabet, 52)}");
    }

    private async Task<IReadOnlyList<Change>> RequirementsAsync(UserRecord caller, CancellationToken cancellationToken) =>
        await proofs.RequirementsAsync(caller, cancellationToken)
            ?? throw PlatformDirectory.Conflict("authentication_conflict", "This sign-in lost a concurrent change in another region. Sign in again before issuing an API key secret.");

    private Task<ApiKeyRecord?> ReadAsync(string keyId, CancellationToken cancellationToken) =>
        data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ApiKeyRecord>(
            "SELECT * FROM api_keys WHERE id = @keyId AND instance_id = @Instance", new { keyId, Instance }), cancellationToken);

    private async Task<(string Role, Change[] Assertions, Change Issuing)> AuthorizeAsync(UserRecord caller, string organizationId, string? projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.InstanceId != Instance)
        {
            throw PlatformDirectory.Forbidden();
        }

        if (!await credentials.AreCurrentAsync(Instance, caller.AuthenticationBindings, cancellationToken))
        {
            throw PlatformDirectory.Conflict("authentication_conflict", "This sign-in lost a concurrent change in another region. Sign in again before issuing an API key secret.");
        }

        return await this.AuthorizeAsync(caller.Id, organizationId, projectId, cancellationToken);
    }

    private async Task<(string Role, Change[] Assertions, Change Issuing)> AuthorizeAsync(string userId, string organizationId, string? projectId, CancellationToken cancellationToken)
    {
        if (projectId is null)
        {
            var organization = await directory.OrganizationAccessAsync(userId, organizationId, cancellationToken);
            PlatformDirectory.RequireRole(organization, Roles.Admin);
            return organization.Role is { } role
                ? (role, [PlatformDirectory.Authority(organization), Change.Assert(organization.Organization)], PlatformDirectory.Issuing(organization))
                : throw PlatformDirectory.Forbidden();
        }

        var project = await directory.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken);
        PlatformDirectory.RequireRole(project, Roles.Member);
        return (project.Role, [PlatformDirectory.Authority(project, Roles.Member), Change.Assert(project.Organization), Change.Assert(project.Project)], PlatformDirectory.Issuing(project));
    }

    private async Task<Versioned<ApiKeyRecord>?> OwnedAsync(string organizationId, string? projectId, string keyId, CancellationToken cancellationToken) =>
        IsKeyId(keyId)
            ? Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ApiKeyRecord>(
                "SELECT * FROM api_keys WHERE id = @keyId AND instance_id = @Instance AND organization_id = @organizationId AND project_id IS @projectId",
                new { keyId, Instance, organizationId, projectId }), cancellationToken))
            : null;

    internal sealed record CreatorRole(string UserId, string? OrganizationRole, string? ProjectRole);

    internal sealed record KeyScope(bool Organization, string? ProjectOrganization);
}
