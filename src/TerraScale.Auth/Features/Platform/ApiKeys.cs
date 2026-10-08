using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Security;
using TerraScale.Auth.Store;
using TerraScale.DotNet;

namespace TerraScale.Auth.Features.Platform;

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

    /// <summary>Live shapes.</summary>
    public const string ShapesRead = "shapes:read";

    /// <summary>Every scope.</summary>
    public static IReadOnlyList<string> All { get; } = [DatabasesRead, DatabasesWrite, DocumentsRead, DocumentsWrite, ShapesRead];

    /// <summary>The scopes a project role grants (an organization role grants those of the project role it implies).</summary>
    public static IReadOnlyList<string> For(string? projectRole) => projectRole switch
    {
        Roles.Viewer => [DatabasesRead, DocumentsRead, ShapesRead],
        Roles.Member or Roles.Admin => All,
        _ => [],
    };
}

/// <summary>
/// API keys (TS-598): OAuth client credentials owned by an organization or a project, exchanged at the token endpoint
/// for 15-minute tokens. A key string is a canonical ULID (its client id), <c>_</c> and 52 base32
/// characters (260 random bits); only SHA-256 digests are stored and compared in constant time.
/// </summary>
public sealed partial class ApiKeys(AuthStore store, PlatformDirectory directory, TimeProvider clock)
{
    /// <summary>The longest grace a rotation gives the previous key string.</summary>
    public static readonly TimeSpan MaxPreviousSecretTtl = TimeSpan.FromDays(1);

    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private const string Instance = PlatformDirectory.Instance;

    /// <summary>Whether <paramref name="value"/> has the shape of a key id.</summary>
    public static bool IsKeyId(string? value) =>
        TerraScaleIds.IsValid(value);

    /// <summary>Whether <paramref name="value"/> is a TerraBase database name.</summary>
    public static bool IsDatabaseName(string? value) => value is not null && DatabaseName().IsMatch(value);

    /// <summary>
    /// Creates an organization key (<paramref name="projectId"/> null; needs the admin organization role) or a project
    /// key (needs the project's member role, and scopes within the caller's). Returns the key and its string, once.
    /// </summary>
    public Task<(ApiKeyRecord Key, string Secret)> CreateAsync(string userId, string organizationId, string? projectId, string name, IReadOnlyList<string> scopes, string? database, DateTimeOffset? expiresAt, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var authority = await this.AuthorizeAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (projectId is not null && scopes.Except(TerraBaseScopes.For(authority.Role)).Any())
            {
                throw PlatformDirectory.Forbidden();
            }

            var (id, secret) = Generate();
            var key = new ApiKeyRecord(id, Instance, organizationId, projectId, database, name, scopes, Tokens.Sha256Hex(secret), null, null, userId, clock.GetUtcNow(), expiresAt);
            List<StoreOperation> operations = [authority.Assertion, AuthStore.Create(AuthStore.ApiKeyCollection, Instance, id, Serialize(key))];
            if (projectId is not null)
            {
                // Orders the key against a deletion of the project.
                operations.Add(AuthStore.Require(AuthStore.ProjectCollection, Instance, projectId, PlatformDirectory.Filter("exists", "/id")));
            }

            operations.Add(directory.Audit(organizationId, userId, "api_key.created", id, string.Join(' ', scopes)));
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

    /// <summary>
    /// Issues a new key string for the same id. The previous string keeps working for <paramref name="previousTtl"/>
    /// (at most a day), then only the new one does. Another rotation is refused while that grace is still active.
    /// </summary>
    public Task<(ApiKeyRecord Key, string Secret)> RotateAsync(string userId, string organizationId, string? projectId, string keyId, TimeSpan previousTtl, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var authority = await this.AuthorizeAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
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
                [authority.Assertion, AuthStore.Replace(AuthStore.ApiKeyCollection, Instance, keyId, Serialize(rotated), key.Version), directory.Audit(organizationId, userId, "api_key.rotated", keyId)], (rotated, secret));
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

    /// <summary>The digests a presented key string may match now: the current one, and the previous one in its grace.</summary>
    public IReadOnlyList<string> Digests(ApiKeyRecord key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.PreviousSecretHash is { } previous && key.PreviousSecretExpiresAt > clock.GetUtcNow() ? [key.SecretHash, previous] : [key.SecretHash];
    }

    private static (string Id, string Secret) Generate()
    {
        var id = AuthStore.NewId();
        return (id, $"{id}_{RandomNumberGenerator.GetString(Alphabet, 52)}");
    }

    private static JsonElement Serialize(ApiKeyRecord key) => JsonSerializer.SerializeToElement(key, PlatformJson.Default.ApiKeyRecord);

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
