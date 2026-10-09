using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Security;

namespace TerraScale.Auth.Features.SigningKeys;

/// <summary>
/// How long a replica serves a ring it read before reading the vault again, counted from when the read was sent. One
/// minute in production, where nothing configures it, so every replica uses the same interval.
/// </summary>
public sealed record KeyRingRefresh
{
    /// <summary>
    /// The longest interval: a replica then learns of a scheduled key long before it signs, and the previous key's
    /// last token (at most <see cref="Jwt.MaxLifetime"/>, issued within <see cref="KeyRing.MaxClockSkew"/>) expires
    /// before that key retires.
    /// </summary>
    public static readonly TimeSpan Max = KeyRing.Overlap - Jwt.MaxLifetime - KeyRing.MaxClockSkew - KeyRing.ActivationMargin;

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
/// Per-instance ES256 signing keys, each sealed under the master key, in one OCI Vault secret per instance
/// (<see cref="ISigningKeyVault"/>); every write is a new secret version. Lookups reuse a replica's last read for
/// <see cref="KeyRingRefresh"/> (one minute), which the <see cref="Overlap"/> makes safe; bootstrap and rotation
/// always read the current version. Concurrent first use, bootstrap and rotations resolve through conditional writes,
/// never process locks. Every key is published and validates; the newest key whose activation time has passed signs.
/// A scheduled rotation publishes its key <see cref="Overlap"/> before it signs, and every rotation retires the keys
/// whose successor has been signing for at least <see cref="Overlap"/>.
/// </summary>
public sealed class KeyRing(ISigningKeyVault vault, AuthOptions options, TimeProvider clock, KeyRingRefresh? refresh = null)
{
    /// <summary>
    /// The most keys a ring holds, which bounds the secret and every lookup. A rotation that
    /// would exceed it after retirement is refused (409).
    /// </summary>
    public const int MaxKeys = 16;

    /// <summary>A scheduled rotation adds a key once the newest one is this old.</summary>
    public static readonly TimeSpan RotationAge = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a scheduled key is published before it signs (verifiers and replicas cache the ring for minutes),
    /// and how long a superseded key stays published after its successor starts signing: longer
    /// than <see cref="Jwt.MaxLifetime"/> plus <see cref="MaxClockSkew"/>, so its last token expires first.
    /// </summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromDays(2);

    /// <summary>
    /// How far this replica's wall clock, which timestamps the tokens it signs, may be from the vault's clock, which
    /// activates and retires keys (the usual leeway of JWT validators). Beyond it the replica refuses to sign (503)
    /// and is not ready.
    /// </summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Added to the refresh interval an unscheduled key waits after the vault is seen serving it: OCI's <c>Date</c>
    /// header has whole-second resolution and answers spend time in transit, so a replica's estimate of the vault's
    /// clock can lag by a second or more.
    /// </summary>
    public static readonly TimeSpan ActivationMargin = TimeSpan.FromSeconds(5);

    /// <summary>The least time between rereads of the ring caused by tokens naming a key it lacks.</summary>
    public static readonly TimeSpan UnknownKeyRefresh = TimeSpan.FromSeconds(10);

    /// <summary>After a failed shared read, lookups answer 503 without asking the vault for this long.</summary>
    public static readonly TimeSpan FailedReadBackoff = TimeSpan.FromSeconds(5);

    private const string Algorithm = "ES256";
    private const int MaxAttempts = 8;

    private readonly TimeSpan refreshInterval = refresh?.Interval ?? TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, Snapshot> reads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<Snapshot>>> pendingReads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> failedReads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> unknownKeyReads = new(StringComparer.Ordinal);
    private long readSequence;

    // A snapshot never changes: lookups parse its ring, and verify its public metadata, once.
    private readonly ConditionalWeakTable<Snapshot, SigningKeyRing?> parsedRings = new();
    private readonly ConditionalWeakTable<Snapshot, SigningKeyRecord[]> verifiedPublicKeys = new();

    // "v1" names the public-metadata MAC scheme: this derivation and the fields ComputePublicMetadataMac covers. Bump
    // both strings with any change to it, so rings of the old scheme read as sealed under another master key and fail
    // closed instead of being silently replaced.
    private readonly byte[] publicMetadataKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: "ts-auth signing public metadata v1"u8.ToArray());
    private readonly string masterKeyId = Convert.ToHexStringLower(HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 16, info: "ts-auth signing ring master key id v1"u8.ToArray()));

    /// <summary>
    /// The instance's keys, the signing key first; the instance's first key is created on first
    /// use. The caller owns the returned keys and disposes them.
    /// </summary>
    public async Task<IReadOnlyList<Es256Key>> GetActiveAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await this.ReadAsync(instanceId, fresh: attempt > 0, cancellationToken);
            this.CheckClock(current);
            if (this.Parse(current) is { } ring)
            {
                // Restore lists the newest key first; move the signing key to the front.
                var keys = this.Restore(instanceId, ring);
                var signer = keys.Count - 1 - SignerIndex(ring.Keys, this.Now(current).ToUnixTimeSeconds());
                var signing = keys[signer];
                keys.RemoveAt(signer);
                keys.Insert(0, signing);
                return keys;
            }

            // Another replica may create the ring first; its key then wins.
            if (attempt > 0)
            {
                if (await this.WriteNewKeyAsync(instanceId, current, [], TimeSpan.Zero, cancellationToken) is { } key)
                {
                    return [key];
                }

                await BackoffAsync(attempt, cancellationToken);
            }
        }

        throw ApiException.Unavailable();
    }

    /// <summary>
    /// Loads public verification keys without opening any private-key box. First use creates
    /// one ring conditionally; existing rings must already contain public metadata.
    /// </summary>
    public Task<IReadOnlyList<Es256Key>> GetPublicAsync(string instanceId, CancellationToken cancellationToken) =>
        this.GetPublicAsync(instanceId, keyId: null, cancellationToken);

    /// <summary>
    /// Like <see cref="GetPublicAsync(string, CancellationToken)"/> for verifying a token signed with
    /// <paramref name="keyId"/>: when this replica's cached ring lacks that key, the ring is read again first
    /// (at most every <see cref="UnknownKeyRefresh"/>), so a key another replica just added verifies at once.
    /// </summary>
    public async Task<IReadOnlyList<Es256Key>> GetPublicAsync(string instanceId, string? keyId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await this.ReadAsync(instanceId, fresh: attempt > 0, cancellationToken);
            if (keyId is not null && this.Parse(current) is { } cached && cached.Keys.All(key => key.Id != keyId) && this.ClaimUnknownKeyRead(instanceId))
            {
                current = await this.ReadAsync(instanceId, fresh: false, cancellationToken, TimeSpan.Zero);
            }

            if (this.Parse(current) is { } ring)
            {
                return this.RestorePublic(instanceId, current, ring);
            }

            if (attempt > 0)
            {
                using var created = await this.WriteNewKeyAsync(instanceId, current, [], TimeSpan.Zero, cancellationToken);
                if (created is not null)
                {
                    var publicKey = created.PublicJwk();
                    return [Es256Key.ImportPublic(created.KeyId, publicKey.X, publicKey.Y)];
                }

                await BackoffAsync(attempt, cancellationToken);
            }
        }

        throw ApiException.Unavailable();
    }

    /// <summary>
    /// Validates the ring's public metadata as the vault holds it now, bypassing the refresh interval but sharing an
    /// in-flight read, and that this replica's clock is within <see cref="MaxClockSkew"/> of the vault's; never
    /// creates or changes state. A missing ring fails.
    /// </summary>
    public async Task CheckPublicMetadataAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var current = await this.ReadAsync(instanceId, fresh: false, cancellationToken, TimeSpan.Zero);
        this.CheckClock(current);
        var ring = this.Parse(current) ?? throw new InvalidOperationException("The signing key ring is missing.");
        this.RestorePublic(instanceId, current, ring).ForEach(key => key.Dispose());
    }

    /// <summary>
    /// Startup bootstrap: writes a fresh one-key ring when the secret holds none, a ring that does not
    /// parse, or a ring of this master key whose keys cannot all be verified and opened. Tokens signed by
    /// replaced keys stop validating. A ring sealed under another master key is never replaced: that is a
    /// misconfigured replica or a key change, and fails until an operator writes <c>{}</c> to the secret.
    /// Returns whether it wrote; vault failures surface as 503 and leave the secret unchanged.
    /// </summary>
    public async Task<bool> EnsureAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await this.ReadAsync(instanceId, fresh: true, cancellationToken);
            switch (this.Inspect(instanceId, current.Content))
            {
                case Stored.Usable:
                    return false;
                case Stored.Foreign:
                    throw new InvalidOperationException("The signing key ring was sealed under another master key; write {} to the secret to replace it.");
            }

            using var key = await this.WriteNewKeyAsync(instanceId, current, [], TimeSpan.Zero, cancellationToken);
            if (key is not null)
            {
                return true;
            }

            // Another replica wrote first, or its write is still being applied: check its ring.
            await BackoffAsync(attempt, cancellationToken);
        }

        throw ApiException.Unavailable();
    }

    /// <summary>
    /// Adds a key and retires keys superseded for at least <see cref="Overlap"/>. The key is published at once and
    /// first scheduled to sign after <see cref="Overlap"/>, like a scheduled one. Once the vault is seen serving it,
    /// its activation is brought forward to a refresh interval (<see cref="KeyRingRefresh"/>) plus
    /// <see cref="ActivationMargin"/> after that, when every replica's cached ring includes it; if that publication
    /// cannot be confirmed, the key keeps its scheduled activation. Replicas that cache nothing sign with it at once.
    /// The caller owns the returned key and disposes it.
    /// </summary>
    public async Task<Es256Key> RotateAsync(string instanceId, CancellationToken cancellationToken)
    {
        var (key, secret) = await this.RotateAsync(instanceId, scheduled: false, cancellationToken) ?? throw new InvalidOperationException("An unscheduled rotation always adds a key.");
        try
        {
            if (this.refreshInterval > TimeSpan.Zero)
            {
                await this.ExpediteAsync(instanceId, secret, key.KeyId, cancellationToken);
            }
        }
        catch (ApiException error) when (error.Status == 503)
        {
            // The vault failed: the key keeps its scheduled activation.
        }
        catch
        {
            key.Dispose();
            throw;
        }

        return key;
    }

    /// <summary>
    /// The scheduled rotation: once the newest key is <see cref="RotationAge"/> old, adds a key that
    /// signs after <see cref="Overlap"/> and retires keys superseded for at least <see cref="Overlap"/>.
    /// Concurrent replicas add one key between them. Returns whether it added a key.
    /// </summary>
    public async Task<bool> RotateIfDueAsync(string instanceId, CancellationToken cancellationToken)
    {
        using var key = (await this.RotateAsync(instanceId, scheduled: true, cancellationToken))?.Key;
        return key is not null;
    }

    // The complete original ring is restored first, so a rotation never writes a ring that cannot be read. Returns the
    // added key and the secret it was written to.
    private async Task<(Es256Key Key, string Secret)?> RotateAsync(string instanceId, bool scheduled, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await this.ReadAsync(instanceId, fresh: true, cancellationToken);
            if (this.Parse(current) is not { } ring)
            {
                if (await this.WriteNewKeyAsync(instanceId, current, [], TimeSpan.Zero, cancellationToken) is { } first)
                {
                    return (first, current.Secret);
                }

                await BackoffAsync(attempt, cancellationToken);
                continue;
            }

            this.Restore(instanceId, ring).ForEach(restored => restored.Dispose());
            var keys = ring.Keys;
            var now = this.Now(current).ToUnixTimeSeconds();
            if (scheduled && now < keys[^1].CreatedAt + (long)RotationAge.TotalSeconds)
            {
                return null;
            }

            // The vault's clock, not this replica's, times activation and retirement. Never retires the
            // newest key or one whose successor is not yet active.
            var kept = keys.SkipWhile((_, index) => index < keys.Count - 1 && keys[index + 1].ActivatesAt + (long)Overlap.TotalSeconds <= now).ToList();
            if (kept.Count >= MaxKeys)
            {
                throw new ApiException(409, "signing_key_limit", "The signing key ring is full until its superseded keys pass the overlap.");
            }

            // Replicas that cache the ring learn of a key only once the vault serves it: until an unscheduled key's
            // publication is confirmed (ExpediteAsync), it is scheduled like any other.
            var lead = scheduled || this.refreshInterval > TimeSpan.Zero ? Overlap : TimeSpan.Zero;
            var key = await this.WriteNewKeyAsync(instanceId, current, kept, lead, cancellationToken);
            if (key is not null)
            {
                return (key, current.Secret);
            }

            // The ring changed since it was read: retry on the newer version, losing no key.
            await BackoffAsync(attempt, cancellationToken);
        }

        throw ApiException.Unavailable();
    }

    // A fresh read returns the version token a conditional write needs. Otherwise the ring as this replica last read
    // it, rereading once that read is older than the refresh interval or maxAge (measured monotonically); concurrent
    // lookups on this replica share one vault read, which ends only when the vault answers, never with a waiter.
    private async Task<Snapshot> ReadAsync(string instanceId, bool fresh, CancellationToken cancellationToken, TimeSpan? maxAge = null)
    {
        if (fresh)
        {
            var (started, sent) = (Interlocked.Increment(ref this.readSequence), clock.GetTimestamp());
            return this.Publish(instanceId, this.Observe(await vault.ReadAsync(instanceId, writable: true, cancellationToken).ConfigureAwait(false), started, sent));
        }

        // A read's age counts from when it was sent: one that returned a superseded ring was sent before the newer
        // version was published, however long its answer took.
        var limit = maxAge is { } age && age < this.refreshInterval ? age : this.refreshInterval;
        if (this.reads.TryGetValue(instanceId, out var cached) && clock.GetElapsedTime(cached.Sent) < limit)
        {
            return cached;
        }

        // Fail closed without asking the vault again right after a failed read: traffic never multiplies an outage.
        if (this.failedReads.TryGetValue(instanceId, out var failedAt) && clock.GetElapsedTime(failedAt) < FailedReadBackoff)
        {
            throw ApiException.Unavailable();
        }

        Lazy<Task<Snapshot>>? created = null;
        created = new Lazy<Task<Snapshot>>(async () =>
        {
            try
            {
                var (started, sent) = (Interlocked.Increment(ref this.readSequence), clock.GetTimestamp());
                var read = this.Publish(instanceId, this.Observe(await vault.ReadAsync(instanceId, writable: false, CancellationToken.None).ConfigureAwait(false), started, sent));
                this.failedReads.TryRemove(instanceId, out _);
                return read;
            }
            catch
            {
                this.failedReads[instanceId] = clock.GetTimestamp();
                throw;
            }
            finally
            {
                this.pendingReads.TryRemove(KeyValuePair.Create(instanceId, created!));
            }
        });
        var shared = this.pendingReads.GetOrAdd(instanceId, created);
        return await shared.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Confirms, as verifiers read it, that the vault serves the ring holding the key, then brings the key's activation
    // forward to the refresh interval plus ActivationMargin after that answer (by the vault's clock, rounded up): any
    // replica's cached ring that lacks the key was read before and expires by then. Gives up, leaving the scheduled
    // activation, if the secret was replaced or publication cannot be seen within the attempts.
    private async Task ExpediteAsync(string instanceId, string secret, string keyId, CancellationToken cancellationToken)
    {
        DateTimeOffset? published = null;
        for (var attempt = 0; attempt < MaxAttempts && published is null; attempt++)
        {
            var read = await vault.ReadAsync(instanceId, writable: false, cancellationToken).ConfigureAwait(false);
            if (read.Secret != secret)
            {
                return;
            }

            if (Holds(read.Content, keyId))
            {
                published = read.ServerTime;
            }
            else
            {
                await BackoffAsync(attempt, cancellationToken);
            }
        }

        if (published is not { } servedAt)
        {
            return;
        }

        var activatesAt = RoundUp(servedAt + this.refreshInterval + ActivationMargin);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await this.ReadAsync(instanceId, fresh: true, cancellationToken);
            if (current.Secret != secret || this.Parse(current) is not { } ring || ring.Keys.FirstOrDefault(key => key.Id == keyId) is not { } record
                || record.ActivatesAt <= activatesAt)
            {
                return;
            }

            // Every key is verified first, so moving one activation never re-authenticates altered metadata.
            this.Restore(instanceId, ring).ForEach(restored => restored.Dispose());
            if (current.Version is { } version)
            {
                var content = JsonSerializer.SerializeToUtf8Bytes(ring with
                {
                    Keys = [.. ring.Keys.Select(key => key.Id == keyId ? this.AuthenticatePublicMetadata(instanceId, key with { ActivatesAt = activatesAt }) : key)],
                });
                if (await vault.TryWriteAsync(instanceId, current.Secret, content, version, cancellationToken).ConfigureAwait(false))
                {
                    _ = this.Publish(instanceId, current with { Content = content, Version = null, Number = current.Number + 1 });
                    return;
                }
            }

            await BackoffAsync(attempt, cancellationToken);
        }
    }

    private static bool Holds(byte[] content, string keyId)
    {
        try
        {
            return JsonSerializer.Deserialize<SigningKeyRing>(content)?.Keys.Any(key => key.Id == keyId) == true;
        }
        catch (JsonException)
        {
            return false;
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

    // Anchors the vault's clock at receipt to this replica's monotonic clock, so estimates of it only ever lag.
    private Snapshot Observe(VaultVersion read, long started, long sent) => new(read.Content, read.Version, read.Secret, read.Number, read.ServerTime, clock.GetTimestamp(), started, sent);

    // Version numbers order the reads and writes of one secret: a read that started before a newer version was read or
    // written never replaces it in the cache. A recreated secret's numbers start again, so across secrets the read
    // that started later wins: a delayed read or a write of the replaced secret never displaces the replacement.
    // Returns what the cache holds afterwards, so a read that lost answers with the ring that superseded it.
    private Snapshot Publish(string instanceId, Snapshot snapshot) =>
        this.reads.AddOrUpdate(instanceId, snapshot, (_, cached) =>
            (cached.Secret == snapshot.Secret ? snapshot.Number >= cached.Number : snapshot.Started > cached.Started) ? snapshot : cached);

    // Tokens carry this replica's wall-clock timestamps while keys activate and retire by the vault's clock: past
    // MaxClockSkew a token signed before its key's successor activates could outlive the key's retirement.
    private void CheckClock(Snapshot snapshot)
    {
        if ((clock.GetUtcNow() - this.Now(snapshot)).Duration() > MaxClockSkew)
        {
            throw ApiException.Unavailable(new InvalidOperationException("This replica's clock is more than KeyRing.MaxClockSkew from the vault's."));
        }
    }

    // OCI applies a new version asynchronously: after a lost write, wait with jitter (0.05 s doubling to 2 s) before rereading.
    private static Task BackoffAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, 100 << attempt) * (0.5 + (Random.Shared.NextDouble() / 2))), cancellationToken);

    // Null for a secret that holds no ring yet (an empty JSON object). A malformed ring fails closed with 503.
    private SigningKeyRing? Parse(Snapshot snapshot) => this.parsedRings.GetValue(snapshot, ParseContent);

    private static SigningKeyRing? ParseContent(Snapshot snapshot)
    {
        try
        {
            using var document = JsonDocument.Parse(snapshot.Content);
            if (document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.EnumerateObject().Any())
            {
                return null;
            }

            return document.RootElement.Deserialize<SigningKeyRing>() ?? throw new JsonException("The signing key ring is empty.");
        }
        catch (JsonException error)
        {
            throw ApiException.Unavailable(error);
        }
    }

    // No I/O. An empty secret, or anything that is not a ring, is replaceable; so is a ring of this master key that
    // fails to restore. A ring naming another master key is not, even with damaged key records: its sealing master
    // key is read before anything else.
    private Stored Inspect(string instanceId, byte[] content)
    {
        SigningKeyRing ring;
        try
        {
            using (var document = JsonDocument.Parse(content))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("masterKeyId", out var sealer)
                    && sealer.ValueKind == JsonValueKind.String && !Tokens.FixedTimeEquals(sealer.GetString()!, this.masterKeyId))
                {
                    return Stored.Foreign;
                }
            }

            ring = JsonSerializer.Deserialize<SigningKeyRing>(content) ?? throw new JsonException("The signing key ring is empty.");
        }
        catch (JsonException)
        {
            return Stored.Replaceable;
        }

        if (!Tokens.FixedTimeEquals(ring.MasterKeyId ?? string.Empty, this.masterKeyId))
        {
            return Stored.Foreign;
        }

        try
        {
            this.Restore(instanceId, ring).ForEach(key => key.Dispose());
            return Stored.Usable;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Stored.Replaceable;
        }
    }

    // The vault's clock at the snapshot's read plus the monotonic time since: replica clock skew and wall-clock steps
    // never time keys.
    private DateTimeOffset Now(Snapshot snapshot) => snapshot.ServerTime + clock.GetElapsedTime(snapshot.Timestamp);

    // The newest key whose activation has passed signs; if none has, the oldest does.
    private static int SignerIndex(IReadOnlyList<SigningKeyRecord> keys, long now)
    {
        var index = keys.Count - 1;
        while (index > 0 && keys[index].ActivatesAt > now)
        {
            index--;
        }

        return index;
    }

    // Generates a key that signs after lead, appends it to the kept records and writes the ring as a new version
    // of the one read. Returns the key only if the write committed; otherwise, or if anything throws, the key is disposed.
    private async Task<Es256Key?> WriteNewKeyAsync(string instanceId, Snapshot current, List<SigningKeyRecord> kept, TimeSpan lead, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (current.Version is not { } version)
        {
            // Another write is still being applied; the caller rereads.
            return null;
        }

        // Keep creation times strictly ordered across replicas. Refuse if no later valid Unix
        // timestamp can be assigned.
        var now = this.Now(current);
        var createdAt = Math.Max(now.ToUnixTimeSeconds(), kept.Count == 0 ? long.MinValue : checked(kept[^1].CreatedAt + 1));
        // A positive lead is rounded up to the second: the key never signs before it has fully passed.
        var activatesAt = lead > TimeSpan.Zero ? RoundUp(now + lead) : now.ToUnixTimeSeconds();
        _ = DateTimeOffset.FromUnixTimeSeconds(createdAt);
        _ = DateTimeOffset.FromUnixTimeSeconds(activatesAt);
        var key = Es256Key.Generate(NewKeyId());
        try
        {
            var content = JsonSerializer.SerializeToUtf8Bytes(new SigningKeyRing(instanceId, this.masterKeyId, [.. kept, this.Seal(instanceId, key, createdAt, activatesAt)]));
            if (await vault.TryWriteAsync(instanceId, current.Secret, content, version, cancellationToken).ConfigureAwait(false))
            {
                // This replica serves what it wrote, ordered as the read it wrote on; the next fresh read learns the
                // new version token.
                _ = this.Publish(instanceId, current with { Content = content, Version = null, Number = current.Number + 1 });
                return key;
            }
        }
        catch
        {
            key.Dispose();
            throw;
        }

        key.Dispose();
        return null;
    }

    private static string NewKeyId() => $"skey_{Tokens.RandomToken(40)}";

    // Binds each box to its key and instance: a box copied into another ring or under another
    // kid fails authentication. Key ids never contain ':', so the context is unambiguous.
    private static string Context(string instanceId, string keyId) => $"signing-key:{keyId}:{instanceId}";

    private SigningKeyRecord Seal(string instanceId, Es256Key key, long createdAt, long activatesAt)
    {
        var secret = key.ExportSecret();
        try
        {
            var publicKey = key.PublicJwk();
            return this.AuthenticatePublicMetadata(instanceId, new SigningKeyRecord(key.KeyId, Algorithm, SecretBox.Seal(options.MasterKey, secret, Context(instanceId, key.KeyId)), createdAt, activatesAt, publicKey.X, publicKey.Y, string.Empty));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private void ValidateRing(string instanceId, SigningKeyRing ring)
    {
        // Validate the original count and ordering before importing any key or changing state.
        if (ring.InstanceId != instanceId || ring.Keys is not { Count: > 0 and <= MaxKeys })
        {
            throw new CryptographicException("The signing key ring has an invalid owner or key count.");
        }

        if (!Tokens.FixedTimeEquals(ring.MasterKeyId ?? string.Empty, this.masterKeyId))
        {
            throw new CryptographicException("The signing key ring was sealed under another master key.");
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < ring.Keys.Count; index++)
        {
            var record = ring.Keys[index];
            if (record is null || record.Algorithm != Algorithm || string.IsNullOrEmpty(record.Id) || record.Id.Contains(':', StringComparison.Ordinal)
                || !identifiers.Add(record.Id) || (index > 0 && record.CreatedAt <= ring.Keys[index - 1].CreatedAt))
            {
                throw new CryptographicException("The signing key ring has invalid key identities or creation order.");
            }

            _ = DateTimeOffset.FromUnixTimeSeconds(record.CreatedAt);
            _ = DateTimeOffset.FromUnixTimeSeconds(record.ActivatesAt);
        }
    }

    private SigningKeyRecord AuthenticatePublicMetadata(string instanceId, SigningKeyRecord record) =>
        record with { PublicMetadataMac = this.ComputePublicMetadataMac(instanceId, record) };

    // Canonical JSON string-array encoding binds fields without delimiter ambiguities. The
    // private box is deliberately excluded: its failure must not prevent public verification.
    private string ComputePublicMetadataMac(string instanceId, SigningKeyRecord record) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(this.publicMetadataKey, JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            instanceId, record.Id, record.Algorithm, record.CreatedAt.ToString(CultureInfo.InvariantCulture),
            record.ActivatesAt.ToString(CultureInfo.InvariantCulture), record.X, record.Y,
        })));

    private void VerifyPublicMetadata(string instanceId, SigningKeyRecord record)
    {
        if (record.PublicMetadataMac is null || !Tokens.FixedTimeEquals(this.ComputePublicMetadataMac(instanceId, record), record.PublicMetadataMac))
        {
            throw new CryptographicException("The signing key's public metadata is not authentic.");
        }
    }

    // The snapshot's ring is validated and its public metadata verified once; every caller gets its own keys.
    private List<Es256Key> RestorePublic(string instanceId, Snapshot snapshot, SigningKeyRing ring)
    {
        var records = this.verifiedPublicKeys.GetValue(snapshot, _ =>
        {
            this.ValidateRing(instanceId, ring);
            var newestFirst = ring.Keys.Reverse().ToArray();
            Array.ForEach(newestFirst, record => this.VerifyPublicMetadata(instanceId, record));
            return newestFirst;
        });
        var keys = new List<Es256Key>(records.Length);
        try
        {
            foreach (var record in records)
            {
                keys.Add(Es256Key.ImportPublic(record.Id, record.X, record.Y));
            }

            return keys;
        }
        catch
        {
            keys.ForEach(key => key.Dispose());
            throw;
        }
    }

    // Private loading still checks every key and its published coordinates before signing or rotation.
    private List<Es256Key> Restore(string instanceId, SigningKeyRing ring)
    {
        this.ValidateRing(instanceId, ring);
        var keys = new List<Es256Key>(ring.Keys.Count);
        try
        {
            foreach (var record in ring.Keys.Reverse())
            {
                this.VerifyPublicMetadata(instanceId, record);
                var secret = SecretBox.Open(options.MasterKey, record.SecretBox, Context(instanceId, record.Id));
                try
                {
                    var key = Es256Key.Import(record.Id, secret);
                    keys.Add(key);
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

            return keys;
        }
        catch
        {
            keys.ForEach(key => key.Dispose());
            throw;
        }
    }

    private enum Stored
    {
        Usable,
        Replaceable,
        Foreign,
    }

    // ServerTime: the vault's clock at the read; Timestamp: this replica's monotonic clock when the answer arrived;
    // Started: the order in which this replica started the read; Sent: its monotonic clock when the read was sent.
    private sealed record Snapshot(byte[] Content, string? Version, string Secret, long Number, DateTimeOffset ServerTime, long Timestamp, long Started, long Sent);
}
