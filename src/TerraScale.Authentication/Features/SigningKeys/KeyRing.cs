using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Features.SigningKeys;

/// <summary>
/// How long a replica serves the keys it read before reading TerraBase again, counted from when the read was sent. One
/// minute in production, where nothing configures it, so every replica uses the same interval.
/// </summary>
public sealed record KeyRingRefresh
{
    /// <summary>
    /// The longest interval: a superseded key's last token (at most <see cref="Jwt.MaxLifetime"/>) expires before that
    /// key retires, even on a replica that last read the keys one interval ago.
    /// </summary>
    public static readonly TimeSpan Max = KeyRing.Overlap - Jwt.MaxLifetime;

    /// <summary>Validates the interval: zero (reread on every lookup) up to <see cref="Max"/>.</summary>
    public KeyRingRefresh(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(interval, Max);
        this.Interval = interval;
    }

    /// <summary>The longest a read is reused; zero rereads on every lookup.</summary>
    public TimeSpan Interval { get; }
}

/// <summary>
/// Per-instance ES256 signing keys, one TerraBase document per key in <see cref="AuthStore.SigningKeyCollection"/>, each
/// private key sealed under the master key. The JWKS and the signer come from the same read, so a key signs only once it
/// is published: the newest key whose activation time has passed signs, and every key activates after it is stored, by
/// <see cref="Overlap"/> for a scheduled rotation and by one <see cref="KeyRingRefresh"/> interval (when every replica's
/// cached JWKS holds it) for an unscheduled one. Keys are only added and retired, never changed, so replicas that add
/// keys at once all keep theirs and agree on the signer once they read the same set. Every rotation retires the keys
/// whose successor has been signing for at least <see cref="Overlap"/>.
/// </summary>
public sealed class KeyRing(AuthStore store, AuthOptions options, TimeProvider clock, KeyRingRefresh? refresh = null)
{
    /// <summary>The most keys an instance holds, which bounds every lookup. A rotation that would exceed it after retirement is refused (409).</summary>
    public const int MaxKeys = 16;

    /// <summary>A scheduled rotation adds a key once the newest one is this old.</summary>
    public static readonly TimeSpan RotationAge = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a scheduled key is published before it signs, and how long a superseded key stays published after its
    /// successor starts signing: longer than <see cref="Jwt.MaxLifetime"/>, so its last token expires first.
    /// </summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromDays(2);

    /// <summary>The least time between rereads caused by tokens naming a key the cached read lacks.</summary>
    public static readonly TimeSpan UnknownKeyRefresh = TimeSpan.FromSeconds(10);

    /// <summary>After a failed shared read, lookups answer 503 without asking TerraBase for this long.</summary>
    public static readonly TimeSpan FailedReadBackoff = TimeSpan.FromSeconds(5);

    /// <summary>The error code of a platform issuer without signing keys; readiness reports its message.</summary>
    public const string NoSigningKeysCode = "no_signing_keys";

    private const string Algorithm = "ES256";

    private readonly TimeSpan refreshInterval = refresh?.Interval ?? TimeSpan.FromMinutes(1);

    // The authority's reads serve every security use: signing, signature checks, readiness, rotation and first keys. This
    // region's copy's serve only GetPublishedAsync. Each source has its own cache, shared in-flight reads and failure
    // backoff, so a JWKS read never becomes a security decision and an authority outage never fails a healthy JWKS.
    private readonly Reads authority = new((instanceId, cancellationToken) => store.QueryAsync(AuthStore.SigningKeyCollection, instanceId, [], cancellationToken));
    private readonly Reads published = new((instanceId, cancellationToken) => store.QueryLocalAsync(AuthStore.SigningKeyCollection, instanceId, cancellationToken));
    private readonly ConcurrentDictionary<string, Lazy<Task<Snapshot>>> pendingFirstKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> unknownKeyReads = new(StringComparer.Ordinal);

    // A snapshot never changes: its keys' public metadata is verified once.
    private readonly ConditionalWeakTable<Snapshot, SigningKey[]> verified = new();

    // "v1" names the public-metadata MAC scheme: this derivation and the fields ComputePublicMetadataMac covers. Bump
    // both strings with any change to it, so keys of the old scheme read as sealed under another master key and fail
    // closed.
    private readonly byte[] publicMetadataKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth signing public metadata v1"u8.ToArray());
    private readonly string masterKeyId = Convert.ToHexStringLower(HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 16, info: "ts-auth signing ring master key id v1"u8.ToArray()));

    /// <summary>
    /// The instance's keys, the signing key first; a project tenant's first key is created on first use, the platform
    /// issuer's never (<see cref="NoSigningKeys"/>). The caller owns the returned keys and disposes them.
    /// </summary>
    public async Task<IReadOnlyList<Es256Key>> GetActiveAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(this.authority, instanceId, fresh: false, cancellationToken).ConfigureAwait(false);
        if (current.Keys.Count == 0)
        {
            current = await this.FirstKeyAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }

        var keys = this.Restore(instanceId, this.Verify(instanceId, current));
        var signer = SignerIndex(keys.Select(key => key.Record).ToList(), clock.GetUtcNow().ToUnixTimeSeconds());
        var ordered = keys.Select(key => key.Key).ToList();
        (ordered[0], ordered[signer]) = (ordered[signer], ordered[0]);
        return ordered;
    }

    /// <summary>Loads public verification keys, newest first, without opening any private-key box. First use creates a project tenant's first key.</summary>
    public Task<IReadOnlyList<Es256Key>> GetPublicAsync(string instanceId, CancellationToken cancellationToken) =>
        this.GetPublicAsync(instanceId, keyId: null, cancellationToken);

    /// <summary>
    /// Like <see cref="GetPublicAsync(string, CancellationToken)"/> for verifying a token signed with
    /// <paramref name="keyId"/>: when this replica's cached read lacks that key, TerraBase is read again first (at most
    /// every <see cref="UnknownKeyRefresh"/>), so a key another replica just added verifies at once.
    /// </summary>
    public async Task<IReadOnlyList<Es256Key>> GetPublicAsync(string instanceId, string? keyId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(this.authority, instanceId, fresh: false, cancellationToken).ConfigureAwait(false);
        if (keyId is not null && current.Keys.All(key => key.Key.Id != keyId) && this.ClaimUnknownKeyRead(instanceId))
        {
            current = await this.ReadAsync(this.authority, instanceId, fresh: false, cancellationToken, TimeSpan.Zero).ConfigureAwait(false);
        }

        if (current.Keys.Count == 0)
        {
            current = await this.FirstKeyAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }

        return RestorePublic(this.Verify(instanceId, current));
    }

    /// <summary>
    /// The JWKS: the instance's public keys as this region's copy holds them (another region's when it does not answer), read
    /// and cached apart from the authority's, which every signature check, signing and readiness use. An empty copy (keys
    /// not replicated yet) publishes the authority's keys instead, as <see cref="GetPublicAsync(string, CancellationToken)"/>
    /// reads them, which creates a project's first key there: a copy's missing keys never cause a create of their own.
    /// </summary>
    public async Task<IReadOnlyList<Es256Key>> GetPublishedAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(this.published, instanceId, fresh: false, cancellationToken).ConfigureAwait(false);
        return current.Keys.Count == 0
            ? await this.GetPublicAsync(instanceId, cancellationToken).ConfigureAwait(false)
            : RestorePublic(this.Verify(instanceId, current));
    }

    /// <summary>
    /// Validates the instance's public key metadata as TerraBase holds it now, bypassing the refresh interval but sharing
    /// an in-flight read; never creates or changes state. An instance without keys fails with <see cref="NoSigningKeys"/>.
    /// </summary>
    public async Task CheckPublicMetadataAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(this.authority, instanceId, fresh: false, cancellationToken, TimeSpan.Zero).ConfigureAwait(false);
        if (current.Keys.Count == 0)
        {
            throw NoSigningKeys();
        }

        RestorePublic(this.Verify(instanceId, current)).ForEach(key => key.Dispose());
    }

    /// <summary>
    /// Startup bootstrap: checks that the instance has keys and that every stored key verifies and opens under this
    /// master key; never creates or changes state. No keys fails with <see cref="NoSigningKeys"/>; keys it cannot use
    /// are a misconfigured replica or tampering and fail until an operator removes them. Storage failures surface as 503.
    /// </summary>
    public async Task VerifyAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(this.authority, instanceId, fresh: true, cancellationToken).ConfigureAwait(false);
        if (current.Keys.Count == 0)
        {
            throw NoSigningKeys();
        }

        this.Restore(instanceId, this.Verify(instanceId, current)).ForEach(key => key.Key.Dispose());
    }

    /// <summary>
    /// Startup or operator provisioning (<see cref="SigningKeyProvisioning"/>): creates the instance's first key when it has
    /// none, then checks every stored key like <see cref="VerifyAsync"/>. Existing keys are never replaced or changed, so
    /// running it again, or on two replicas at once, is safe. Returns whether it created a key.
    /// </summary>
    public async Task<bool> ProvisionAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(this.authority, instanceId, fresh: true, cancellationToken).ConfigureAwait(false);
        var created = current.Keys.Count == 0;
        if (created)
        {
            current = await this.EnsureFirstAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }

        this.Restore(instanceId, this.Verify(instanceId, current)).ForEach(key => key.Key.Dispose());
        return created;
    }

    /// <summary>The platform issuer has no signing key: 503 until startup provisioning completes.</summary>
    public static ApiException NoSigningKeys() =>
        new(503, NoSigningKeysCode, "no signing keys: startup bootstrap pending (docs/auth/signing-keys.md)");

    /// <summary>
    /// Adds a key now, published at once and signing one refresh interval later, when every replica's cached JWKS holds it,
    /// and retires keys superseded for at least <see cref="Overlap"/>. The caller owns the returned key and disposes it.
    /// </summary>
    public async Task<Es256Key> RotateAsync(string instanceId, CancellationToken cancellationToken) =>
        await this.RotateAsync(instanceId, scheduled: false, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("An unscheduled rotation always adds a key.");

    /// <summary>
    /// The scheduled rotation: once the newest key is <see cref="RotationAge"/> old, adds a key that signs after
    /// <see cref="Overlap"/>, and retires keys superseded for at least <see cref="Overlap"/>. Returns whether it added a key.
    /// </summary>
    public async Task<bool> RotateIfDueAsync(string instanceId, CancellationToken cancellationToken)
    {
        using var key = await this.RotateAsync(instanceId, scheduled: true, cancellationToken).ConfigureAwait(false);
        return key is not null;
    }

    // Every key is verified and opened first, so a rotation never adds to keys this replica cannot use.
    private async Task<Es256Key?> RotateAsync(string instanceId, bool scheduled, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(this.authority, instanceId, fresh: true, cancellationToken).ConfigureAwait(false);
        if (current.Keys.Count == 0)
        {
            RequireImplicitFirstKey(instanceId);
            return await this.CreateAsync(instanceId, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        }

        var keys = this.Verify(instanceId, current);
        this.Restore(instanceId, keys).ForEach(key => key.Key.Dispose());
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (scheduled && now < keys.Max(key => key.CreatedAt) + (long)RotationAge.TotalSeconds)
        {
            return null;
        }

        // Never retires the newest key or one whose successor is not yet signing.
        var byActivation = keys.OrderBy(key => key.ActivatesAt).ThenBy(key => key.CreatedAt).ThenBy(key => key.Id, StringComparer.Ordinal).ToList();
        var retired = byActivation.TakeWhile((_, index) => index < byActivation.Count - 1 && byActivation[index + 1].ActivatesAt + (long)Overlap.TotalSeconds <= now).ToList();
        if (keys.Length - retired.Count >= MaxKeys)
        {
            throw new ApiException(409, "signing_key_limit", "The signing key ring is full until its superseded keys pass the overlap.");
        }

        // Retired first, so the instance never holds more than MaxKeys keys.
        foreach (var record in retired)
        {
            var version = current.Keys.First(stored => stored.Key.Id == record.Id).Version;
            // False: another replica retired it first.
            _ = await Guard(() => store.CompareAndDeleteAsync(AuthStore.SigningKeyCollection, instanceId, record.Id, version, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        var key = await this.CreateAsync(instanceId, scheduled ? Overlap : this.refreshInterval, cancellationToken).ConfigureAwait(false);
        // This replica serves the new set from its next read.
        this.authority.Cached.TryRemove(instanceId, out _);
        return key;
    }

    // The platform issuer's first key comes only from ProvisionAsync: an empty ring there is lost or wiped state, and a
    // silently created key would sign tokens no relying party's cached JWKS holds and hide the loss. A project tenant's
    // first key is created on first use; its project record, checked before any tenant request, gates it.
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

    // Creates the instance's first key unless a fresh read finds one, and returns the read that holds it. Concurrent
    // callers on this replica share one attempt, which ends only when TerraBase answers, never with a waiter.
    private async Task<Snapshot> EnsureFirstAsync(string instanceId, CancellationToken cancellationToken)
    {
        Lazy<Task<Snapshot>>? created = null;
        created = new Lazy<Task<Snapshot>>(async () =>
        {
            try
            {
                var current = await this.ReadAsync(this.authority, instanceId, fresh: true, CancellationToken.None).ConfigureAwait(false);
                if (current.Keys.Count == 0)
                {
                    // Another replica may add its first key at once; both are kept and published.
                    (await this.CreateAsync(instanceId, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false)).Dispose();
                    current = await this.ReadAsync(this.authority, instanceId, fresh: true, CancellationToken.None).ConfigureAwait(false);
                }

                return current.Keys.Count > 0 ? current : throw ApiException.Unavailable();
            }
            finally
            {
                this.pendingFirstKeys.TryRemove(KeyValuePair.Create(instanceId, created!));
            }
        });
        return await this.pendingFirstKeys.GetOrAdd(instanceId, created).Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Generates a key that signs after lead and stores it under its own address. The caller owns the returned key.
    private async Task<Es256Key> CreateAsync(string instanceId, TimeSpan lead, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        // A positive lead is rounded up to the second: the key never signs before it has fully passed.
        var activatesAt = lead > TimeSpan.Zero ? RoundUp(now + lead) : now.ToUnixTimeSeconds();
        var key = Es256Key.Generate(AuthStore.NewId());
        try
        {
            var record = JsonSerializer.SerializeToElement(this.Seal(instanceId, key, now.ToUnixTimeSeconds(), activatesAt));
            if (instanceId == OidcIssuer.InstanceId)
            {
                await Guard(() => store.CreateAsync(AuthStore.SigningKeyCollection, instanceId, key.KeyId, record, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            // A project tenant's key is stored only while its project exists at the authority, checked in the same
            // transaction: a copy that still lists a deleted project never adds a key for it.
            else if (await Guard(() => store.TransactAsync(
            [
                AuthStore.Require(AuthStore.ProjectCollection, OidcIssuer.InstanceId, instanceId, PlatformDirectory.Filter("exists", "/id")),
                AuthStore.Create(AuthStore.SigningKeyCollection, instanceId, key.KeyId, record),
            ], cancellationToken), cancellationToken).ConfigureAwait(false) is int failed)
            {
                throw failed == 0 ? new ApiException(404, "not_found", "The project does not exist.") : ApiException.Unavailable();
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

    // A fresh read bypasses the cache. Otherwise the keys as this replica last read them, rereading once that read is
    // older than the refresh interval or maxAge (measured monotonically); concurrent lookups on this replica share one
    // read, which ends only when TerraBase answers, never with a waiter.
    private async Task<Snapshot> ReadAsync(Reads source, string instanceId, bool fresh, CancellationToken cancellationToken, TimeSpan? maxAge = null)
    {
        if (fresh)
        {
            var read = source.Cached[instanceId] = await this.LoadAsync(source, instanceId, cancellationToken).ConfigureAwait(false);
            // Successful provisioning supersedes an earlier readiness read before the collections existed.
            source.Failed.TryRemove(instanceId, out _);
            return this.Current(read);
        }

        var limit = maxAge is { } age && age < this.refreshInterval ? age : this.refreshInterval;
        if (source.Cached.TryGetValue(instanceId, out var cached) && clock.GetElapsedTime(cached.Sent) < limit)
        {
            return cached;
        }

        // Fail closed without asking TerraBase again right after a failed read: traffic never multiplies an outage.
        if (source.Failed.TryGetValue(instanceId, out var failedAt) && clock.GetElapsedTime(failedAt) < FailedReadBackoff)
        {
            throw ApiException.Unavailable();
        }

        Lazy<Task<Snapshot>>? created = null;
        created = new Lazy<Task<Snapshot>>(async () =>
        {
            try
            {
                var read = source.Cached[instanceId] = await this.LoadAsync(source, instanceId, CancellationToken.None).ConfigureAwait(false);
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
        return this.Current(await shared.Value.WaitAsync(cancellationToken).ConfigureAwait(false));
    }

    // A read that took longer than the refresh interval (measured from when it was sent) is already older than any cached
    // read may be, so it is refused, never served: a key removed at the authority is accepted for at most that interval,
    // in-flight reads included. Zero rereads on every lookup and bounds nothing.
    private Snapshot Current(Snapshot read) =>
        this.refreshInterval > TimeSpan.Zero && clock.GetElapsedTime(read.Sent) >= this.refreshInterval ? throw ApiException.Unavailable() : read;

    private Task<Snapshot> LoadAsync(Reads source, string instanceId, CancellationToken cancellationToken) => Guard(async () =>
    {
        var sent = clock.GetTimestamp();
        var keys = new List<(SigningKey Key, string Version)>();
        await foreach (var document in source.Query(instanceId, cancellationToken).ConfigureAwait(false))
        {
            var key = document.Document.Deserialize<SigningKey>() ?? throw new JsonException("A signing key document is empty.");
            // A key copied to another address, as a storage writer could, is never served.
            if (keys.Count >= MaxKeys || key.InstanceId != instanceId || document.Id != AuthStore.NaturalKeyId(instanceId, AuthStore.SigningKeyCollection, key.Id))
            {
                throw new CryptographicException("The instance's signing keys have an invalid count or address.");
            }

            keys.Add((key, document.Version));
        }

        return new Snapshot(keys, sent);
    }, cancellationToken);

    // Storage failures, malformed documents included, fail closed with 503; tampering found later answers 500.
    private static async Task<T> Guard<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not (ApiException or CryptographicException or OutOfMemoryException)
            && !(failure is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw ApiException.Unavailable(failure);
        }
    }

    // Whole Unix seconds, never earlier than the time given.
    private static long RoundUp(DateTimeOffset time) => time.ToUnixTimeSeconds() + (time.UtcTicks % TimeSpan.TicksPerSecond == 0 ? 0 : 1);

    // At most one unknown-key reread per UnknownKeyRefresh, counted from the attempt, whether or not it succeeds.
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

    // The newest key whose activation has passed signs; if none has, the oldest does. Keys arrive newest first.
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

    // Activation, then creation, then key id: every replica that reads the same keys picks the same signer.
    private static bool Later(SigningKey left, SigningKey right) =>
        left.ActivatesAt != right.ActivatesAt ? left.ActivatesAt > right.ActivatesAt
        : left.CreatedAt != right.CreatedAt ? left.CreatedAt > right.CreatedAt
        : string.CompareOrdinal(left.Id, right.Id) > 0;

    // Binds each box to its key and instance: a box copied to another instance or under another kid fails authentication.
    // Key ids never contain ':', so the context is unambiguous.
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

    // Canonical JSON string-array encoding binds fields without delimiter ambiguities. The private box is deliberately
    // excluded: its failure must not prevent public verification.
    private string ComputePublicMetadataMac(SigningKey key) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(this.publicMetadataKey, JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            key.InstanceId, key.Id, key.Algorithm, key.CreatedAt.ToString(CultureInfo.InvariantCulture),
            key.ActivatesAt.ToString(CultureInfo.InvariantCulture), key.X, key.Y,
        })));

    // The snapshot's keys are validated and their public metadata verified once, newest first.
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

    // Private loading also checks every key against its published coordinates before signing or rotation.
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

    // Keys: every stored key of the instance with its version; Sent: this replica's monotonic clock when the read was sent.
    private sealed record Snapshot(IReadOnlyList<(SigningKey Key, string Version)> Keys, long Sent);

    // One source's reads by instance: the last snapshot, the read in flight and when a read last failed.
    private sealed class Reads(Func<string, CancellationToken, IAsyncEnumerable<TerraBaseDocument>> query)
    {
        public Func<string, CancellationToken, IAsyncEnumerable<TerraBaseDocument>> Query { get; } = query;

        public ConcurrentDictionary<string, Snapshot> Cached { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, Lazy<Task<Snapshot>>> Pending { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, long> Failed { get; } = new(StringComparer.Ordinal);
    }
}
