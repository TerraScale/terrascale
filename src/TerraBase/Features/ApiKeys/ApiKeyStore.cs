using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.ControlPlane;
using TerraBase.Hosting;

namespace TerraBase.Features.ApiKeys;

internal readonly union IssueApiKeyOutcome(IssuedApiKey, StateConflict, BackendUnavailable);

internal readonly union ApiKeyRevokeOutcome(Completed, ResourceNotFound, BackendUnavailable);

internal readonly union ApiKeyLookup(ApiKeyRecord, ResourceNotFound, BackendUnavailable);

public sealed record ApiKeyRecord(
    string Id, string Name, IReadOnlyList<string> Scopes, Ulid OrganizationId, Ulid? ProjectId, Ulid? DatabaseId, DateTimeOffset? ExpiresAt)
{
    public bool Grants(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);

    public bool Reaches(ProjectRecord project, DatabaseRecord? database) =>
        project.OrganizationId == OrganizationId
        && (ProjectId is not { } projectId || projectId == project.Id)
        && (DatabaseId is not { } databaseId || (database is not null && databaseId == database.Id));
}

public sealed record IssuedApiKey(ApiKeyRecord Key, string Secret);

internal sealed class ApiKeyStore(SystemDatabase system, ControlSnapshots snapshots, TimeProvider time)
{
    public const string SecretPrefix = "tb_";

    private const int MaxSecretLength = 256;

    private const int MaxCacheEntries = 4096;

    private static readonly ResourceNotFound UnknownKey = new("unknown API key", "invalid_key");

    private static readonly ResourceNotFound ExpiredKey = new("the API key expired", "invalid_key");

    private readonly ApiKeyCache _cache = new(MaxCacheEntries);

    public async ValueTask<IssueApiKeyOutcome> CreateAsync(
        string name, IReadOnlyList<string> scopes, Ulid organizationId, Ulid? projectId, Ulid? databaseId, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var secret = string.Concat(SecretPrefix, Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));
        var hash = Hash(secret);
        var key = new ApiKeyRecord(Ulid.NewUlid(time.GetUtcNow()).ToString(), name, scopes, organizationId, projectId, databaseId, expiresAt);
        return await system.WriteAsync(
        [
            SqlBuild.Statement(
                "INSERT INTO api_keys (id, name, scopes, organization_id, project_id, database_id, expires_at, hash, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                SqlBuild.Text(key.Id), SqlBuild.Text(name), SqlBuild.Text(string.Join(' ', scopes)), SqlBuild.Id(organizationId),
                projectId is { } project ? SqlBuild.Id(project) : SqlValue.Null, databaseId is { } database ? SqlBuild.Id(database) : SqlValue.Null,
                SqlBuild.OptionalTime(expiresAt), SqlBuild.Text(hash), SqlBuild.Time(time.GetUtcNow())),
            SqlBuild.Statement("INSERT INTO api_key_hashes (hash, key_id) VALUES (?, ?)", SqlBuild.Text(hash), SqlBuild.Text(key.Id)),
        ], ConsistencyMode.Strong, cancellationToken) switch
        {
            SqlWriteResult => new IssuedApiKey(key, secret),
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ApiKeyRevokeOutcome> RevokeAsync(string? keyId, CancellationToken cancellationToken) =>
        keyId is null ? new ResourceNotFound("the API key does not exist", "unknown_api_key") : await system.WriteAsync(
        [
            SqlBuild.Statement("DELETE FROM api_key_hashes WHERE key_id = ?", SqlBuild.Text(keyId)),
            SqlBuild.Statement("DELETE FROM api_keys WHERE id = ?", SqlBuild.Text(keyId)),
        ], ConsistencyMode.Strong, cancellationToken) switch
        {
            SqlWriteResult { ResultSets: [_, { RowsAffected: > 0 }] } => new Completed(),
            SqlWriteResult => new ResourceNotFound("the API key does not exist", "unknown_api_key"),
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };

    public bool TryFindCached(ReadOnlySpan<char> secret, out ApiKeyLookup outcome)
    {
        if (!secret.StartsWith(SecretPrefix, StringComparison.Ordinal) || secret.Length > MaxSecretLength)
        {
            outcome = UnknownKey;
            return true;
        }

        var generation = system.ChangeGeneration;
        if (generation <= 0)
        {
            outcome = default;
            return false;
        }

        var snapshotVersion = snapshots.Version;
        var digest = TokenDigest.HashUtf8(secret);
        if (_cache.TryGet(generation, snapshotVersion, digest, out var cached))
        {
            if (IsExpired(cached))
            {
                _cache.Remove(generation, snapshotVersion, digest);
                outcome = ExpiredKey;
                return true;
            }

            outcome = cached;
            return true;
        }

        outcome = default;
        return false;
    }

    public ValueTask<ApiKeyLookup> FindAsync(string secret, CancellationToken cancellationToken)
    {
        if (!secret.StartsWith(SecretPrefix, StringComparison.Ordinal) || secret.Length > MaxSecretLength)
        {
            return ValueTask.FromResult<ApiKeyLookup>(UnknownKey);
        }

        Span<byte> digestBytes = stackalloc byte[SHA256.HashSizeInBytes];
        TokenDigest.HashUtf8(secret, digestBytes);
        var digest = TokenDigest.FromHash(digestBytes);
        var generation = system.ChangeGeneration;
        var snapshotVersion = snapshots.Version;
        if (generation > 0 && _cache.TryGet(generation, snapshotVersion, digest, out var cached))
        {
            if (IsExpired(cached))
            {
                _cache.Remove(generation, snapshotVersion, digest);
                return ValueTask.FromResult<ApiKeyLookup>(ExpiredKey);
            }

            return ValueTask.FromResult<ApiKeyLookup>(cached);
        }

        var hash = Convert.ToHexStringLower(digestBytes);
        if (snapshots.FindApiKey(hash) is { } verifier)
        {
            var record = new ApiKeyRecord(verifier.KeyId, verifier.KeyId, verifier.Scopes, snapshots.OrganizationOf(verifier.ProjectId) ?? Ulid.Empty,
                verifier.ProjectId, verifier.DatabaseId, verifier.ExpiresAtUtc);
            return ValueTask.FromResult(CacheAndCheck(digest, record, generation, snapshotVersion));
        }

        return FindInSystemAsync(digest, hash, generation, snapshotVersion, cancellationToken);
    }

    private async ValueTask<ApiKeyLookup> FindInSystemAsync(
        TokenDigest digest, string hash, long generation, long snapshotVersion, CancellationToken cancellationToken)
    {
        var outcome = await system.QueryAsync(SqlBuild.Statement(
            "SELECT k.id, k.name, k.scopes, k.organization_id, k.project_id, k.database_id, k.expires_at FROM api_key_hashes h JOIN api_keys k ON k.id = h.key_id WHERE h.hash = ?",
            SqlBuild.Text(hash)), cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> { Count: > 0 } rows => CacheAndCheck(digest, Read(rows[0]), generation, snapshotVersion),
            IReadOnlyList<IReadOnlyList<SqlValue>> => UnknownKey,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private ApiKeyLookup CacheAndCheck(TokenDigest digest, ApiKeyRecord key, long generation, long snapshotVersion)
    {
        if (IsExpired(key))
        {
            return ExpiredKey;
        }

        if (generation > 0 && system.ChangeGeneration == generation && snapshots.Version == snapshotVersion)
        {
            _cache.Set(generation, snapshotVersion, digest, key);
        }

        return key;
    }

    private bool IsExpired(ApiKeyRecord key) => key.ExpiresAt is { } expires && expires <= time.GetUtcNow();

    private static ApiKeyRecord Read(IReadOnlyList<SqlValue> row) => new(
        row.TextAt(0), row.TextAt(1), row.TextAt(2).Split(' ', StringSplitOptions.RemoveEmptyEntries), row.IdAt(3),
        row.OptionalTextAt(4) is { } project && Ulid.TryParse(project, out var projectId) ? projectId : null,
        row.OptionalTextAt(5) is { } database && Ulid.TryParse(database, out var databaseId) ? databaseId : null,
        row.OptionalTimeAt(6));

    public static string Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        TokenDigest.HashUtf8(secret, digest);
        return Convert.ToHexStringLower(digest);
    }

    private sealed class ApiKeyCache(int maxEntries)
    {
        private readonly Lock _swapGate = new();
        private volatile Bucket _current = new(0, 0);

        public bool TryGet(long generation, long snapshotVersion, TokenDigest key, [NotNullWhen(true)] out ApiKeyRecord? value)
        {
            var bucket = _current;
            if (bucket.Generation == generation && bucket.SnapshotVersion == snapshotVersion && bucket.Entries.TryGetValue(key, out value))
            {
                return true;
            }

            value = null;
            return false;
        }

        public void Set(long generation, long snapshotVersion, TokenDigest key, ApiKeyRecord value)
        {
            var bucket = _current;
            if (bucket.Generation != generation || bucket.SnapshotVersion != snapshotVersion)
            {
                lock (_swapGate)
                {
                    bucket = _current;
                    if (generation < bucket.Generation || (generation == bucket.Generation && snapshotVersion < bucket.SnapshotVersion))
                    {
                        return;
                    }

                    if (bucket.Generation != generation || bucket.SnapshotVersion != snapshotVersion)
                    {
                        bucket = new Bucket(generation, snapshotVersion);
                        _current = bucket;
                    }
                }
            }

            if (Volatile.Read(ref bucket.Count) < maxEntries && bucket.Entries.TryAdd(key, value))
            {
                _ = Interlocked.Increment(ref bucket.Count);
            }
        }

        public void Remove(long generation, long snapshotVersion, TokenDigest key)
        {
            var bucket = _current;
            if (bucket.Generation == generation && bucket.SnapshotVersion == snapshotVersion && bucket.Entries.TryRemove(key, out _))
            {
                _ = Interlocked.Decrement(ref bucket.Count);
            }
        }

        private sealed class Bucket(long generation, long snapshotVersion)
        {
            public long Generation { get; } = generation;
            public long SnapshotVersion { get; } = snapshotVersion;
            public ConcurrentDictionary<TokenDigest, ApiKeyRecord> Entries { get; } = new();
            public int Count;
        }
    }
}
