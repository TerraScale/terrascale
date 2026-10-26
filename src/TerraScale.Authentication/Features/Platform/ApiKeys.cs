using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>The TerraBase scopes ts-auth grants; roles become scopes only here.</summary>
public static class TerraBaseScopes
{
    /// <summary>Read databases, schema, indexes, backups, policies, usage and saved queries; read-only SQL.</summary>
    public const string DatabasesRead = "databases:read";

    /// <summary>Create, branch and delete databases; backups, restores, policies, indexes and saved queries.</summary>
    public const string DatabasesWrite = "databases:write";

    /// <summary>Read collections and documents; queries.</summary>
    public const string DocumentsRead = "documents:read";

    /// <summary>Write collections and documents; transactions.</summary>
    public const string DocumentsWrite = "documents:write";

    /// <summary>Every scope.</summary>
    public static IReadOnlyList<string> All { get; } = [DatabasesRead, DatabasesWrite, DocumentsRead, DocumentsWrite];

    /// <summary>The scopes a project role grants (an organization role grants those of the project role it implies).</summary>
    public static IReadOnlyList<string> For(string? projectRole) => projectRole switch
    {
        Roles.Viewer => [DatabasesRead, DocumentsRead],
        Roles.Member or Roles.Admin => All,
        _ => [],
    };
}

/// <summary>
/// API keys (TS-598): OAuth client credentials owned by an organization or a project, exchanged at the token endpoint
/// for 60-second tokens. A key works until it is revoked or expires: it does not depend on the sign-in that created it,
/// so signing out, a password reset or a lost regional sign-in race never disables it. A key string is a canonical ULID (its client id), <c>_</c> and 52 base32
/// characters (260 random bits); only SHA-256 digests are stored and compared in constant time.
/// </summary>
public sealed partial class ApiKeys(AuthStore store, PlatformDirectory directory, TimeProvider clock)
{
    /// <summary>The longest grace a rotation gives the previous key string.</summary>
    public static readonly TimeSpan MaxPreviousSecretTtl = TimeSpan.FromDays(1);

    /// <summary>How stale a key's last-used time may be: a key records at most one use per this interval.</summary>
    public static readonly TimeSpan UsageGranularity = TimeSpan.FromMinutes(5);

    /// <summary>The most CIDRs one allowlist holds.</summary>
    public const int MaxAllowedCidrs = 32;

    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private const string Instance = PlatformDirectory.Instance;

    /// <summary>Whether <paramref name="value"/> has the shape of a key id.</summary>
    public static bool IsKeyId(string? value) =>
        TerraScaleIds.IsValid(value);

    /// <summary>
    /// The canonical form of an IP allowlist: each entry an IPv4 or IPv6 CIDR or bare address (a /32 or /128), host bits
    /// cleared, IPv4-mapped addresses as IPv4, duplicates removed. Null when an entry is invalid or there are too many.
    /// </summary>
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

    /// <summary>Whether <paramref name="value"/> is a TerraBase database name.</summary>
    public static bool IsDatabaseName(string? value) => value is not null && DatabaseName().IsMatch(value);

    /// <summary>
    /// Creates an organization key (<paramref name="projectId"/> null; needs the admin organization role) or a project
    /// key (needs the project's member role, and scopes within the caller's). Returns the key and its string, once.
    /// </summary>
    public Task<(ApiKeyRecord Key, string Secret)> CreateAsync(UserRecord caller, string organizationId, string? projectId, string name, IReadOnlyList<string> scopes, string? database, DateTimeOffset? expiresAt, IReadOnlyList<string> allowedCidrs, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var authority = await this.AuthorizeAsync(caller, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (projectId is not null && scopes.Except(TerraBaseScopes.For(authority.Role)).Any())
            {
                throw PlatformDirectory.Forbidden();
            }

            var (id, secret) = Generate();
            var key = new ApiKeyRecord(id, Instance, organizationId, projectId, database, name, scopes, Tokens.Sha256Hex(secret), null, null, caller.Id, clock.GetUtcNow(), expiresAt, allowedCidrs);
            List<StoreOperation> operations = [authority.Assertion, AuthStore.Create(AuthStore.ApiKeyCollection, Instance, id, Serialize(key))];
            if (projectId is not null)
            {
                // Orders the key against a deletion of the project.
                operations.Add(AuthStore.Require(AuthStore.ProjectCollection, Instance, projectId, PlatformDirectory.Filter("exists", "/id")));
            }

            operations.Add(directory.Audit(organizationId, caller.Id, "api_key.created", id, string.Join(' ', scopes)));
            return new PlatformDirectory.Plan<(ApiKeyRecord, string)>(operations, (key, secret));
        }, cancellationToken);

    /// <summary>A page of the organization's keys, or the project's, oldest first; the same roles as creating.</summary>
    public async Task<Page<ApiKeyRecord>> ListAsync(string userId, string organizationId, string? projectId, int limit, string? after, CancellationToken cancellationToken)
    {
        _ = await this.AuthorizeAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        // An organization key's project is null; a project key's is its project.
        var project = new JsonObject { ["eq"] = new JsonArray("/projectId", projectId is null ? null : JsonValue.Create(projectId)) };
        var page = await directory.PageAsync(AuthStore.ApiKeyCollection, [PlatformDirectory.Equal("/organizationId", organizationId), project],
            PlatformJson.Default.ApiKeyRecord, "/createdAt", limit, after, cancellationToken).ConfigureAwait(false);
        return new([.. page.Items.Select(key => key.Value).Where(key => key.InstanceId == Instance && key.OrganizationId == organizationId && key.ProjectId == projectId)], page.Next);
    }

    /// <summary>One key of the organization or project; 404 for any other.</summary>
    public async Task<ApiKeyRecord> GetAsync(string userId, string organizationId, string? projectId, string keyId, CancellationToken cancellationToken)
    {
        _ = await this.AuthorizeAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        return (await this.OwnedAsync(organizationId, projectId, keyId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound()).Value;
    }

    /// <summary>Revokes (deletes) a key: no token is issued for it afterwards.</summary>
    public Task RevokeAsync(string userId, string organizationId, string? projectId, string keyId, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var authority = await this.AuthorizeAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            var key = await this.OwnedAsync(organizationId, projectId, keyId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
            return new PlatformDirectory.Plan<bool>(
                [authority.Assertion, AuthStore.Delete(AuthStore.ApiKeyCollection, Instance, keyId, key.Version), directory.Audit(organizationId, userId, "api_key.revoked", keyId)], true);
        }, cancellationToken);

    /// <summary>Replaces the key's IP allowlist (empty allows any address); its next token carries the new list.</summary>
    public Task<ApiKeyRecord> SetAllowedCidrsAsync(string userId, string organizationId, string? projectId, string keyId, IReadOnlyList<string> allowedCidrs, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var authority = await this.AuthorizeAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            var key = await this.OwnedAsync(organizationId, projectId, keyId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
            var changed = key.Value with { AllowedCidrs = allowedCidrs };
            return new PlatformDirectory.Plan<ApiKeyRecord>(
                [authority.Assertion, AuthStore.Replace(AuthStore.ApiKeyCollection, Instance, keyId, Serialize(changed), key.Version),
                    directory.Audit(organizationId, userId, "api_key.allowed_cidrs_changed", keyId, string.Join(' ', allowedCidrs))], changed);
        }, cancellationToken);

    /// <summary>
    /// Issues a new key string for the same id. The previous string keeps working for <paramref name="previousTtl"/>
    /// (at most a day), then only the new one does. Another rotation is refused while that grace is still active.
    /// </summary>
    public Task<(ApiKeyRecord Key, string Secret)> RotateAsync(UserRecord caller, string organizationId, string? projectId, string keyId, TimeSpan previousTtl, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var authority = await this.AuthorizeAsync(caller, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            var key = await this.OwnedAsync(organizationId, projectId, keyId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
            var now = clock.GetUtcNow();
            if (key.Value.PreviousSecretHash is not null && key.Value.PreviousSecretExpiresAt > now)
            {
                throw PlatformDirectory.Conflict("rotation_pending", "The previous API key secret is still in its rotation grace period.");
            }

            var secret = $"{keyId}_{RandomNumberGenerator.GetString(Alphabet, 52)}";
            var rotated = key.Value with
            {
                SecretHash = Tokens.Sha256Hex(secret),
                PreviousSecretHash = previousTtl > TimeSpan.Zero ? key.Value.SecretHash : null,
                PreviousSecretExpiresAt = previousTtl > TimeSpan.Zero ? now + previousTtl : null,
            };
            return new PlatformDirectory.Plan<(ApiKeyRecord, string)>(
                [authority.Assertion, AuthStore.Replace(AuthStore.ApiKeyCollection, Instance, keyId, Serialize(rotated), key.Version), directory.Audit(organizationId, caller.Id, "api_key.rotated", keyId)], (rotated, secret));
        }, cancellationToken);

    /// <summary>The key a client id names, unless it is unknown or expired. Callers authenticate its string first.</summary>
    public async Task<ApiKeyRecord?> FindAsync(string keyId, CancellationToken cancellationToken)
    {
        if (!IsKeyId(keyId))
        {
            return null;
        }

        var document = await store.GetAsync(AuthStore.ApiKeyCollection, Instance, keyId, cancellationToken).ConfigureAwait(false);
        return document?.Document.Deserialize(PlatformJson.Default.ApiKeyRecord) is { } key && key.InstanceId == Instance && key.Id == keyId
            && (key.ExpiresAt is null || key.ExpiresAt > clock.GetUtcNow())
            ? key
            : null;
    }

    /// <summary>The <c>key_gen</c> of a key string: the first 16 hex digits of its stored digest.</summary>
    public static string Generation(string secret) => Tokens.Sha256Hex(secret)[..16];

    /// <summary>
    /// Why a token of key <paramref name="keyId"/>, issued for the key string of <paramref name="generation"/> to act in
    /// <paramref name="projectId"/>, no longer acts (<c>key_revoked</c> or <c>key_expired</c>), or null while it still does:
    /// the key, its organization and the project still exist, and the key string still works. Always read from the store.
    /// </summary>
    public async Task<string?> RefusalAsync(string keyId, string generation, string? projectId, CancellationToken cancellationToken)
    {
        var document = IsKeyId(keyId) ? await store.GetAsync(AuthStore.ApiKeyCollection, Instance, keyId, cancellationToken).ConfigureAwait(false) : null;
        if (document?.Document.Deserialize(PlatformJson.Default.ApiKeyRecord) is not { } key || key.InstanceId != Instance || key.Id != keyId)
        {
            return "key_revoked";
        }

        if (key.ExpiresAt <= clock.GetUtcNow())
        {
            return "key_expired";
        }

        if (generation.Length != 16 || !this.Digests(key).Any(digest => digest.StartsWith(generation, StringComparison.Ordinal))
            || projectId is not null && key.ProjectId is not null && key.ProjectId != projectId)
        {
            return "key_revoked";
        }

        // The same reads as an exchange (PlatformGrants.ApiKeyAsync), together: one storage round trip.
        var organization = directory.OrganizationAsync(key.OrganizationId, cancellationToken);
        var project = projectId is null ? Task.FromResult<Versioned<ProjectRecord>?>(null) : directory.ProjectAsync(projectId, cancellationToken);
        await Task.WhenAll(organization, project).ConfigureAwait(false);
        return await organization.ConfigureAwait(false) is null
            || projectId is not null && (await project.ConfigureAwait(false))?.Value.OrganizationId != key.OrganizationId
            ? "key_revoked"
            : null;
    }

    /// <summary>The digests a presented key string may match now: the current one, and the previous one in its grace.</summary>
    public IReadOnlyList<string> Digests(ApiKeyRecord key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.PreviousSecretHash is { } previous && key.PreviousSecretExpiresAt > clock.GetUtcNow() ? [key.SecretHash, previous] : [key.SecretHash];
    }

    /// <summary>
    /// Records that <paramref name="key"/> just exchanged a token, unless it already did within <see cref="UsageGranularity"/>.
    /// Best effort: a concurrent write in another region wins, and a storage failure never fails the exchange.
    /// </summary>
    public async Task RecordUseAsync(ApiKeyRecord key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var now = clock.GetUtcNow();
        try
        {
            var document = await store.GetAsync(AuthStore.ApiKeyUsageCollection, Instance, key.Id, cancellationToken).ConfigureAwait(false);
            if (document?.Document.Deserialize(PlatformJson.Default.ApiKeyUsageRecord) is { } usage && usage.LastUsedAt > now - UsageGranularity)
            {
                return;
            }

            var record = JsonSerializer.SerializeToElement(new ApiKeyUsageRecord(key.Id, Instance, key.OrganizationId, key.ProjectId, now), PlatformJson.Default.ApiKeyUsageRecord);
            if (document is null)
            {
                await store.CreateAsync(AuthStore.ApiKeyUsageCollection, Instance, key.Id, record, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await store.ReplaceAsync(AuthStore.ApiKeyUsageCollection, Instance, key.Id, record, document.Version, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException)
        {
            // Another exchange recorded the use first, or storage is unavailable; the token is issued either way.
        }
    }

    /// <summary>When each of <paramref name="keys"/> was last used; a key absent from the answer was never used.</summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> LastUsedAsync(IReadOnlyList<ApiKeyRecord> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var uses = await Task.WhenAll(keys.Select(async key =>
            (await store.GetAsync(AuthStore.ApiKeyUsageCollection, Instance, key.Id, cancellationToken).ConfigureAwait(false))?.Document
                .Deserialize(PlatformJson.Default.ApiKeyUsageRecord) is { } usage && usage.InstanceId == Instance && usage.KeyId == key.Id
                ? usage : null)).ConfigureAwait(false);
        return uses.OfType<ApiKeyUsageRecord>().ToDictionary(usage => usage.KeyId, usage => usage.LastUsedAt);
    }

    /// <summary>Authenticates a whole key string against a fresh authority read, including rotation grace and expiry.</summary>
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

    private static (string Id, string Secret) Generate()
    {
        var id = AuthStore.NewId();
        return (id, $"{id}_{RandomNumberGenerator.GetString(Alphabet, 52)}");
    }

    private static JsonElement Serialize(ApiKeyRecord key) => JsonSerializer.SerializeToElement(key, PlatformJson.Default.ApiKeyRecord);

    private async Task<(string Role, StoreOperation Assertion)> AuthorizeAsync(UserRecord caller, string organizationId, string? projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.InstanceId != Instance)
        {
            throw PlatformDirectory.Forbidden();
        }

        if (!await store.AreCredentialsCurrentAsync(Instance, caller.AuthenticationBindings, cancellationToken).ConfigureAwait(false))
        {
            throw PlatformDirectory.Conflict("authentication_conflict", "This sign-in lost a concurrent change in another region. Sign in again before issuing an API key secret.");
        }

        return await this.AuthorizeAsync(caller.Id, organizationId, projectId, cancellationToken).ConfigureAwait(false);
    }

    // Organization keys need the admin organization role; project keys the project's member role.
    private async Task<(string Role, StoreOperation Assertion)> AuthorizeAsync(string userId, string organizationId, string? projectId, CancellationToken cancellationToken)
    {
        if (projectId is null)
        {
            var organization = await directory.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            PlatformDirectory.RequireRole(organization, Roles.Admin);
            return (organization.Role!, PlatformDirectory.Authority(organization, Roles.Admin));
        }

        var project = await directory.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        PlatformDirectory.RequireRole(project, Roles.Member);
        return (project.Role, PlatformDirectory.Authority(project, Roles.Member));
    }

    private async Task<Versioned<ApiKeyRecord>?> OwnedAsync(string organizationId, string? projectId, string keyId, CancellationToken cancellationToken)
    {
        if (!IsKeyId(keyId))
        {
            return null;
        }

        var document = await store.GetAsync(AuthStore.ApiKeyCollection, Instance, keyId, cancellationToken).ConfigureAwait(false);
        return document?.Document.Deserialize(PlatformJson.Default.ApiKeyRecord) is { } key && key.InstanceId == Instance && key.Id == keyId
            && key.OrganizationId == organizationId && key.ProjectId == projectId
            ? new(key, document.Version)
            : null;
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,62}\z")]
    private static partial Regex DatabaseName();
}
