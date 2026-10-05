using System.Security.Cryptography;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Security;
using TerraScale.Auth.Store;

namespace TerraScale.Auth.Features.SigningKeys;

/// <summary>
/// Per-instance ES256 signing keys, sealed under the master key in one TerraBase document per
/// instance (the <c>ring</c> key of the instance in <see cref="Collection"/>). Every lookup rereads storage, so all replicas see a rotation at once. Concurrent
/// first use and concurrent rotations resolve through conditional writes, never process locks.
/// Rotation retains every key: storage exposes no authoritative supersession time, and replica
/// wall clocks cannot prove that its tokens have expired. The ring stays bounded by refusing
/// further rotation at <see cref="MaxKeys"/>.
/// </summary>
public sealed class KeyRing(IDocumentStore storage, AuthOptions options, TimeProvider clock)
{
    /// <summary>The TerraBase collection holding one <see cref="SigningKeyRing"/> per instance.</summary>
    public const string Collection = "signing_key_rings";

    /// <summary>
    /// The most keys a ring holds, which bounds the document and every lookup. A rotation that
    /// would exceed it is refused (409). Retirement requires an approved trustworthy-time
    /// design; keys are never dropped based on replica wall time.
    /// </summary>
    public const int MaxKeys = 16;

    private const string Algorithm = "ES256";
    private const string RingKey = "ring";
    private const int MaxAttempts = 8;

    private readonly FailClosedDocumentStore store = new(storage);

    /// <summary>
    /// The instance's keys, the signing key first; the instance's first key is created on first
    /// use. The caller owns the returned keys and disposes them.
    /// </summary>
    public async Task<IReadOnlyList<Es256Key>> GetActiveAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (await this.store.GetAsync<SigningKeyRing>(Collection, instanceId, RingKey, cancellationToken) is { } ring)
            {
                return this.Restore(instanceId, ring.Document);
            }

            // Another replica may create the ring first; its key then wins.
            if (await this.WriteNewKeyAsync(instanceId, keys => this.store.TryCreateAsync(Collection, instanceId, RingKey, new SigningKeyRing(keys), cancellationToken), [], cancellationToken) is { } key)
            {
                return [key];
            }
        }

        throw ApiException.Unavailable();
    }

    /// <summary>
    /// Adds a new signing key and retains all older keys. The complete original ring is restored
    /// first, so a rotation never writes a ring that cannot be read. The caller owns the returned
    /// key and disposes it.
    /// </summary>
    public async Task<Es256Key> RotateAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await this.store.GetAsync<SigningKeyRing>(Collection, instanceId, RingKey, cancellationToken);
            if (current is null)
            {
                if (await this.WriteNewKeyAsync(instanceId, keys => this.store.TryCreateAsync(Collection, instanceId, RingKey, new SigningKeyRing(keys), cancellationToken), [], cancellationToken) is { } first)
                {
                    return first;
                }

                continue;
            }

            this.Restore(instanceId, current.Document).ForEach(restored => restored.Dispose());
            // ponytail: no authoritative supersession time; retain keys and refuse at the
            // bound until an approved trustworthy-time retirement design is available.
            var kept = current.Document.Keys;
            if (kept.Count >= MaxKeys)
            {
                throw new ApiException(409, "signing_key_limit", "The signing key ring is full; safe key retirement requires an approved trustworthy-time design.");
            }

            var key = await this.WriteNewKeyAsync(instanceId, keys => this.store.TryReplaceAsync(Collection, instanceId, RingKey, new SigningKeyRing(keys), current.Version, cancellationToken), kept, cancellationToken);
            if (key is not null)
            {
                return key;
            }

            // The ring changed since it was read: retry on the newer version, losing no key.
        }

        throw ApiException.Unavailable();
    }

    // Generates a key, appends it to the kept records and writes the ring. Returns the key only
    // if the write committed; otherwise, or if anything throws, the key is disposed.
    private async Task<Es256Key?> WriteNewKeyAsync(string instanceId, Func<IReadOnlyList<SigningKeyRecord>, Task<bool>> write, IReadOnlyList<SigningKeyRecord> kept, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Keep creation times strictly ordered across replicas; this ordering is not evidence
        // that a key is safe to retire. Refuse if no later valid Unix timestamp can be assigned.
        var createdAt = Math.Max(clock.GetUtcNow().ToUnixTimeSeconds(), kept.Count == 0 ? long.MinValue : checked(kept[^1].CreatedAt + 1));
        _ = DateTimeOffset.FromUnixTimeSeconds(createdAt);
        var key = Es256Key.Generate(NewKeyId());
        try
        {
            if (await write([.. kept, this.Seal(instanceId, key, createdAt)]))
            {
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

    private SigningKeyRecord Seal(string instanceId, Es256Key key, long createdAt)
    {
        var secret = key.ExportSecret();
        try
        {
            return new SigningKeyRecord(key.KeyId, Algorithm, SecretBox.Seal(options.MasterKey, secret, Context(instanceId, key.KeyId)), createdAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    // A ring that cannot be fully restored fails the request: signing with a partial ring could
    // drop a key that still validates live tokens.
    private List<Es256Key> Restore(string instanceId, SigningKeyRing ring)
    {
        // Validate the original count and ordering before importing any key or changing state.
        if (ring.Keys is not { Count: > 0 and <= MaxKeys })
        {
            throw new CryptographicException("The signing key ring is empty or holds more keys than allowed.");
        }

        for (var index = 0; index < ring.Keys.Count; index++)
        {
            var record = ring.Keys[index];
            if (record is null || (index > 0 && record.CreatedAt <= ring.Keys[index - 1].CreatedAt))
            {
                throw new CryptographicException("The signing key ring is not in strict creation order.");
            }

            _ = DateTimeOffset.FromUnixTimeSeconds(record.CreatedAt);
        }

        var keys = new List<Es256Key>(ring.Keys.Count);
        try
        {
            foreach (var record in ring.Keys.Reverse())
            {
                if (record.Algorithm != Algorithm || string.IsNullOrEmpty(record.Id) || record.Id.Contains(':', StringComparison.Ordinal))
                {
                    throw new CryptographicException("Unsupported persisted signing key.");
                }

                var secret = SecretBox.Open(options.MasterKey, record.SecretBox, Context(instanceId, record.Id));
                try
                {
                    keys.Add(Es256Key.Import(record.Id, secret));
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
}
