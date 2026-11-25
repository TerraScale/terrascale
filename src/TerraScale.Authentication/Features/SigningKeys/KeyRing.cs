using Dapper;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.AgentId;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Features.SigningKeys;

public sealed record KeyRingRefresh
{
    public static readonly TimeSpan Max = KeyRing.Overlap - Jwt.MaxLifetime;

    public KeyRingRefresh(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(interval, Max);
        this.Interval = interval;
    }

    public TimeSpan Interval { get; }
}

public sealed record Added;

public readonly union RotateIfDueResult(Added, AuthCompleted, AuthConflict);

public sealed class KeyRing(AuthData data, AuthOptions options, TimeProvider clock, KeyRingRefresh? refresh = null)
{
    public const int MaxKeys = 16;

    public static readonly TimeSpan RotationAge = TimeSpan.FromDays(30);

    public static readonly TimeSpan Overlap = TimeSpan.FromDays(2);

    public static readonly TimeSpan UnknownKeyRefresh = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan FailedReadBackoff = TimeSpan.FromSeconds(5);

    public const string NoSigningKeysCode = "no_signing_keys";

    private const string Algorithm = "ES256";

    private readonly TimeSpan refreshInterval = refresh?.Interval ?? TimeSpan.FromMinutes(1);

    private readonly Reads authority = new((instanceId, cancellationToken) => Keys(data, instanceId, cancellationToken));
    private readonly Reads published = new((instanceId, cancellationToken) => Keys(data, instanceId, cancellationToken));
    private readonly ConcurrentDictionary<string, Lazy<Task<Snapshot>>> pendingFirstKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> unknownKeyReads = new(StringComparer.Ordinal);

    private readonly ConditionalWeakTable<Snapshot, SigningKey[]> verified = new();

    private readonly byte[] publicMetadataKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth signing public metadata v1"u8.ToArray());
    private readonly string masterKeyId = Convert.ToHexStringLower(HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 16, info: "ts-auth signing ring master key id v1"u8.ToArray()));

    public async Task<IReadOnlyList<Es256Key>> GetActiveAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(this.authority, instanceId, fresh: false, cancellationToken);
        if (current.Keys.Count == 0)
        {
            current = await this.FirstKeyAsync(instanceId, cancellationToken);
        }

        var keys = this.Restore(instanceId, this.Verify(instanceId, current));
        var signer = SignerIndex(keys.Select(key => key.Record).ToList(), clock.GetUtcNow().ToUnixTimeSeconds());
        var ordered = keys.Select(key => key.Key).ToList();
        (ordered[0], ordered[signer]) = (ordered[signer], ordered[0]);
        return ordered;
    }

    public Task<IReadOnlyList<Es256Key>> GetPublicAsync(string instanceId, CancellationToken cancellationToken) =>
        this.GetPublicAsync(instanceId, keyId: null, cancellationToken);

    public async Task<IReadOnlyList<Es256Key>> GetPublicAsync(string instanceId, string? keyId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(this.authority, instanceId, fresh: false, cancellationToken);
        if (keyId is not null && current.Keys.All(key => key.Key.Id != keyId) && this.ClaimUnknownKeyRead(instanceId))
        {
            current = await this.ReadAsync(this.authority, instanceId, fresh: false, cancellationToken, TimeSpan.Zero);
        }

        if (current.Keys.Count == 0)
        {
            current = await this.FirstKeyAsync(instanceId, cancellationToken);
        }

        return RestorePublic(this.Verify(instanceId, current));
    }

    public async Task<IReadOnlyList<Es256Key>> GetPublishedAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(this.published, instanceId, fresh: false, cancellationToken);
        return current.Keys.Count == 0
            ? await this.GetPublicAsync(instanceId, cancellationToken)
            : RestorePublic(this.Verify(instanceId, current));
    }

    public async Task CheckPublicMetadataAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(this.authority, instanceId, fresh: false, cancellationToken, TimeSpan.Zero);
        if (current.Keys.Count == 0)
        {
            throw NoSigningKeys();
        }

        RestorePublic(this.Verify(instanceId, current)).ForEach(key => key.Dispose());
    }

    public async Task VerifyAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(this.authority, instanceId, fresh: true, cancellationToken);
        if (current.Keys.Count == 0)
        {
            throw NoSigningKeys();
        }

        this.Restore(instanceId, this.Verify(instanceId, current)).ForEach(key => key.Key.Dispose());
    }

    public async Task<bool> ProvisionAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(this.authority, instanceId, fresh: true, cancellationToken);
        var created = current.Keys.Count == 0;
        if (created)
        {
            current = await this.EnsureFirstAsync(instanceId, cancellationToken);
        }

        this.Restore(instanceId, this.Verify(instanceId, current)).ForEach(key => key.Key.Dispose());
        return created;
    }

    public static ApiException NoSigningKeys() =>
        new(503, NoSigningKeysCode, "no signing keys: startup bootstrap pending (docs/auth/signing-keys.md)");

    public async Task<RotateIfDueResult> RotateIfDueAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(this.authority, instanceId, fresh: true, cancellationToken);
        if (current.Keys.Count == 0)
        {
            RequireImplicitFirstKey(instanceId);
            (await this.CreateAsync(instanceId, TimeSpan.Zero, cancellationToken)).Dispose();
            return new Added();
        }

        var keys = this.Verify(instanceId, current);
        this.Restore(instanceId, keys).ForEach(key => key.Key.Dispose());
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (now < keys.Max(key => key.CreatedAt) + (long)RotationAge.TotalSeconds)
        {
            return new AuthCompleted();
        }

        var byActivation = keys.OrderBy(key => key.ActivatesAt).ThenBy(key => key.CreatedAt).ThenBy(key => key.Id, StringComparer.Ordinal).ToList();
        var retired = byActivation.TakeWhile((_, index) => index < byActivation.Count - 1 && byActivation[index + 1].ActivatesAt + (long)Overlap.TotalSeconds <= now).ToList();
        if (keys.Length - retired.Count >= MaxKeys)
        {
            return new AuthConflict("The signing key ring is full until its superseded keys pass the overlap.", "signing_key_limit");
        }

        foreach (var record in retired)
        {
            var version = current.Keys.First(stored => stored.Key.Id == record.Id).Version;
            _ = await Guard(() => data.SaveAsync(Change.Delete(record, version), cancellationToken), cancellationToken);
        }

        var key = await this.CreateAsync(instanceId, Overlap, cancellationToken);
        this.authority.Cached.TryRemove(instanceId, out _);
        key.Dispose();
        return new Added();
    }

    private static void RequireImplicitFirstKey(string instanceId)
    {
        if (instanceId == OidcIssuer.InstanceId)
        {
            throw NoSigningKeys();
        }
    }

    private Task<Snapshot> FirstKeyAsync(string instanceId, CancellationToken cancellationToken)
    {
        RequireImplicitFirstKey(instanceId);
        return this.EnsureFirstAsync(instanceId, cancellationToken);
    }

    private async Task<Snapshot> EnsureFirstAsync(string instanceId, CancellationToken cancellationToken)
    {
        Lazy<Task<Snapshot>>? created = null;
        created = new Lazy<Task<Snapshot>>(async () =>
        {
            try
            {
                var current = await this.ReadAsync(this.authority, instanceId, fresh: true, CancellationToken.None);
                if (current.Keys.Count == 0)
                {
                    (await this.CreateAsync(instanceId, TimeSpan.Zero, CancellationToken.None)).Dispose();
                    current = await this.ReadAsync(this.authority, instanceId, fresh: true, CancellationToken.None);
                }

                return current.Keys.Count > 0 ? current : throw ApiException.Unavailable();
            }
            finally
            {
                this.pendingFirstKeys.TryRemove(KeyValuePair.Create(instanceId, created!));
            }
        });
        return await this.pendingFirstKeys.GetOrAdd(instanceId, created).Value.WaitAsync(cancellationToken);
    }

    private async Task<Es256Key> CreateAsync(string instanceId, TimeSpan lead, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        var activatesAt = lead > TimeSpan.Zero ? RoundUp(now + lead) : now.ToUnixTimeSeconds();
        var key = Es256Key.Generate(AuthIds.New());
        try
        {
            var record = this.Seal(instanceId, key, now.ToUnixTimeSeconds(), activatesAt);
            if (instanceId is OidcIssuer.InstanceId or AgentIdIssuer.Slug)
            {
                if (!await Guard(() => data.SaveAsync(Change.Insert(record), cancellationToken), cancellationToken))
                {
                    throw ApiException.Unavailable();
                }
            }
            else if (await Guard(async () => Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectRecord>(
                "SELECT * FROM projects WHERE id = @instanceId AND instance_id = @platform", new { instanceId, platform = OidcIssuer.InstanceId }), cancellationToken)),
                cancellationToken) is not { } project)
            {
                throw new ApiException(404, "not_found", "The project does not exist.");
            }
            else if (!await Guard(() => data.SaveAsync([Change.Assert(project), Change.Insert(record)], cancellationToken), cancellationToken))
            {
                throw ApiException.Unavailable();
            }

            this.authority.Cached.TryRemove(instanceId, out _);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private async Task<Snapshot> ReadAsync(Reads source, string instanceId, bool fresh, CancellationToken cancellationToken, TimeSpan? maxAge = null)
    {
        if (fresh)
        {
            var read = source.Cached[instanceId] = await this.LoadAsync(source, instanceId, cancellationToken);
            source.Failed.TryRemove(instanceId, out _);
            return this.Current(read);
        }

        var limit = maxAge is { } age && age < this.refreshInterval ? age : this.refreshInterval;
        if (source.Cached.TryGetValue(instanceId, out var cached) && clock.GetElapsedTime(cached.Sent) < limit)
        {
            return cached;
        }

        if (source.Failed.TryGetValue(instanceId, out var failedAt) && clock.GetElapsedTime(failedAt) < FailedReadBackoff)
        {
            throw ApiException.Unavailable();
        }

        Lazy<Task<Snapshot>>? created = null;
        created = new Lazy<Task<Snapshot>>(async () =>
        {
            try
            {
                var read = source.Cached[instanceId] = await this.LoadAsync(source, instanceId, CancellationToken.None);
                source.Failed.TryRemove(instanceId, out _);
                return read;
            }
            catch
            {
                source.Failed[instanceId] = clock.GetTimestamp();
                throw;
            }
            finally
            {
                source.Pending.TryRemove(KeyValuePair.Create(instanceId, created!));
            }
        });
        var shared = source.Pending.GetOrAdd(instanceId, created);
        if (ReferenceEquals(shared, created))
        {
            _ = ObserveAsync(shared.Value);
        }

        return this.Current(await shared.Value.WaitAsync(cancellationToken));
    }

    private static async Task ObserveAsync(Task read)
    {
        await Task.WhenAny(read);
        _ = read.Exception;
    }

    private Snapshot Current(Snapshot read) =>
        this.refreshInterval > TimeSpan.Zero && clock.GetElapsedTime(read.Sent) >= this.refreshInterval ? throw ApiException.Unavailable() : read;

    private Task<Snapshot> LoadAsync(Reads source, string instanceId, CancellationToken cancellationToken) => Guard(async () =>
    {
        var sent = clock.GetTimestamp();
        var keys = new List<(SigningKey Key, string Version)>();
        foreach (var row in await source.Query(instanceId, cancellationToken))
        {
            if (keys.Count >= MaxKeys || row.Value.InstanceId != instanceId)
            {
                throw new CryptographicException("The instance's signing keys have an invalid count or address.");
            }

            keys.Add((row.Value, row.Version));
        }

        return new Snapshot(keys, sent);
    }, cancellationToken);

    private static async Task<T> Guard<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation();
        }
        catch (Exception failure) when (failure is not (ApiException or CryptographicException or OutOfMemoryException)
            && !(failure is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw ApiException.Unavailable(failure);
        }
    }

    private static long RoundUp(DateTimeOffset time) => time.ToUnixTimeSeconds() + (time.UtcTicks % TimeSpan.TicksPerSecond == 0 ? 0 : 1);

    private bool ClaimUnknownKeyRead(string instanceId)
    {
        var now = clock.GetTimestamp();
        while (true)
        {
            if (!this.unknownKeyReads.TryGetValue(instanceId, out var last))
            {
                if (this.unknownKeyReads.TryAdd(instanceId, now))
                {
                    return true;
                }
            }
            else if (clock.GetElapsedTime(last) < UnknownKeyRefresh)
            {
                return false;
            }
            else if (this.unknownKeyReads.TryUpdate(instanceId, now, last))
            {
                return true;
            }
        }
    }

    private static int SignerIndex(List<SigningKey> newestFirst, long now)
    {
        var signer = -1;
        for (var index = 0; index < newestFirst.Count; index++)
        {
            var key = newestFirst[index];
            if (key.ActivatesAt <= now && (signer < 0 || Later(key, newestFirst[signer])))
            {
                signer = index;
            }
        }

        return signer >= 0 ? signer : newestFirst.Count - 1;
    }

    private static bool Later(SigningKey left, SigningKey right) =>
        left.ActivatesAt != right.ActivatesAt ? left.ActivatesAt > right.ActivatesAt
        : left.CreatedAt != right.CreatedAt ? left.CreatedAt > right.CreatedAt
        : string.CompareOrdinal(left.Id, right.Id) > 0;

    private static string Context(string instanceId, string keyId) => $"signing-key:{keyId}:{instanceId}";

    private SigningKey Seal(string instanceId, Es256Key key, long createdAt, long activatesAt)
    {
        var secret = key.ExportSecret();
        try
        {
            var publicKey = key.PublicJwk();
            var record = new SigningKey(instanceId, this.masterKeyId, key.KeyId, Algorithm, SecretBox.Seal(options.MasterKey, secret, Context(instanceId, key.KeyId)), createdAt, activatesAt, publicKey.X, publicKey.Y, string.Empty);
            return record with { PublicMetadataMac = this.ComputePublicMetadataMac(record) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private string ComputePublicMetadataMac(SigningKey key) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(this.publicMetadataKey, JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            key.InstanceId, key.Id, key.Algorithm, key.CreatedAt.ToString(CultureInfo.InvariantCulture),
            key.ActivatesAt.ToString(CultureInfo.InvariantCulture), key.X, key.Y,
        })));

    private SigningKey[] Verify(string instanceId, Snapshot snapshot) => this.verified.GetValue(snapshot, read =>
    {
        foreach (var (key, _) in read.Keys)
        {
            if (!Tokens.FixedTimeEquals(key.MasterKeyId ?? string.Empty, this.masterKeyId))
            {
                throw new CryptographicException("A signing key was sealed under another master key.");
            }

            if (key.InstanceId != instanceId || key.Algorithm != Algorithm || !TerraScaleIds.IsValid(key.Id))
            {
                throw new CryptographicException("A signing key has an invalid owner, algorithm or identity.");
            }

            _ = DateTimeOffset.FromUnixTimeSeconds(key.CreatedAt);
            _ = DateTimeOffset.FromUnixTimeSeconds(key.ActivatesAt);
            if (key.PublicMetadataMac is null || !Tokens.FixedTimeEquals(this.ComputePublicMetadataMac(key), key.PublicMetadataMac))
            {
                throw new CryptographicException("The signing key's public metadata is not authentic.");
            }
        }

        return [.. read.Keys.Select(stored => stored.Key).OrderByDescending(key => key.CreatedAt).ThenByDescending(key => key.Id, StringComparer.Ordinal)];
    });

    private static List<Es256Key> RestorePublic(SigningKey[] keys)
    {
        var restored = new List<Es256Key>(keys.Length);
        try
        {
            foreach (var key in keys)
            {
                restored.Add(Es256Key.ImportPublic(key.Id, key.X, key.Y));
            }

            return restored;
        }
        catch
        {
            restored.ForEach(key => key.Dispose());
            throw;
        }
    }

    private List<(SigningKey Record, Es256Key Key)> Restore(string instanceId, SigningKey[] keys)
    {
        var restored = new List<(SigningKey, Es256Key)>(keys.Length);
        try
        {
            foreach (var record in keys)
            {
                var secret = SecretBox.Open(options.MasterKey, record.SecretBox, Context(instanceId, record.Id));
                try
                {
                    var key = Es256Key.Import(record.Id, secret);
                    restored.Add((record, key));
                    var publicKey = key.PublicJwk();
                    if (publicKey.X != record.X || publicKey.Y != record.Y)
                    {
                        throw new CryptographicException("The private signing key does not match its published coordinates.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secret);
                }
            }

            return restored;
        }
        catch
        {
            restored.ForEach(key => key.Item2.Dispose());
            throw;
        }
    }

    private sealed record Snapshot(IReadOnlyList<(SigningKey Key, string Version)> Keys, long Sent);

    private static async Task<List<Versioned<SigningKey>>> Keys(AuthData data, string instanceId, CancellationToken cancellationToken)
    {
        var take = MaxKeys + 1;
        return [.. Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<SigningKey>(
            "SELECT * FROM signing_keys WHERE instance_id = @instanceId ORDER BY id LIMIT @take", new { instanceId, take }), cancellationToken))];
    }

    private sealed class Reads(Func<string, CancellationToken, Task<List<Versioned<SigningKey>>>> query)
    {
        public Func<string, CancellationToken, Task<List<Versioned<SigningKey>>>> Query { get; } = query;

        public ConcurrentDictionary<string, Snapshot> Cached { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, Lazy<Task<Snapshot>>> Pending { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, long> Failed { get; } = new(StringComparer.Ordinal);
    }
}
